using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;
// `RouteBuilder` is intentionally the production class
// `Parsek.Logistics.RouteBuilder`. The fluent test fixture lives in
// `Parsek.Tests.Generators.RouteBuilder` and is not imported here.

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pure-logic tests for <see cref="RouteBuilder.BuildRoute"/>. Covers the
    /// happy path, every reject reason, and the log-line contract. Captured
    /// via <see cref="ParsekLog.TestSinkForTesting"/>; runs Sequential because
    /// the log sink is global static state.
    /// </summary>
    [Collection("Sequential")]
    public class RouteBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly CultureInfo originalCulture;

        public RouteBuilderTests()
        {
            originalCulture = Thread.CurrentThread.CurrentCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixtures
        // -----------------------------------------------------------------

        private static InventoryPayloadItem MakeInventoryItem(
            string identityHash = "payload-hash",
            string partName = "evaJetpack",
            int quantity = 1,
            int slotsTaken = 1,
            string variantName = null)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            return new InventoryPayloadItem
            {
                IdentityHash = identityHash,
                PartName = partName,
                VariantName = variantName,
                Quantity = quantity,
                SlotsTaken = slotsTaken,
                StoredPartSnapshot = storedPart
            };
        }

        private static RouteEndpoint MakeMunEndpoint(uint pid = 9001)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = pid,
                BodyName = "Mun",
                Latitude = 12.345,
                Longitude = -45.678,
                Altitude = 612.5,
                IsSurface = true
            };
        }

        private static RouteConnectionWindow MakeCompleteWindow()
        {
            return new RouteConnectionWindow
            {
                WindowId = "w",
                DockUT = 100.0,
                UndockUT = 160.0,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                EndpointAtDock = MakeMunEndpoint(),
                TransferEndpointSituation = 4
            };
        }

        // KSC-origin source recording with completed transfer.
        private static Recording MakeKscSource(
            double startUT = 1000.0,
            double endUT = 1300.0,
            string recordingId = "src-ksc")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-1",
                TreeOrder = 3,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteConnectionWindows = new List<RouteConnectionWindow> { MakeCompleteWindow() },
                RouteOriginProof = null
            }.WithUtSpan(startUT, endUT);
        }

        // Non-KSC source: empty LaunchSiteName + RouteOriginProof with PID.
        private static Recording MakeNonKscSource(
            uint originPid = 123,
            double startUT = 5000.0,
            double endUT = 5600.0,
            string recordingId = "src-non-ksc")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = "tree-non-ksc",
                TreeOrder = 1,
                StartBodyName = "Mun",
                LaunchSiteName = null,
                RouteConnectionWindows = new List<RouteConnectionWindow> { MakeCompleteWindow() },
                RouteOriginProof = new RouteOriginProof { StartDockedOriginVesselPid = originPid }
            }.WithUtSpan(startUT, endUT);
        }

        private static RouteAnalysisResult EligibleAnalysisFromSource(Recording source)
        {
            RouteConnectionWindow window = source.RouteConnectionWindows[0];
            return new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.Eligible,
                SourceRecording = source,
                ConnectionWindow = window,
                ResourceDeliveryManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 50.0 }
                },
                InventoryDeliveryManifest = new List<InventoryPayloadItem>
                {
                    MakeInventoryItem()
                }
            };
        }

        private static RouteBuilder.RouteCreationInputs Inputs(
            double interval = 600.0,
            string name = "Test Route")
        {
            return new RouteBuilder.RouteCreationInputs
            {
                Name = name,
                DispatchIntervalSeconds = interval
            };
        }

        // -----------------------------------------------------------------
        // Happy-path tests
        // -----------------------------------------------------------------

        [Fact]
        public void Build_FromEligibleSingleRecordingResult_ProducesActiveRoute_WithFreshGuid()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.Null(outcome.RejectReason);
            Assert.Equal(RouteStatus.Active, outcome.Route.Status);
            Assert.False(string.IsNullOrEmpty(outcome.Route.Id));
            // GUID "N" is 32 hex chars.
            Assert.Equal(32, outcome.Route.Id.Length);
        }

        [Fact]
        public void Build_PopulatesSourceRefsWithRouteProofHash()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Single(outcome.Route.SourceRefs);
            RouteSourceRef sref = outcome.Route.SourceRefs[0];
            Assert.Equal(source.RecordingId, sref.RecordingId);
            Assert.Equal(source.TreeId, sref.TreeId);
            Assert.Equal(source.TreeOrder, sref.TreeOrder);
            Assert.Equal(source.StartUT, sref.StartUT);
            Assert.Equal(source.EndUT, sref.EndUT);

            // Hash must match the same computation the store does.
            string expected = RouteStore.ComputeRouteProofHashFromRecording(source);
            Assert.Equal(expected, sref.RouteProofHash);
        }

        [Fact]
        public void Build_KscOriginRecording_SetsIsKscOriginTrue()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.CAREER);

            Assert.True(outcome.Route.IsKscOrigin);
            Assert.Equal("Kerbin", outcome.Route.Origin.BodyName);
            Assert.Equal(0u, outcome.Route.Origin.VesselPersistentId);
        }

        [Fact]
        public void Build_NonKscOriginRecording_PopulatesOriginVesselPid_FromRouteOriginProof()
        {
            Recording source = MakeNonKscSource(originPid: 4242);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.NotNull(outcome.Route);
            Assert.False(outcome.Route.IsKscOrigin);
            Assert.Equal(4242u, outcome.Route.Origin.VesselPersistentId);
        }

        [Fact]
        public void Build_StopCarriesResourceAndInventoryManifestsFromResult()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Single(outcome.Route.Stops);
            RouteStop stop = outcome.Route.Stops[0];
            Assert.Equal(RouteConnectionKind.DockingPort, stop.ConnectionKind);
            Assert.Equal(50.0, stop.DeliveryManifest["LiquidFuel"]);
            Assert.Single(stop.InventoryDeliveryManifest);
            Assert.Equal("evaJetpack", stop.InventoryDeliveryManifest[0].PartName);
        }

        [Fact]
        public void Build_TransitDurationMatchesSourcePathSpan()
        {
            Recording source = MakeKscSource(startUT: 2000.0, endUT: 2900.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 900.0), Game.Modes.SANDBOX);

            Assert.Equal(900.0, outcome.Route.TransitDuration);
        }

        [Fact]
        public void Build_PopulatesDispatchWindowEpochUTFromFirstStartUT()
        {
            Recording source = MakeKscSource(startUT: 17_000.0, endUT: 17_500.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 600.0), Game.Modes.SANDBOX);

            Assert.Equal(17_000.0, outcome.Route.DispatchWindowEpochUT);
        }

        // -----------------------------------------------------------------
        // Reject paths
        // -----------------------------------------------------------------

        [Fact]
        public void Build_DispatchIntervalBelowTransit_Rejected()
        {
            Recording source = MakeKscSource(startUT: 0.0, endUT: 1000.0);
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 500.0), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("interval-below-transit", outcome.RejectReason);
        }

        [Fact]
        public void Build_NegativeOrZeroInterval_Rejected()
        {
            Recording source = MakeKscSource();
            RouteAnalysisResult analysis = EligibleAnalysisFromSource(source);

            RouteBuilder.RouteBuildOutcome zero =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: 0.0), Game.Modes.SANDBOX);
            RouteBuilder.RouteBuildOutcome neg =
                RouteBuilder.BuildRoute(analysis, null, Inputs(interval: -10.0), Game.Modes.SANDBOX);

            Assert.Equal("interval-invalid", zero.RejectReason);
            Assert.Equal("interval-invalid", neg.RejectReason);
        }

        [Fact]
        public void Build_IneligibleResult_ReturnsNullRouteWithRejectReason()
        {
            RouteAnalysisResult analysis = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.MissingRouteProof
            };

            RouteBuilder.RouteBuildOutcome outcome =
                RouteBuilder.BuildRoute(analysis, null, Inputs(), Game.Modes.SANDBOX);

            Assert.Null(outcome.Route);
            Assert.Equal("source-no-longer-eligible", outcome.RejectReason);
        }

        [Fact]
        public void Build_LogsRouteUITagOnBuildAndOnReject()
        {
            // Happy path log
            Recording source = MakeKscSource();
            RouteAnalysisResult ok = EligibleAnalysisFromSource(source);
            RouteBuilder.BuildRoute(ok, null, Inputs(), Game.Modes.SANDBOX);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[RouteUI]")
                && l.Contains("Built route"));

            logLines.Clear();

            // Reject log
            RouteAnalysisResult bad = new RouteAnalysisResult
            {
                Status = RouteAnalysisStatus.MissingRouteProof
            };
            RouteBuilder.BuildRoute(bad, null, Inputs(), Game.Modes.SANDBOX);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[RouteUI]")
                && l.Contains("BuildRoute rejected")
                && l.Contains("source-no-longer-eligible"));
        }
    }

    /// <summary>
    /// Tiny helper to set the UT-anchor fields on a <see cref="Recording"/>
    /// used by tests. <see cref="Recording.StartUT"/> / <see cref="Recording.EndUT"/>
    /// are computed properties; this routes through the test entry points so
    /// fixtures stay declarative.
    /// </summary>
    internal static class RecordingTimingExtensions
    {
        internal static Recording WithUtSpan(this Recording rec, double startUT, double endUT)
        {
            // StartUT/EndUT are computed properties derived from
            // trajectory bounds or the Explicit* anchors when no points
            // exist. For these unit tests there are no Points, so the
            // Explicit* fields are the only way to pin the span.
            rec.ExplicitStartUT = startUT;
            rec.ExplicitEndUT = endUT;
            return rec;
        }
    }
}
