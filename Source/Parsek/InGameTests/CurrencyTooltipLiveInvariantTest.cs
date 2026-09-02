using System.Globalization;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Live proof for RESERVATION-OVERLAY-GAPS (b): the funds and science hover tooltips
    /// must reconcile with the number on the currency bar. The pure builder is
    /// unit-tested (<c>CurrencyReservationOverlayTests</c>); what only a live career can
    /// prove is that the THREE inputs the overlay reads - the ledger's running balance,
    /// its floored availability, its unclamped projected minimum - and the value
    /// <c>KspStatePatcher</c> wrote onto the bar agree with each other after a recalc,
    /// including the drawdown-guard case where the bar is held above the ledger.
    /// </summary>
    public class CurrencyTooltipLiveInvariantTest
    {
        private const string Tag = "LedgerGroundTruth";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // RecalculateAndPatch writes the live Funding / R&D singletons, and the sibling
        // ground-truth cell in this batch hard-asserts the seeded pools against a
        // quicksave; restoring the flight baseline afterwards keeps the pair
        // order-independent (discovery order is not pinned).
        [InGameTest(Category = "LedgerGroundTruth", Scene = GameScenes.FLIGHT,
            RestoreBatchFlightBaselineAfterExecution = true,
            Description = "Currency tooltip reconciles with the live bar: after RecalculateAndPatch the funds "
              + "and science tooltips' Total - Reserved equals the value on the stock widget (bar-anchored "
              + "form), or, when the bar is floored at zero under a negative ledger balance, the deficit "
              + "form names the signed balance. Career-only; skips if a live/pending tree would defer patching.")]
        public void TooltipsReconcileWithTheLiveBar()
        {
            if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.CAREER)
            {
                InGameAssert.Skip("Currency tooltip reconciliation is career-only");
                return;
            }
            if (Funding.Instance == null || ResearchAndDevelopment.Instance == null)
            {
                InGameAssert.Skip("Funding / R&D singletons are not initialized");
                return;
            }
            if (RecordingStore.HasPendingTree
                || GameStateRecorder.HasActiveUncommittedTree()
                || GameStateRecorder.HasLiveRecorder())
            {
                InGameAssert.Skip("RecalculateAndPatch would defer KSP singleton patching while a live/pending tree exists");
                return;
            }
            var funds = LedgerOrchestrator.Funds;
            var science = LedgerOrchestrator.Science;
            if (funds == null || science == null)
            {
                InGameAssert.Skip("Ledger modules are not installed");
                return;
            }

            // The bar is whatever the LAST patch wrote; recalc so the modules and the bar
            // describe the same walk.
            LedgerOrchestrator.RecalculateAndPatch();

            // Total and Reserved are parsed back out of the RENDERED tooltip ("N0" /
            // "F1"), so each carries up to half a display unit of rounding against the
            // raw bar; the tolerance is one full unit of the format, not half.
            AssertReconciles("funds",
                funds.GetProjectionCurrentBalance(), funds.GetAvailableFunds(),
                funds.GetProjectionMinBalance(), Funding.Instance.Funds,
                CurrencyReservationOverlay.GetFundsTooltip(), tolerance: 1.0);
            AssertReconciles("science",
                science.GetProjectionCurrentBalance(), science.GetAvailableScience(),
                science.GetProjectionMinBalance(), ResearchAndDevelopment.Instance.Science,
                CurrencyReservationOverlay.GetScienceTooltip(), tolerance: 0.1);
        }

        private static void AssertReconciles(
            string pool, double running, double available, double minProjected, double bar,
            string tooltip, double tolerance)
        {
            InGameAssert.IsNotNull(tooltip, $"{pool} tooltip must render while the singletons exist");
            string expected = CurrencyReservationOverlay.BuildTooltipFromLedger(
                running, available, minProjected, bar, pool == "funds" ? "N0" : "F1");
            InGameAssert.AreEqual(expected, tooltip,
                $"{pool} tooltip must be the ledger derivation of the live inputs " +
                $"(running={running.ToString("R", IC)} available={available.ToString("R", IC)} " +
                $"minProjected={minProjected.ToString("R", IC)} bar={bar.ToString("R", IC)})");

            bool deficitForm = tooltip.StartsWith("Balance:", System.StringComparison.Ordinal);
            if (deficitForm)
            {
                // Only reachable with the bar floored under a negative ledger balance.
                InGameAssert.IsTrue(bar <= tolerance && running < 0.0,
                    $"{pool} deficit form rendered while bar={bar.ToString("R", IC)} running={running.ToString("R", IC)}");
            }
            else
            {
                // Total - Reserved is the on-screen number, by construction; a Short by line
                // may follow and must carry the projection's own overdraw.
                InGameAssert.IsTrue(
                    TryParseTotalAndReserved(tooltip, out double total, out double reserved),
                    $"{pool} tooltip did not parse as Total / Reserved: {tooltip.Replace("\n", " | ")}");
                InGameAssert.IsTrue(System.Math.Abs((total - reserved) - bar) <= tolerance,
                    $"{pool} Total - Reserved = {(total - reserved).ToString("R", IC)} must equal bar={bar.ToString("R", IC)}");
                InGameAssert.AreEqual(minProjected < 0.0, tooltip.Contains("Short by:"),
                    $"{pool} Short by line presence must follow the sign of minProjected={minProjected.ToString("R", IC)}");
            }

            ParsekLog.Info(Tag,
                $"CurrencyTooltipLive: pool={pool} running={running.ToString("R", IC)} " +
                $"available={available.ToString("R", IC)} minProjected={minProjected.ToString("R", IC)} " +
                $"bar={bar.ToString("R", IC)} form={(deficitForm ? "deficit" : "bar-anchored")} " +
                $"tooltip='{tooltip.Replace("\n", " | ")}'");
        }

        /// <summary>
        /// Parses the first two lines ("Total: N" / "Reserved: N") of the bar-anchored
        /// form back to numbers, tolerating the thousands separators the N0 format adds.
        /// </summary>
        internal static bool TryParseTotalAndReserved(string tooltip, out double total, out double reserved)
        {
            total = 0.0;
            reserved = 0.0;
            if (string.IsNullOrEmpty(tooltip)) return false;
            string[] lines = tooltip.Split('\n');
            if (lines.Length < 2) return false;
            return TryParseLabelled(lines[0], "Total: ", out total)
                && TryParseLabelled(lines[1], "Reserved: ", out reserved);
        }

        private static bool TryParseLabelled(string line, string label, out double value)
        {
            value = 0.0;
            if (line == null || !line.StartsWith(label, System.StringComparison.Ordinal)) return false;
            string number = line.Substring(label.Length).Replace(",", string.Empty);
            return double.TryParse(number, NumberStyles.Float, IC, out value);
        }
    }
}
