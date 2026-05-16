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
            // catches: a regression that always appends "×1" for single-item
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
            Assert.Equal("evaJetpack (white) ×2",
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

        [Fact]
        public void BuildSummaryBlock_CareerMode_IncludesDispatchCostLine()
        {
            // catches: the Career-mode "Dispatch cost" row dropping out of the
            // summary block. Career players need cost visibility before
            // confirming the route; without it the dialog hides a recurring
            // funds debit behind a single click.
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER);
            Assert.Contains("Dispatch cost", block);
        }

        [Fact]
        public void BuildSummaryBlock_SandboxMode_OmitsDispatchCostLine()
        {
            // catches: leaking the "Dispatch cost" row into Sandbox mode,
            // which has no funds budget. A spurious cost line would confuse
            // sandbox players and imply a non-existent debit.
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.SANDBOX);
            Assert.DoesNotContain("Dispatch cost", block);
        }
    }
}
