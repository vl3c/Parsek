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
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string line = RouteCreationFormatters.FormatResourceLine("LiquidFuel", 150.0);
            Assert.Equal("LiquidFuel: 150.0", line);
        }

        [Fact]
        public void FormatInventoryLine_QuantityOne_OmitsMultiplier()
        {
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
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.CAREER);
            Assert.Contains("Dispatch cost", block);
        }

        [Fact]
        public void BuildSummaryBlock_SandboxMode_OmitsDispatchCostLine()
        {
            string block = RouteCreationFormatters.BuildSummaryBlock(
                EligibleAnalysis(), Game.Modes.SANDBOX);
            Assert.DoesNotContain("Dispatch cost", block);
        }
    }
}
