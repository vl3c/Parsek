using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RouteProofCaptureTests
    {
        [Fact]
        public void ExtractResourceManifest_WithPartPidScope_FiltersResources()
        {
            ConfigNode vessel = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 5.0, 200.0)));

            Dictionary<string, ResourceAmount> scoped =
                VesselSpawner.ExtractResourceManifest(vessel, new List<uint> { 100 });

            Assert.NotNull(scoped);
            Assert.Single(scoped);
            Assert.Equal(80.0, scoped["LiquidFuel"].amount);
            Assert.Equal(100.0, scoped["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void ExtractInventoryPayloadItems_PreservesExactStoredPartSnapshot()
        {
            ConfigNode storedPart = MakeStoredPart(
                "evaJetpack",
                "white",
                1,
                MakeResource("MonoPropellant", 5.0, 5.0));
            ConfigNode vessel = MakeVessel(
                MakePart(100, "cargoBay", MakeInventoryModule(storedPart)),
                MakePart(200, "stationCargo", MakeInventoryModule(
                    MakeStoredPart("evaRepairKit", null, 2))));

            List<InventoryPayloadItem> payload =
                VesselSpawner.ExtractInventoryPayloadItems(vessel, new List<uint> { 100 });

            Assert.NotNull(payload);
            Assert.Single(payload);
            Assert.Equal("evaJetpack", payload[0].PartName);
            Assert.Equal("white", payload[0].VariantName);
            Assert.Equal(1, payload[0].Quantity);
            Assert.Equal(1, payload[0].SlotsTaken);
            Assert.Equal(5.0, payload[0].StoredResources["MonoPropellant"].amount);
            Assert.Equal("STOREDPART", payload[0].StoredPartSnapshot.name);
            Assert.Equal("evaJetpack", payload[0].StoredPartSnapshot.GetValue("partName"));
        }

        [Fact]
        public void InventoryPayloadIdentityHash_IgnoresSlotAndQuantityButIncludesPayload()
        {
            ConfigNode first = new ConfigNode("STOREDPART");
            first.AddValue("slotIndex", "0");
            first.AddValue("partName", "evaJetpack");
            first.AddValue("variantName", "white");
            first.AddValue("quantity", "1");

            ConfigNode second = new ConfigNode("STOREDPART");
            second.AddValue("quantity", "3");
            second.AddValue("slotIndex", "4");
            second.AddValue("variantName", "white");
            second.AddValue("partName", "evaJetpack");

            string firstHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(first);
            string secondHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(second);
            Assert.Equal(firstHash, secondHash);

            second.SetValue("variantName", "orange", true);
            Assert.NotEqual(firstHash, VesselSpawner.ComputeInventoryPayloadIdentityHash(second));
        }

        [Fact]
        public void InventoryPayloadIdentityHash_IgnoresNestedProtoPartVolatileValues()
        {
            ConfigNode first = MakeStoredPartWithInnerPart(
                partName: "evaJetpack",
                resourceAmount: 5.0,
                moduleMode: "packed",
                cid: "4294884350",
                persistentId: "100",
                position: "0,0,0",
                temperature: "-1");
            ConfigNode second = MakeStoredPartWithInnerPart(
                partName: "evaJetpack",
                resourceAmount: 5.0,
                moduleMode: "packed",
                cid: "4294889999",
                persistentId: "200",
                position: "1,2,3",
                temperature: "250");

            string firstHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(first);
            Assert.Equal(firstHash, VesselSpawner.ComputeInventoryPayloadIdentityHash(second));

            second.GetNode("PART").GetNode("RESOURCE").SetValue("amount", "4", true);
            Assert.NotEqual(firstHash, VesselSpawner.ComputeInventoryPayloadIdentityHash(second));

            ConfigNode third = MakeStoredPartWithInnerPart(
                partName: "evaJetpack",
                resourceAmount: 5.0,
                moduleMode: "deployed",
                cid: "4294884350",
                persistentId: "100",
                position: "0,0,0",
                temperature: "-1");
            Assert.NotEqual(firstHash, VesselSpawner.ComputeInventoryPayloadIdentityHash(third));
        }

        [Fact]
        public void ExtractInventoryPayloadItems_GroupsStacksByPayloadIdentity()
        {
            ConfigNode first = MakeStoredPart("evaRepairKit", null, 2);
            first.AddValue("slotIndex", "0");
            ConfigNode second = MakeStoredPart("evaRepairKit", null, 3);
            second.AddValue("slotIndex", "1");
            ConfigNode vessel = MakeVessel(
                MakePart(100, "cargoBay", MakeInventoryModule(first, second)));

            List<InventoryPayloadItem> payload =
                VesselSpawner.ExtractInventoryPayloadItems(vessel, new List<uint> { 100 });

            Assert.NotNull(payload);
            Assert.Single(payload);
            Assert.Equal("evaRepairKit", payload[0].PartName);
            Assert.Equal(5, payload[0].Quantity);
            Assert.Equal(2, payload[0].SlotsTaken);
        }

        [Fact]
        public void RouteConnectionWindow_CapturesDockAndCompletesAtUndock()
        {
            ConfigNode dockedSnapshot = MakeVessel(
                MakePart(
                    100,
                    "transportTank",
                    MakeResource("LiquidFuel", 80.0, 100.0),
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))),
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 0.0, 200.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0,
                9001,
                RouteConnectionKind.DockingPort,
                dockedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Mun",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                4);

            Assert.NotNull(window);
            Assert.False(window.IsComplete);
            Assert.Equal(80.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.Equal(0.0, window.DockEndpointResources["LiquidFuel"].amount);
            Assert.Single(window.DockTransportInventory);
            Assert.Null(window.DockEndpointInventory);

            ConfigNode undockedTransport = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));
            ConfigNode undockedEndpoint = MakeVessel(
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 50.0, 200.0),
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))));

            Assert.True(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                undockedTransport,
                undockedEndpoint));

            Assert.True(window.IsComplete);
            Assert.Equal(160.0, window.UndockUT);
            Assert.Equal(30.0, window.UndockTransportResources["LiquidFuel"].amount);
            Assert.Equal(50.0, window.UndockEndpointResources["LiquidFuel"].amount);
            Assert.Null(window.UndockTransportInventory);
            Assert.Single(window.UndockEndpointInventory);
            Assert.Equal("evaJetpack", window.UndockEndpointInventory[0].PartName);
        }

        // Regression for the 2026-05-18 dock-2 playtest: the dock-side endpoint
        // baseline was inflated to LF=400/800 because the endpoint snapshot fell
        // back to the post-couple merged vessel, which contains transport parts
        // too. With endpointPreCoupleSnapshot set, the endpoint baseline must
        // reflect ONLY the partner's pre-dock parts.
        [Fact]
        public void BuildDockRouteConnectionWindow_EndpointPreCoupleSnapshotOverride_ScopesEndpointBaselineToPartner()
        {
            // Merged snapshot (post-couple): transport tank 80/100 + endpoint tank 200/200
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(
                    100,
                    "transportTank",
                    MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 200.0, 200.0)));

            // Pre-couple endpoint snapshot: only the endpoint tank, 200/200.
            ConfigNode endpointPreCouple = MakeVessel(
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 200.0, 200.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0,
                9001,
                RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1,
                endpointPreCoupleSnapshot: endpointPreCouple);

            Assert.NotNull(window);
            Assert.Equal(80.0, window.DockTransportResources["LiquidFuel"].amount);
            // Endpoint baseline must come from the pre-couple snapshot: 200/200, not 280/300.
            Assert.Equal(200.0, window.DockEndpointResources["LiquidFuel"].amount);
            Assert.Equal(200.0, window.DockEndpointResources["LiquidFuel"].maxAmount);
        }

        // Catches the regression where the endpointPreCoupleSnapshot parameter is
        // silently ignored and the merged snapshot is used as the endpoint baseline
        // source.
        [Fact]
        public void BuildDockRouteConnectionWindow_EndpointPreCoupleSnapshotOverride_DoesNotIncludeTransportContribution()
        {
            // If the merged snapshot is used by mistake, both tanks contribute and
            // the endpoint baseline shows 280/300 (=80/100 + 200/200) instead of 200/200.
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(
                    100,
                    "transportTank",
                    MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 200.0, 200.0)));
            ConfigNode endpointPreCouple = MakeVessel(
                MakePart(
                    200,
                    "endpointTank",
                    MakeResource("LiquidFuel", 200.0, 200.0)));

            // With endpointPids = both 100 and 200 (the buggy scenario where the
            // endpoint-PID set came from the merged vessel's snapshot), the
            // pre-couple snapshot still scopes the resource extraction to its own
            // parts only — pid 100 isn't in endpointPreCouple, so it contributes
            // nothing.
            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0,
                9001,
                RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 100, 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1,
                endpointPreCoupleSnapshot: endpointPreCouple);

            Assert.NotNull(window);
            // Endpoint baseline must NOT roll in pid 100's contribution because the
            // pre-couple snapshot doesn't have it.
            Assert.Equal(200.0, window.DockEndpointResources["LiquidFuel"].amount);
            Assert.Equal(200.0, window.DockEndpointResources["LiquidFuel"].maxAmount);
        }

        // Baseline behavior (no override): when endpointPreCoupleSnapshot is null,
        // BuildDockRouteConnectionWindow falls back to dockedSnapshot for the endpoint
        // baseline — preserving the existing public contract.
        [Fact]
        public void BuildDockRouteConnectionWindow_NoEndpointOverride_UsesMergedSnapshotForEndpointBaseline()
        {
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 80.0, 100.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 200.0, 200.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0,
                9001,
                RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1);

            Assert.NotNull(window);
            Assert.Equal(80.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.Equal(200.0, window.DockEndpointResources["LiquidFuel"].amount);
        }

        // --- Dock-side TRANSPORT baseline is post-couple (symmetric transport fix) ---
        // The merged snapshot is captured frames after the couple, so a same-frame stock
        // crossfeed equalisation that drained the transport tank into the depot deflates
        // DOCK_TRANSPORT_RESOURCES; a later undock reading then looks like a pickup and
        // trips the strict MixedPickupDelivery gate. A pre-couple transport snapshot fixes
        // the baseline, mirroring the endpointPreCoupleSnapshot mechanism above.

        [Fact]
        public void BuildDockRouteConnectionWindow_TransportPreCoupleSnapshot_UsesPreCoupleTransportBaseline()
        {
            // Merged (post-couple, equalisation drained the transport tank): transport 300/600.
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 300.0, 600.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 300.0, 1000.0)));
            // Pre-couple transport (true pre-dock level): 500/600.
            ConfigNode transportPreCouple = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 500.0, 600.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1,
                endpointPreCoupleSnapshot: null,
                transportPreCoupleSnapshot: transportPreCouple);

            Assert.NotNull(window);
            Assert.Equal(500.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.Equal(600.0, window.DockTransportResources["LiquidFuel"].maxAmount);
        }

        [Fact]
        public void BuildDockRouteConnectionWindow_NoTransportOverride_UsesMergedSnapshotForTransportBaseline()
        {
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 300.0, 600.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 300.0, 1000.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1);

            Assert.NotNull(window);
            // No override -> existing contract preserved (merged snapshot baseline).
            Assert.Equal(300.0, window.DockTransportResources["LiquidFuel"].amount);
        }

        [Fact]
        public void BuildDockRouteConnectionWindow_TransportPreCoupleSnapshot_MissingTransportPids_FallsBackToMerged()
        {
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 300.0, 600.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 300.0, 1000.0)));
            // A snapshot that does NOT contain the transport PID (100) must be rejected by
            // the self-validation and the merged snapshot used instead.
            ConfigNode unrelatedSnapshot = MakeVessel(
                MakePart(999, "someOtherPart", MakeResource("LiquidFuel", 500.0, 600.0)));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1,
                endpointPreCoupleSnapshot: null,
                transportPreCoupleSnapshot: unrelatedSnapshot);

            Assert.NotNull(window);
            Assert.Equal(300.0, window.DockTransportResources["LiquidFuel"].amount);
        }

        [Fact]
        public void BuildDockRouteConnectionWindow_TransportPreCoupleSnapshot_ScopesInventoryBaseline()
        {
            // Merged transport bay has no kit (moved/equalised during dock); the pre-couple
            // transport snapshot still carries it. DOCK_TRANSPORT_INVENTORY must use it.
            ConfigNode dockedMergedSnapshot = MakeVessel(
                MakePart(100, "transportBay", MakeResource("LiquidFuel", 300.0, 600.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 300.0, 1000.0)));
            ConfigNode transportPreCouple = MakeVessel(
                MakePart(100, "transportBay",
                    MakeResource("LiquidFuel", 500.0, 600.0),
                    MakeInventoryModule(MakeStoredPart("evaJetpack", "white", 1))));

            RouteConnectionWindow window = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort,
                dockedMergedSnapshot,
                new List<uint> { 100 },
                new List<uint> { 200 },
                endpointAtDock: null,
                transferEndpointSituation: -1,
                endpointPreCoupleSnapshot: null,
                transportPreCoupleSnapshot: transportPreCouple);

            Assert.NotNull(window);
            Assert.Equal(500.0, window.DockTransportResources["LiquidFuel"].amount);
            Assert.NotNull(window.DockTransportInventory);
            Assert.Single(window.DockTransportInventory);
        }

        // End-to-end (the actual user-visible bug): a deflated dock-transport baseline makes
        // a clean delivery run read as a pickup (transportGain = undock - dock > 0) and get
        // rejected as MixedPickupDelivery; the pre-couple transport baseline removes the
        // false positive while everything else about the window is identical.
        [Fact]
        public void AnalyzeRecording_DeflatedDockTransportBaseline_FalselyRejects_PreCoupleBaselineDoesNot()
        {
            ConfigNode merged = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 300.0, 600.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 300.0, 1000.0)));
            ConfigNode transportPreCouple = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 500.0, 600.0)));
            ConfigNode endpointPreCouple = MakeVessel(
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 100.0, 1000.0)));
            RouteEndpoint endpoint = new RouteEndpoint
            {
                VesselPersistentId = 9001,
                BodyName = "Mun",
                IsSurface = false
            };

            // Undock: a clean 100 LF delivery. Transport 500 -> 400 (gave 100); depot
            // (pre-couple baseline 100) -> 200 (received 100). Consistent both sides.
            Dictionary<string, ResourceAmount> undockTransport = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 400.0, maxAmount = 600.0 }
            };
            Dictionary<string, ResourceAmount> undockEndpoint = new Dictionary<string, ResourceAmount>
            {
                ["LiquidFuel"] = new ResourceAmount { amount = 200.0, maxAmount = 1000.0 }
            };

            // BUG: no transport pre-couple -> DockTransport = 300 -> transportGain = +100.
            RouteConnectionWindow bug = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort, merged,
                new List<uint> { 100 }, new List<uint> { 200 },
                endpoint, transferEndpointSituation: 4,
                endpointPreCoupleSnapshot: endpointPreCouple,
                transportPreCoupleSnapshot: null);
            bug.UndockUT = 200.0;
            bug.UndockTransportResources = undockTransport;
            bug.UndockEndpointResources = undockEndpoint;
            RouteAnalysisResult bugResult = RouteAnalysisEngine.AnalyzeRecording(
                new Recording { RecordingId = "deflated", StartBodyName = "Kerbin", LaunchSiteName = "LaunchPad", RouteConnectionWindows = new List<RouteConnectionWindow> { bug } });
            Assert.Equal(RouteAnalysisStatus.MixedPickupDelivery, bugResult.Status);

            // FIX: transport pre-couple -> DockTransport = 500 -> transportGain = -100.
            RouteConnectionWindow fix = RouteProofCapture.BuildDockRouteConnectionWindow(
                100.0, 9001, RouteConnectionKind.DockingPort, merged,
                new List<uint> { 100 }, new List<uint> { 200 },
                endpoint, transferEndpointSituation: 4,
                endpointPreCoupleSnapshot: endpointPreCouple,
                transportPreCoupleSnapshot: transportPreCouple);
            fix.UndockUT = 200.0;
            fix.UndockTransportResources = undockTransport;
            fix.UndockEndpointResources = undockEndpoint;
            RouteAnalysisResult fixResult = RouteAnalysisEngine.AnalyzeRecording(
                new Recording { RecordingId = "precouple", StartBodyName = "Kerbin", LaunchSiteName = "LaunchPad", RouteConnectionWindows = new List<RouteConnectionWindow> { fix } });
            Assert.NotEqual(RouteAnalysisStatus.MixedPickupDelivery, fixResult.Status);
            // The clean 100 LF delivery is now accepted.
            Assert.Equal(RouteAnalysisStatus.Eligible, fixResult.Status);
        }

        // --- ComputePartSetDifferences (pure helper) ---

        [Fact]
        public void ComputePartSetDifferences_EqualSets_NoDifferences()
        {
            RouteProofCapture.ComputePartSetDifferences(
                new List<uint> { 100u, 200u, 300u },
                new List<uint> { 100u, 200u, 300u },
                out List<uint> added,
                out List<uint> removed);

            Assert.Empty(added);
            Assert.Empty(removed);
        }

        [Fact]
        public void ComputePartSetDifferences_ActualHasExtra_ReturnsAdded()
        {
            RouteProofCapture.ComputePartSetDifferences(
                actualPartPids: new List<uint> { 100u, 200u, 999u },
                expectedPartPids: new List<uint> { 100u, 200u },
                out List<uint> added,
                out List<uint> removed);

            Assert.Equal(new List<uint> { 999u }, added);
            Assert.Empty(removed);
        }

        [Fact]
        public void ComputePartSetDifferences_ActualMissingPart_ReturnsRemoved()
        {
            RouteProofCapture.ComputePartSetDifferences(
                actualPartPids: new List<uint> { 100u },
                expectedPartPids: new List<uint> { 100u, 200u, 300u },
                out List<uint> added,
                out List<uint> removed);

            Assert.Empty(added);
            Assert.Equal(new List<uint> { 200u, 300u }, removed);
        }

        [Fact]
        public void ComputePartSetDifferences_BothAddedAndRemoved_ReturnsBothLists()
        {
            RouteProofCapture.ComputePartSetDifferences(
                actualPartPids: new List<uint> { 100u, 555u },
                expectedPartPids: new List<uint> { 100u, 200u },
                out List<uint> added,
                out List<uint> removed);

            Assert.Equal(new List<uint> { 555u }, added);
            Assert.Equal(new List<uint> { 200u }, removed);
        }

        [Fact]
        public void ComputePartSetDifferences_NullInputs_TreatedAsEmpty()
        {
            RouteProofCapture.ComputePartSetDifferences(
                actualPartPids: null,
                expectedPartPids: null,
                out List<uint> added,
                out List<uint> removed);

            Assert.Empty(added);
            Assert.Empty(removed);
        }

        // --- LogRoutePartSetEqualityWarnings end-to-end ---

        [Fact]
        public void LogRoutePartSetEqualityWarnings_PerfectMatch_EmitsNoWarning()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ConfigNode transport = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));
                ConfigNode endpoint = MakeVessel(
                    MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));

                RouteProofCapture.LogRoutePartSetEqualityWarnings(
                    new[] { transport, endpoint },
                    transportPartPersistentIds: new List<uint> { 100u },
                    endpointPartPersistentIds: new List<uint> { 200u },
                    windowId: "test-window");

                Assert.DoesNotContain(logLines, l => l.Contains("part-set drift"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void LogRoutePartSetEqualityWarnings_TransportGainedPart_EmitsWarning()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // Transport snapshot at undock has pid 999 that wasn't in pre-dock transport set.
                ConfigNode transport = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)),
                    MakePart(999, "newPartFromEvaConstruction", MakeResource("MonoPropellant", 5.0, 5.0)));
                ConfigNode endpoint = MakeVessel(
                    MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));

                RouteProofCapture.LogRoutePartSetEqualityWarnings(
                    new[] { transport, endpoint },
                    transportPartPersistentIds: new List<uint> { 100u },
                    endpointPartPersistentIds: new List<uint> { 200u },
                    windowId: "test-window");

                Assert.Contains(logLines, l =>
                    l.Contains("part-set drift")
                    && l.Contains("side='transport'")
                    && l.Contains("window=test-window")
                    && l.Contains("added=[999]"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void LogRoutePartSetEqualityWarnings_EndpointLostPart_EmitsWarning()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                ConfigNode transport = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));
                // Endpoint expected pids 200, 201; actual at undock has only 200 (jettisoned).
                ConfigNode endpoint = MakeVessel(
                    MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));

                RouteProofCapture.LogRoutePartSetEqualityWarnings(
                    new[] { transport, endpoint },
                    transportPartPersistentIds: new List<uint> { 100u },
                    endpointPartPersistentIds: new List<uint> { 200u, 201u },
                    windowId: "test-window");

                Assert.Contains(logLines, l =>
                    l.Contains("part-set drift")
                    && l.Contains("side='endpoint'")
                    && l.Contains("removed=[201]"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        // Catches a regression where the warning is emitted on the disjoint-overlap
        // failure path (where the disjoint verifier should catch it first and reject
        // the route). The warning is observational ONLY for accepted splits.
        [Fact]
        public void LogRoutePartSetEqualityWarnings_DisjointAmbiguousSnapshot_SkipsWarning()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // Snapshot has parts from BOTH sets — disjoint verifier would reject
                // this case in production. The warning emitter must skip snapshots
                // that aren't cleanly transport-only or endpoint-only.
                ConfigNode mixed = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)),
                    MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));

                RouteProofCapture.LogRoutePartSetEqualityWarnings(
                    new[] { mixed },
                    transportPartPersistentIds: new List<uint> { 100u },
                    endpointPartPersistentIds: new List<uint> { 200u },
                    windowId: "test-window");

                Assert.DoesNotContain(logLines, l => l.Contains("part-set drift"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        // Regression for the 2026-05-18 playtest: a stock fuel transfer pumps fuel
        // between two tanks BUT leaves the outer part-PID set unchanged. The
        // warning must NOT fire — only outer-part changes (EVA construction etc.)
        // should trip it.
        [Fact]
        public void LogRoutePartSetEqualityWarnings_FuelTransferOnly_DoesNotEmitWarning()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            try
            {
                // Transport had 200 LF pre-dock, has 400 LF post-undock (gained 200).
                // Endpoint had 200 LF pre-dock, has 0 LF post-undock (lost 200).
                // Same part-PID sets on both sides.
                ConfigNode transport = MakeVessel(
                    MakePart(100, "transportTank", MakeResource("LiquidFuel", 400.0, 400.0)));
                ConfigNode endpoint = MakeVessel(
                    MakePart(200, "endpointTank", MakeResource("LiquidFuel", 0.0, 400.0)));

                RouteProofCapture.LogRoutePartSetEqualityWarnings(
                    new[] { transport, endpoint },
                    transportPartPersistentIds: new List<uint> { 100u },
                    endpointPartPersistentIds: new List<uint> { 200u },
                    windowId: "test-window");

                Assert.DoesNotContain(logLines, l => l.Contains("part-set drift"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        [Fact]
        public void CompleteRouteConnectionWindowAtUndock_MissingEndpointPart_DoesNotMarkComplete()
        {
            var window = new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                TransferTargetVesselPid = 9001,
                TransportPartPersistentIds = new List<uint> { 100 },
                EndpointPartPersistentIds = new List<uint> { 200 }
            };
            ConfigNode onlyTransport = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)));

            Assert.False(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                onlyTransport));

            Assert.False(window.IsComplete);
            Assert.True(double.IsNaN(window.UndockUT));
        }

        [Fact]
        public void CompleteRouteConnectionWindowAtUndock_RouteSetsStillDocked_DoesNotMarkComplete()
        {
            var window = new RouteConnectionWindow
            {
                WindowId = "window",
                DockUT = 100.0,
                TransferTargetVesselPid = 9001,
                TransportPartPersistentIds = new List<uint> { 100 },
                EndpointPartPersistentIds = new List<uint> { 200 }
            };
            ConfigNode routePairStillDocked = MakeVessel(
                MakePart(100, "transportTank", MakeResource("LiquidFuel", 30.0, 100.0)),
                MakePart(200, "endpointTank", MakeResource("LiquidFuel", 50.0, 200.0)));
            ConfigNode unrelatedUndockedCraft = MakeVessel(
                MakePart(300, "thirdCraft", MakeResource("Ore", 1.0, 10.0)));

            Assert.False(RouteProofCapture.CompleteRouteConnectionWindowAtUndock(
                window,
                160.0,
                routePairStillDocked,
                unrelatedUndockedCraft));

            Assert.False(window.IsComplete);
            Assert.True(double.IsNaN(window.UndockUT));
        }

        private static ConfigNode MakeVessel(params ConfigNode[] parts)
        {
            ConfigNode vessel = new ConfigNode("VESSEL");
            for (int i = 0; i < parts.Length; i++)
                vessel.AddNode(parts[i]);
            return vessel;
        }

        private static ConfigNode MakePart(uint persistentId, string name, params ConfigNode[] children)
        {
            ConfigNode part = new ConfigNode("PART");
            part.AddValue("name", name);
            part.AddValue("persistentId", persistentId.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < children.Length; i++)
                part.AddNode(children[i]);
            return part;
        }

        private static ConfigNode MakeResource(string name, double amount, double maxAmount)
        {
            ConfigNode resource = new ConfigNode("RESOURCE");
            resource.AddValue("name", name);
            resource.AddValue("amount", amount.ToString("R", CultureInfo.InvariantCulture));
            resource.AddValue("maxAmount", maxAmount.ToString("R", CultureInfo.InvariantCulture));
            return resource;
        }

        private static ConfigNode MakeInventoryModule(params ConfigNode[] storedParts)
        {
            ConfigNode module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleInventoryPart");
            module.AddValue("InventorySlots", "4");
            ConfigNode storedPartsNode = module.AddNode("STOREDPARTS");
            for (int i = 0; i < storedParts.Length; i++)
                storedPartsNode.AddNode(storedParts[i]);
            return module;
        }

        private static ConfigNode MakeStoredPart(
            string partName,
            string variantName,
            int quantity,
            ConfigNode resource = null)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            if (!string.IsNullOrEmpty(variantName))
                storedPart.AddValue("variantName", variantName);
            storedPart.AddValue("quantity", quantity.ToString(CultureInfo.InvariantCulture));
            if (resource != null)
            {
                ConfigNode part = storedPart.AddNode("PART");
                part.AddValue("name", partName);
                part.AddNode(resource);
            }
            return storedPart;
        }

        private static ConfigNode MakeStoredPartWithInnerPart(
            string partName,
            double resourceAmount,
            string moduleMode,
            string cid,
            string persistentId,
            string position,
            string temperature)
        {
            ConfigNode storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("slotIndex", "0");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", "1");
            storedPart.AddValue("stackCapacity", "1");
            storedPart.AddValue("variantName", "white");

            ConfigNode part = storedPart.AddNode("PART");
            part.AddValue("name", partName);
            part.AddValue("cid", cid);
            part.AddValue("uid", "0");
            part.AddValue("mid", "0");
            part.AddValue("persistentId", persistentId);
            part.AddValue("launchID", "0");
            part.AddValue("parent", "0");
            part.AddValue("position", position);
            part.AddValue("rotation", "0,0,0,0");
            part.AddValue("temp", temperature);
            part.AddValue("flag", "");
            part.AddNode(MakeResource("MonoPropellant", resourceAmount, 5.0));

            ConfigNode module = part.AddNode("MODULE");
            module.AddValue("name", "ModuleCargoPart");
            module.AddValue("payloadMode", moduleMode);

            return storedPart;
        }
    }
}
