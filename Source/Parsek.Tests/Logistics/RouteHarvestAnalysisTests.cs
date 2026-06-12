using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the M2 gain-side flow-closure check (plan D6) and the D5
    /// boundary-bridging scope rules (review BLOCKER 1). Fixtures model the
    /// two canonical mining runs:
    /// <list type="bullet">
    ///   <item>the DEPOT run - recording starts DOCKED to the depot (combined
    ///   root), undocks (split child = the transport), drills, docks at the
    ///   colony (merge child carries the window);</item>
    ///   <item>the KSC run - launch recording is the root, drills, docks at
    ///   the colony.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class RouteHarvestAnalysisTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        // Canonical part pids: 100 = transport, 200 = origin depot tank,
        // 300 = colony endpoint part.
        private const uint TransportPid = 100;
        private const uint DepotPid = 200;
        private const uint ColonyPid = 300;
        private const double DockUT = 500.0;
        private const double UndockUT = 600.0;

        public RouteHarvestAnalysisTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResourceTransferability.ResetForTesting();
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixture helpers
        // ------------------------------------------------------------------

        private static ResourceAmount RA(double amount, double max = 1000.0)
        {
            return new ResourceAmount { amount = amount, maxAmount = max };
        }

        private static Dictionary<string, ResourceAmount> Ore(double amount)
        {
            return new Dictionary<string, ResourceAmount> { ["Ore"] = RA(amount) };
        }

        private static RouteRunCargoManifest CompleteManifest(
            uint[] pids,
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
        {
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = new List<uint>(pids),
                StartTransportResources = start,
                EndTransportResources = end,
                EndCaptured = true
            };
        }

        private static RouteHarvestWindow HarvestWindow(
            double startUT, double endUT,
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end,
            bool openedAtStart = false, bool closedAtStop = false,
            string body = "Minmus", int situation = 1)
        {
            return new RouteHarvestWindow
            {
                WindowId = "hw-" + startUT,
                StartUT = startUT,
                EndUT = endUT,
                OpenedAtRecordingStart = openedAtStart,
                ClosedAtRecordingStop = closedAtStop,
                StartTransportResources = start,
                EndTransportResources = end,
                BodyName = body,
                Latitude = 10.0,
                Longitude = 20.0,
                Altitude = 30.0,
                SituationAtOpen = situation
            };
        }

        // Delivery window at the colony: transport-scoped manifests are the
        // gain-check inputs (dockOre = transport contents AT dock).
        private static RouteConnectionWindow ColonyWindow(
            double dockOre, double undockOre,
            double endpointDockOre = 0.0, double endpointUndockOre = -1.0)
        {
            if (endpointUndockOre < 0.0)
                endpointUndockOre = endpointDockOre + (dockOre - undockOre);
            return new RouteConnectionWindow
            {
                WindowId = "colony-window",
                DockUT = DockUT,
                UndockUT = UndockUT,
                TransferTargetVesselPid = 9001,
                TransferKind = RouteConnectionKind.DockingPort,
                TransportPartPersistentIds = new List<uint> { TransportPid },
                EndpointPartPersistentIds = new List<uint> { ColonyPid },
                DockTransportResources = Ore(dockOre),
                UndockTransportResources = Ore(undockOre),
                DockEndpointResources = Ore(endpointDockOre),
                UndockEndpointResources = Ore(endpointUndockOre),
                EndpointAtDock = new RouteEndpoint
                {
                    VesselPersistentId = 9001,
                    BodyName = "Minmus",
                    Latitude = 1.0,
                    Longitude = 2.0,
                    Altitude = 3.0,
                    IsSurface = true
                },
                TransferEndpointSituation = 1
            };
        }

        /// <summary>
        /// Depot-run tree: root = combined depot+transport (scope
        /// {100, 200}), Undock BP, solo = transport alone (scope {100}),
        /// Dock BP, merge = transport+colony (scope {100, 300}) carrying the
        /// window. UTs: root 0, solo 100, merge 500 (== DockUT).
        /// </summary>
        private static RecordingTree BuildDepotTree(
            out Recording root, out Recording solo, out Recording merge,
            double rootStartOre = 0.0)
        {
            root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree-depot",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 100.0,
                RouteOriginProof = new RouteOriginProof { StartDockedOriginVesselPid = 7777 },
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, DepotPid }, Ore(rootStartOre), Ore(rootStartOre))
            };
            solo = new Recording
            {
                RecordingId = "solo",
                TreeId = "tree-depot",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 500.0,
                ParentBranchPointId = "bp-undock",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid }, Ore(0.0), Ore(120.0))
            };
            merge = new Recording
            {
                RecordingId = "merge",
                TreeId = "tree-depot",
                ExplicitStartUT = DockUT,
                ExplicitEndUT = UndockUT,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, ColonyPid }, Ore(120.0), Ore(120.0))
            };

            var tree = new RecordingTree
            {
                Id = "tree-depot",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(solo);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-undock",
                UT = 100.0,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { solo.RecordingId }
            });
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = DockUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { solo.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        /// <summary>
        /// KSC-run tree: root = launch (scope {100}), Dock BP, merge (scope
        /// {100, 300}) carrying the window.
        /// </summary>
        private static RecordingTree BuildKscTree(
            out Recording root, out Recording merge,
            double rootStartOre = 0.0, double rootEndOre = 120.0)
        {
            root = new Recording
            {
                RecordingId = "root",
                TreeId = "tree-ksc",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = DockUT,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid }, Ore(rootStartOre), Ore(rootEndOre))
            };
            merge = new Recording
            {
                RecordingId = "merge",
                TreeId = "tree-ksc",
                ExplicitStartUT = DockUT,
                ExplicitEndUT = UndockUT,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, ColonyPid }, Ore(rootEndOre), Ore(rootEndOre))
            };
            var tree = new RecordingTree
            {
                Id = "tree-ksc",
                RootRecordingId = root.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = DockUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { root.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }

        private static HashSet<uint> WindowScope()
        {
            return new HashSet<uint> { TransportPid };
        }

        // ------------------------------------------------------------------
        // Lineage
        // ------------------------------------------------------------------

        // catches: a merge branch point with both a transport parent and the
        // endpoint's background parent picking the wrong leg (the depot/colony
        // BG recording must never enter the transport lineage).
        [Fact]
        public void CollectTransportLineage_DisambiguatesMergeParentsByPartPids()
        {
            RecordingTree tree = BuildDepotTree(out Recording root, out Recording solo, out Recording merge);
            var colonyBg = new Recording
            {
                RecordingId = "colony-bg",
                TreeId = tree.Id,
                ExplicitStartUT = 50.0,
                ExplicitEndUT = DockUT,
                RouteRunManifest = CompleteManifest(new[] { ColonyPid }, Ore(500.0), Ore(500.0))
            };
            tree.AddOrReplaceRecording(colonyBg);
            tree.BranchPoints[1].ParentRecordingIds.Add(colonyBg.RecordingId);

            List<RouteHarvestAnalysis.LineageLeg> lineage =
                RouteHarvestAnalysis.CollectTransportLineage(
                    tree, merge, WindowScope(), out string fallback);

            Assert.Null(fallback);
            Assert.NotNull(lineage);
            Assert.Equal(3, lineage.Count);
            Assert.Equal("root", lineage[0].Rec.RecordingId);
            Assert.Equal("solo", lineage[1].Rec.RecordingId);
            Assert.Equal("merge", lineage[2].Rec.RecordingId);
        }

        // catches: scope ambiguity resolved by picking the larger overlap
        // (pids are craft-baked, not launch-unique) instead of degrading.
        [Fact]
        public void CollectTransportLineage_AmbiguousLineage_FallsBackLegacy_Logs()
        {
            RecordingTree tree = BuildDepotTree(out _, out Recording solo, out Recording merge);
            // Second dock parent whose scope ALSO overlaps the transport pid.
            var phantom = new Recording
            {
                RecordingId = "phantom",
                TreeId = tree.Id,
                ExplicitStartUT = 60.0,
                ExplicitEndUT = DockUT,
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, ColonyPid }, Ore(0.0), Ore(0.0))
            };
            tree.AddOrReplaceRecording(phantom);
            tree.BranchPoints[1].ParentRecordingIds.Add(phantom.RecordingId);

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.LegacyFallback, result.Outcome);
            Assert.Contains("ambiguous-merge-parents", result.LegacyReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("legacy analysis") &&
                l.Contains("ambiguous-merge-parents"));
        }

        // ------------------------------------------------------------------
        // Gain verdicts
        // ------------------------------------------------------------------

        // catches: a fully-witnessed harvest gain failing the check (the
        // scenario-family-4 happy path at the pure level).
        [Fact]
        public void CheckTransportGains_CoveredGain_Ok()
        {
            RecordingTree tree = BuildKscTree(out Recording root, out Recording merge);
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(150.0, 400.0, Ore(0.0), Ore(120.0))
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.Covered, result.Outcome);
            Assert.NotNull(result.HarvestedManifest);
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
            Assert.NotNull(result.FirstHarvestWindow);
            Assert.Equal(150.0, result.FirstHarvestWindow.StartUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("gains covered"));
        }

        // catches: an unwitnessed gain admitted, or the reject not naming the
        // resource and quantities.
        [Fact]
        public void CheckTransportGains_UncoveredGain_NamesResourceAndQuantity()
        {
            RecordingTree tree = BuildKscTree(out _, out Recording merge);
            // No harvest windows anywhere.

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal("Ore", result.RejectResource);
            Assert.Equal(120.0, result.RejectGained, 6);
            Assert.Equal(0.0, result.RejectHarvested, 6);
            Assert.Equal("Ore: 120.0 gained, 0.0 harvested", result.RejectDetail);
        }

        // catches: a chain-boundary catch-up burst (leg N end < leg N+1
        // start) not bridging even though a converter was witnessed active at
        // the boundary - the run would falsely reject.
        [Fact]
        public void CheckTransportGains_BoundaryDelta_BridgedWhenWindowOpenAtBoundary()
        {
            RecordingTree tree = BuildChainKscTree(
                out Recording leg1, out Recording leg2, out Recording merge,
                leg1EndOre: 50.0, leg2StartOre: 80.0);
            // Leg 1 drilled 0 -> 50 and its window closed at the chain stop;
            // leg 2 reopened at start and drilled 80 -> 120. The +30 boundary
            // delta is the catch-up burst the bridge must credit.
            leg1.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(150.0, 300.0, Ore(0.0), Ore(50.0), closedAtStop: true)
            };
            leg2.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(300.0, 450.0, Ore(80.0), Ore(120.0), openedAtStart: true)
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.Covered, result.Outcome);
            // 50 (leg1 window) + 30 (bridged boundary) + 40 (leg2 window).
            Assert.Equal(120.0, result.HarvestedManifest["Ore"], 6);
        }

        // catches: a boundary delta bridged WITHOUT a window open at the
        // boundary - an unexplained between-leg gain must stay unaccounted.
        [Fact]
        public void CheckTransportGains_BoundaryDelta_UnbridgedFailsClosed()
        {
            RecordingTree tree = BuildChainKscTree(
                out Recording leg1, out Recording leg2, out Recording merge,
                leg1EndOre: 50.0, leg2StartOre: 80.0);
            // Windows exist but neither flags the boundary (closed mid-leg,
            // opened mid-leg) - bridge condition (c) fails.
            leg1.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(150.0, 250.0, Ore(0.0), Ore(50.0))
            };
            leg2.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(320.0, 450.0, Ore(80.0), Ore(120.0))
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal("Ore", result.RejectResource);
            Assert.Equal(120.0, result.RejectGained, 6);
            Assert.Equal(90.0, result.RejectHarvested, 6);
            Assert.Equal("Ore: 120.0 gained, 90.0 harvested", result.RejectDetail);
        }

        // BLOCKER-1 pin (plan D5 scope fix): the dock-merge child's start
        // manifest is scoped to the COMBINED transport+colony stack. Bridging
        // across the dock seam would credit the colony's entire inventory as
        // "harvested" - the false-admit + zero-debit exploit. The seam must
        // never be a bridge operand even when a window was open at the stop.
        [Fact]
        public void CheckTransportGains_DockSeam_NeverBridged_DepotContentsNotHarvested()
        {
            RecordingTree tree = BuildKscTree(out Recording root, out Recording merge);
            // Combined-stack start manifest at the dock: 620 Ore (transport's
            // 120 + the colony's 500). If the dock seam bridged, the +500
            // delta would cover any gain.
            merge.RouteRunManifest = CompleteManifest(
                new[] { TransportPid, ColonyPid }, Ore(620.0), Ore(620.0));
            // A stalled drill window closed at the root's stop satisfies the
            // would-be witness condition (c) - the seam must still not bridge.
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(450.0, 499.0, Ore(120.0), Ore(120.0), closedAtStop: true)
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal("Ore", result.RejectResource);
            Assert.Equal(0.0, result.RejectHarvested, 6);
        }

        // BLOCKER-1 scope pin: two pre-dock legs with DIFFERING pid scopes
        // (a part was lost across the seam) must not bridge their boundary
        // delta even with the boundary witness flags set.
        [Fact]
        public void CheckTransportGains_MismatchedPidScopeSeam_NotBridged()
        {
            RecordingTree tree = BuildChainKscTree(
                out Recording leg1, out Recording leg2, out Recording merge,
                leg1EndOre: 0.0, leg2StartOre: 100.0);
            // Leg 1 carried an extra part (150) that is gone on leg 2: the
            // scopes differ, so the +100 boundary delta is unbridgeable.
            leg1.RouteRunManifest = CompleteManifest(
                new[] { TransportPid, 150u }, Ore(0.0), Ore(0.0));
            leg2.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(300.0, 450.0, Ore(100.0), Ore(100.0), openedAtStart: true)
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(100.0, 0.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal("Ore", result.RejectResource);
            Assert.Equal(100.0, result.RejectGained, 6);
            Assert.Equal(0.0, result.RejectHarvested, 6);
        }

        // finding-6 pin: on the start-docked depot run the gain anchor is the
        // undock-split child (the first scope-matching leg), NOT the tree
        // root - the combined root's depot tanks must never mask gains.
        [Fact]
        public void CheckTransportGains_AnchorIsFirstScopeMatchingLeg_NotTreeRoot()
        {
            // The depot root starts STUFFED with ore (500). Anchored at the
            // root, the gain would read max(0, 120 - 500) = 0 and an
            // unwitnessed drill run would slip through (the inert check the
            // round-2 review caught).
            RecordingTree tree = BuildDepotTree(
                out Recording root, out Recording solo, out Recording merge,
                rootStartOre: 500.0);

            HarvestGainCheckResult uncovered = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, uncovered.Outcome);
            Assert.Equal(120.0, uncovered.RejectGained, 6);
            Assert.Equal("solo", uncovered.AnchorLeg.RecordingId);

            // With the drill witnessed on the solo leg the same run is covered.
            solo.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(150.0, 400.0, Ore(0.0), Ore(120.0))
            };
            HarvestGainCheckResult covered = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.Covered, covered.Outcome);
            Assert.Equal("solo", covered.AnchorLeg.RecordingId);
        }

        // D2 direction pin: an undefined-name positive gain counts as
        // UNACCOUNTED even when a window's raw deltas would cover it - the
        // harvested side is an admission-direction output and excludes
        // undefined names, so the check fails closed.
        [Fact]
        public void CheckTransportGains_UndefinedNameGain_Unaccounted()
        {
            ResourceTransferability.DefinitionLookupOverrideForTesting =
                CrpFixtures.DefinedLookup;
            RecordingTree tree = BuildKscTree(out Recording root, out Recording merge);
            string undefined = CrpFixtures.UninstalledModResource;
            root.RouteRunManifest = CompleteManifest(
                new[] { TransportPid },
                new Dictionary<string, ResourceAmount> { [undefined] = RA(0.0) },
                new Dictionary<string, ResourceAmount> { [undefined] = RA(80.0) });
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(150.0, 400.0,
                    new Dictionary<string, ResourceAmount> { [undefined] = RA(0.0) },
                    new Dictionary<string, ResourceAmount> { [undefined] = RA(80.0) })
            };
            RouteConnectionWindow window = ColonyWindow(0.0, 0.0);
            window.DockTransportResources =
                new Dictionary<string, ResourceAmount> { [undefined] = RA(80.0) };
            window.UndockTransportResources =
                new Dictionary<string, ResourceAmount> { [undefined] = RA(80.0) };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, window, RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal(undefined, result.RejectResource);
            Assert.Equal(80.0, result.RejectGained, 6);
            Assert.Equal(0.0, result.RejectHarvested, 6);
        }

        // D3-rule-3 pin: a VOIDED manifest (BG transit cleared it to null)
        // degrades the whole tree to the legacy analysis.
        [Fact]
        public void CheckTransportGains_VoidedManifest_LegacyPath()
        {
            RecordingTree tree = BuildDepotTree(out _, out Recording solo, out Recording merge);
            solo.RouteRunManifest = null; // voided on background transition

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.LegacyFallback, result.Outcome);
            Assert.Contains("manifest-missing", result.LegacyReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") &&
                l.Contains("legacy analysis") &&
                l.Contains("manifest-missing"));
        }

        // catches: a pre-M2 recording (no run manifests at all) engaging the
        // gain check instead of degrading.
        [Fact]
        public void CheckTransportGains_NoRunManifest_LegacyPath()
        {
            RecordingTree tree = BuildDepotTree(out Recording root, out Recording solo, out Recording merge);
            root.RouteRunManifest = null;
            solo.RouteRunManifest = null;
            merge.RouteRunManifest = null;

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.LegacyFallback, result.Outcome);
        }

        // round-2 correction 5 pin: a start-only (ForceStop-shaped) manifest
        // is NOT complete - the presence gate requires both halves.
        [Fact]
        public void CheckTransportGains_StartOnlyManifest_LegacyPath()
        {
            RecordingTree tree = BuildDepotTree(out _, out Recording solo, out Recording merge);
            solo.RouteRunManifest.EndCaptured = false;

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.LegacyFallback, result.Outcome);
            Assert.Contains("manifest-incomplete", result.LegacyReason);
        }

        // round-2 correction 2b pin: pre-anchor windows (drilling while still
        // docked at the origin depot) must not inflate the harvested total.
        [Fact]
        public void CheckTransportGains_PreAnchorWindow_DoesNotInflateHarvested()
        {
            RecordingTree tree = BuildDepotTree(out Recording root, out Recording solo, out Recording merge);
            // Window on the combined ROOT (before the undock-split anchor at
            // UT 100): its delta must be excluded by the span filter.
            root.RouteHarvestWindows = new List<RouteHarvestWindow>
            {
                HarvestWindow(10.0, 90.0, Ore(0.0), Ore(120.0))
            };

            HarvestGainCheckResult result = RouteHarvestAnalysis.CheckTransportGains(
                tree, merge, ColonyWindow(120.0, 20.0), RouteAnalysisLogMode.Diagnostic);

            Assert.Equal(HarvestGainOutcome.UntrackedGain, result.Outcome);
            Assert.Equal(0.0, result.RejectHarvested, 6);
        }

        // ------------------------------------------------------------------
        // Chain-seam fixture (KSC root -> chain link -> leg2 -> Dock -> merge)
        // ------------------------------------------------------------------

        private static RecordingTree BuildChainKscTree(
            out Recording leg1, out Recording leg2, out Recording merge,
            double leg1EndOre, double leg2StartOre)
        {
            leg1 = new Recording
            {
                RecordingId = "leg1",
                TreeId = "tree-chain",
                ExplicitStartUT = 0.0,
                ExplicitEndUT = 300.0,
                StartBodyName = "Kerbin",
                LaunchSiteName = "LaunchPad",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid }, Ore(0.0), Ore(leg1EndOre))
            };
            leg2 = new Recording
            {
                RecordingId = "leg2",
                TreeId = "tree-chain",
                ExplicitStartUT = 300.0,
                ExplicitEndUT = DockUT,
                // Chain-segment link: ParentRecordingId, no branch point.
                ParentRecordingId = "leg1",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid }, Ore(leg2StartOre), Ore(120.0))
            };
            merge = new Recording
            {
                RecordingId = "merge",
                TreeId = "tree-chain",
                ExplicitStartUT = DockUT,
                ExplicitEndUT = UndockUT,
                ParentBranchPointId = "bp-dock",
                RouteRunManifest = CompleteManifest(
                    new[] { TransportPid, ColonyPid }, Ore(120.0), Ore(120.0))
            };
            var tree = new RecordingTree
            {
                Id = "tree-chain",
                RootRecordingId = leg1.RecordingId,
                ActiveRecordingId = merge.RecordingId
            };
            tree.AddOrReplaceRecording(leg1);
            tree.AddOrReplaceRecording(leg2);
            tree.AddOrReplaceRecording(merge);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock",
                UT = DockUT,
                Type = BranchPointType.Dock,
                ParentRecordingIds = new List<string> { leg2.RecordingId },
                ChildRecordingIds = new List<string> { merge.RecordingId }
            });
            return tree;
        }
    }
}
