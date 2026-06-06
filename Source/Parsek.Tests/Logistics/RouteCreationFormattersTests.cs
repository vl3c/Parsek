using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pure formatter tests. Locale-stability is checked by flipping
    /// the thread culture to a comma-decimal culture (de-DE) and
    /// asserting the output stays InvariantCulture-formatted.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCreationFormattersTests : IDisposable
    {
        private readonly CultureInfo originalCulture;

        public RouteCreationFormattersTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
        }

        // -----------------------------------------------------------------
        // Resource / inventory / endpoint
        // -----------------------------------------------------------------

        [Fact]
        public void FormatResourceLine_UsesInvariantCulture()
        {
            // catches: dropping the InvariantCulture format and slipping back
            // into thread-culture formatting. On de-DE the "150.0" would
            // render as "150,0", which leaks into the player-facing summary
            // and breaks any downstream parser that scans the dialog text.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string line = RouteCreationFormatters.FormatResourceLine("LiquidFuel", 150.0);
            Assert.Equal("LiquidFuel: 150.0", line);
        }

        [Fact]
        public void FormatInventoryLine_QuantityOne_OmitsMultiplier()
        {
            // catches: a regression that always appends "x1" for single-item
            // payloads, which would clutter the dialog summary and split the
            // visual hierarchy between single and multi-quantity rows.
            InventoryPayloadItem item = new InventoryPayloadItem
            {
                PartName = "evaJetpack",
                Quantity = 1
            };
            Assert.Equal("evaJetpack", RouteCreationFormatters.FormatInventoryLine(item));
        }

        [Fact]
        public void FormatInventoryLine_WithVariant_IncludesVariantInParens()
        {
            // catches: variant name being silently dropped or the quantity
            // multiplier formatting drifting. Either failure makes two
            // visually-distinct payloads collapse to the same dialog line and
            // hides part-variant differences from the player.
            InventoryPayloadItem item = new InventoryPayloadItem
            {
                PartName = "evaJetpack",
                VariantName = "white",
                Quantity = 2
            };
            Assert.Equal("evaJetpack (white) x2",
                RouteCreationFormatters.FormatInventoryLine(item));
        }

        [Fact]
        public void FormatEndpoint_RoundsCoordinatesToThreeDecimals()
        {
            // catches: rounding precision drifting (e.g. F2/F4 instead of F3)
            // or the unit suffixes changing. The dialog summary uses the
            // exact-shape string; an F4 lat would push the body name off the
            // single-line summary on narrow screens.
            RouteEndpoint ep = new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 12.3456789,
                Longitude = -45.6789012,
                Altitude = 612.5
            };
            string s = RouteCreationFormatters.FormatEndpoint(ep);
            Assert.Equal("Mun (12.346°, -45.679°, 613m)", s);
        }

        // -----------------------------------------------------------------
        // Reject messages
        // -----------------------------------------------------------------

        [Fact]
        public void FormatRejectMessage_AllEnumValuesProduceNonEmptyText()
        {
            // catches: a new RouteAnalysisStatus value being added without a
            // matching reject-message branch. The default fallback would
            // render an empty string in the dialog, leaving the player with
            // no explanation for why the route is not eligible.
            foreach (RouteAnalysisStatus status in
                Enum.GetValues(typeof(RouteAnalysisStatus)))
            {
                if (status == RouteAnalysisStatus.Eligible) continue;
                string msg = RouteCreationFormatters.FormatRejectMessage(status);
                Assert.False(string.IsNullOrEmpty(msg),
                    $"Reject message for {status} should be non-empty");
            }
        }

        [Fact]
        public void FormatRejectMessage_MixedPickupDelivery_ExplainsCauseAndFix()
        {
            // catches: the MixedPickupDelivery copy regressing to the old terse
            // "must be one-way in v1" line that did not map to the common
            // destination-full playtest case. The message must (1) state plainly
            // that the transport ended the run with more of a resource than it
            // started (it picked up rather than only delivered) and (2) give the
            // actionable fix (re-record, transfer back out before undocking, or
            // disable transport-tank flow before docking).
            string msg = RouteCreationFormatters.FormatRejectMessage(
                RouteAnalysisStatus.MixedPickupDelivery);

            // (1) plain-language cause
            Assert.Contains("more of a resource than it started", msg);
            Assert.Contains("picked the resource up", msg);
            // one-way contract still stated
            Assert.Contains("one-way", msg);
            // (2) actionable fix: re-record without taking from the destination,
            // and the two destination-full workarounds.
            Assert.Contains("Re-record", msg);
            Assert.Contains("transfer that resource back out before undocking", msg);
            Assert.Contains("disable flow", msg);

            // copy guardrail: plain ASCII only (project hard rule, no em dash or
            // other non-ASCII unicode in player-facing copy).
            foreach (char c in msg)
                Assert.True(c < 128,
                    "Reject copy must be plain ASCII (found non-ASCII char)");
        }

        // -----------------------------------------------------------------
        // Summary block (Career vs. Sandbox conditional)
        // -----------------------------------------------------------------

        private static RouteAnalysisResult EligibleAnalysis()
        {
            Recording source = new Recording
            {
                RecordingId = "src",
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 600.0,
                RouteConnectionWindows = new List<RouteConnectionWindow>()
            };
            RouteConnectionWindow window = new RouteConnectionWindow
            {
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = new RouteEndpoint
                {
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 100.0
                }
            };
            source.RouteConnectionWindows.Add(window);
            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 50.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>()
            };
        }

        private static RouteRunCostCalculator.RouteRunCost ApplicableRunCost(
            double launch, double recovered, int recoveries)
        {
            double net = launch - recovered;
            return new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = launch > 0.0,
                LaunchCost = launch,
                RecoveredCredits = recovered,
                NetCost = net > 0.0 ? net : 0.0,
                RecoveryEventCount = recoveries
            };
        }

        [Fact]
        public void BuildSummaryBlock_CareerKsc_WithRunCost_IncludesCostBlock()
        {
            // catches: the run-cost block dropping out of the Career + KSC summary.
            // Career players need cost visibility before confirming the route; the
            // old "Dispatch cost: TBD" placeholder was replaced by the real net.
            RouteRunCostCalculator.RouteRunCost cost = ApplicableRunCost(12500.0, 7300.0, 1);
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER, null, cost);
            Assert.Contains("Cost per run: 5,200 funds", block);
            Assert.Contains("Launch: 12,500 funds", block);
            Assert.Contains("Recovered: 7,300 funds", block);
            // The old placeholder must be gone.
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_SandboxMode_OmitsCostBlock()
        {
            // catches: leaking a cost block into Sandbox mode (no funds). A
            // not-applicable RunCost (the caller computes Applicable=false outside
            // Career) must omit the block entirely.
            RouteRunCostCalculator.RouteRunCost cost = new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = false,
                CostKnown = false
            };
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.SANDBOX, null, cost);
            Assert.DoesNotContain("Cost per run", block);
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_NoRunCostPassed_OmitsCostBlock()
        {
            // catches: a null run-cost (test / legacy call paths that do not wire
            // the cost) rendering a stray block. The block must only appear when an
            // applicable, known cost is supplied.
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER);
            Assert.DoesNotContain("Cost per run", block);
            Assert.DoesNotContain("TBD", block);
        }

        [Fact]
        public void BuildSummaryBlock_CareerKsc_UnknownCost_OmitsCostBlock()
        {
            // catches: an unhydrated snapshot (launch 0 -> CostKnown false) leaking a
            // misleading "0 funds" block (gotcha G7). The block must be suppressed.
            RouteRunCostCalculator.RouteRunCost cost = new RouteRunCostCalculator.RouteRunCost
            {
                Applicable = true,
                CostKnown = false,
                LaunchCost = 0.0
            };
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER, null, cost);
            Assert.DoesNotContain("Cost per run", block);
        }
    }
}
