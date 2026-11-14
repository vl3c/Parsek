using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Direct branch tests for the three pure pieces of the harvest gain check
    /// (<see cref="RouteHarvestAnalysis.FindGainAnchorIndex"/>,
    /// <see cref="RouteHarvestAnalysis.ComputeFullRunGains"/>,
    /// <see cref="RouteHarvestAnalysis.ComputeHarvestedTotals"/>), which
    /// <c>RouteHarvestAnalysisTests</c> reaches only through
    /// <c>CheckTransportGains</c> and its lineage gate.
    /// </summary>
    [Collection("Sequential")]
    public class RouteHarvestAnalysisComputeTests : IDisposable
    {
        private const uint TransportPid = 100;
        private const uint DepotPid = 200;

        public RouteHarvestAnalysisComputeTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            ResourceTransferability.ResetForTesting();
            // Headless: every name is defined except the one these tests mark
            // undefined (the production null-library fallback would fail open).
            ResourceTransferability.DefinitionLookupOverrideForTesting = name => name != "Kerbalium";
        }

        public void Dispose()
        {
            ResourceTransferability.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // ------------------------------------------------------------------
        // Fixture helpers
        // ------------------------------------------------------------------

        private static Dictionary<string, ResourceAmount> Res(params (string Name, double Amount)[] entries)
        {
            var d = new Dictionary<string, ResourceAmount>();
            foreach (var e in entries)
                d[e.Name] = new ResourceAmount { amount = e.Amount, maxAmount = 1000.0 };
            return d;
        }

        private static RouteRunCargoManifest Manifest(
            uint[] pids,
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end)
        {
            return new RouteRunCargoManifest
            {
                TransportPartPersistentIds = pids != null ? new List<uint>(pids) : null,
                StartTransportResources = start,
                EndTransportResources = end,
                EndCaptured = true
            };
        }

        private static Recording Rec(
            string id, double startUT, double endUT,
            RouteRunCargoManifest manifest,
            params RouteHarvestWindow[] windows)
        {
            return new Recording
            {
                RecordingId = id,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                RouteRunManifest = manifest,
                RouteHarvestWindows = windows.Length > 0 ? new List<RouteHarvestWindow>(windows) : null
            };
        }

        private static RouteHarvestWindow Window(
            double startUT, double endUT,
            Dictionary<string, ResourceAmount> start,
            Dictionary<string, ResourceAmount> end,
            Dictionary<string, double> rates = null,
            bool openedAtStart = false, bool closedAtStop = false)
        {
            return new RouteHarvestWindow
            {
                WindowId = "hw-" + startUT,
                StartUT = startUT,
                EndUT = endUT,
                StartTransportResources = start,
                EndTransportResources = end,
                PlausibleRates = rates,
                OpenedAtRecordingStart = openedAtStart,
                ClosedAtRecordingStop = closedAtStop
            };
        }

        private static RouteHarvestAnalysis.LineageLeg Root(Recording rec)
        {
            return new RouteHarvestAnalysis.LineageLeg { Rec = rec, HasParentSeam = false };
        }

        private static RouteHarvestAnalysis.LineageLeg Chain(Recording rec)
        {
            return new RouteHarvestAnalysis.LineageLeg { Rec = rec, HasParentSeam = true, ChainSeam = true };
        }

        private static RouteHarvestAnalysis.LineageLeg Seam(Recording rec, BranchPointType type)
        {
            return new RouteHarvestAnalysis.LineageLeg
            {
                Rec = rec, HasParentSeam = true, ChainSeam = false, SeamType = type
            };
        }

        private static Recording ScopeLeg(string id, params uint[] pids)
        {
            return Rec(id, 0.0, 1.0, Manifest(pids, Res(), Res()));
        }

        private static HashSet<uint> WindowScope() => new HashSet<uint> { TransportPid };

        private static Dictionary<string, double> Totals(
            List<RouteHarvestAnalysis.LineageLeg> lineage, int anchorIdx, int arrivalIdx,
            double anchorStartUT, double dockUT,
            out RouteHarvestWindow first, out Dictionary<string, double> plausible,
            out Dictionary<string, double> slack)
        {
            return RouteHarvestAnalysis.ComputeHarvestedTotals(
                lineage, anchorIdx, arrivalIdx, anchorStartUT, dockUT,
                out first, out plausible, out slack);
        }

        // ------------------------------------------------------------------
        // FindGainAnchorIndex
        // ------------------------------------------------------------------

        [Fact]
        public void FindGainAnchorIndex_ArrivalIsFirstLeg_ReturnsZero()
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg> { Root(ScopeLeg("a", TransportPid)) };
            Assert.Equal(0, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 0, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_MatchingScopeChain_WalksToTheRoot()
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                Chain(ScopeLeg("b", TransportPid)),
                Seam(ScopeLeg("c", TransportPid), BranchPointType.JointBreak),
            };
            Assert.Equal(0, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 2, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_LegWithoutParentSeam_StopsThere()
        {
            var noSeam = new RouteHarvestAnalysis.LineageLeg { Rec = ScopeLeg("b", TransportPid) };
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                noSeam,
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 1, WindowScope()));
        }

        [Theory]
        [InlineData(BranchPointType.Undock)]
        [InlineData(BranchPointType.Dock)]
        [InlineData(BranchPointType.Board)]
        [InlineData(BranchPointType.Launch)]
        [InlineData(BranchPointType.Terminal)]
        public void FindGainAnchorIndex_CompositionOrFlightBoundary_BlocksEvenWithMatchingScope(BranchPointType type)
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                Seam(ScopeLeg("b", TransportPid), type),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 1, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_ChainSeamIgnoresItsSeamType()
        {
            // A chain seam carries no branch point; a stale SeamType value on it
            // must not read as a blocking seam.
            var chainWithDockType = new RouteHarvestAnalysis.LineageLeg
            {
                Rec = ScopeLeg("b", TransportPid), HasParentSeam = true, ChainSeam = true,
                SeamType = BranchPointType.Dock
            };
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                chainWithDockType,
            };
            Assert.Equal(0, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 1, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_SupersetScope_ExtendsOnlyAcrossChainOrContinuation()
        {
            // Earlier leg carries the transport plus a depot part (a superset).
            var acrossChain = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid, DepotPid)),
                Chain(ScopeLeg("b", TransportPid)),
            };
            Assert.Equal(0, RouteHarvestAnalysis.FindGainAnchorIndex(acrossChain, 1, WindowScope()));

            var acrossContinuation = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid, DepotPid)),
                Seam(ScopeLeg("b", TransportPid), BranchPointType.VesselSwitchContinuation),
            };
            Assert.Equal(0, RouteHarvestAnalysis.FindGainAnchorIndex(acrossContinuation, 1, WindowScope()));

            var acrossBreakup = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid, DepotPid)),
                Seam(ScopeLeg("b", TransportPid), BranchPointType.JointBreak),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(acrossBreakup, 1, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_EarlierScopeMissingOrDisjoint_Stops()
        {
            var noManifest = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 1.0, null)),
                Chain(ScopeLeg("b", TransportPid)),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(noManifest, 1, WindowScope()));

            var nullRec = new List<RouteHarvestAnalysis.LineageLeg>
            {
                new RouteHarvestAnalysis.LineageLeg { Rec = null },
                Chain(ScopeLeg("b", TransportPid)),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(nullRec, 1, WindowScope()));

            var disjoint = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", DepotPid)),
                Chain(ScopeLeg("b", TransportPid)),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(disjoint, 1, WindowScope()));
        }

        [Fact]
        public void FindGainAnchorIndex_NullWindowScope_NeverExtends()
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                Chain(ScopeLeg("b", TransportPid)),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 1, null));
        }

        [Fact]
        public void FindGainAnchorIndex_StopsAtTheFirstFailingSeamOnTheWayBack()
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(ScopeLeg("a", TransportPid)),
                Seam(ScopeLeg("b", TransportPid), BranchPointType.Undock),
                Chain(ScopeLeg("c", TransportPid)),
                Chain(ScopeLeg("d", TransportPid)),
            };
            Assert.Equal(1, RouteHarvestAnalysis.FindGainAnchorIndex(lineage, 3, WindowScope()));
        }

        // ------------------------------------------------------------------
        // ComputeFullRunGains
        // ------------------------------------------------------------------

        private static RouteConnectionWindow DockWindow(Dictionary<string, ResourceAmount> dock)
        {
            return new RouteConnectionWindow { WindowId = "w", DockTransportResources = dock };
        }

        [Fact]
        public void ComputeFullRunGains_NullWindow_Empty()
        {
            var gains = RouteHarvestAnalysis.ComputeFullRunGains(
                Manifest(new[] { TransportPid }, Res(("Ore", 5.0)), Res()), null);
            Assert.Empty(gains);
        }

        [Fact]
        public void ComputeFullRunGains_NullAnchorManifest_GainIsTheDockAmount()
        {
            var gains = RouteHarvestAnalysis.ComputeFullRunGains(null, DockWindow(Res(("Ore", 40.0))));
            Assert.Single(gains);
            Assert.Equal(40.0, gains["Ore"], 6);
        }

        [Fact]
        public void ComputeFullRunGains_MultiResource_UnionClampedAtZero()
        {
            var anchor = Manifest(new[] { TransportPid },
                Res(("Ore", 10.0), ("LiquidFuel", 90.0), ("Oxidizer", 5.0)), Res());
            var gains = RouteHarvestAnalysis.ComputeFullRunGains(anchor,
                DockWindow(Res(("Ore", 70.0), ("LiquidFuel", 30.0), ("MonoPropellant", 12.5))));

            Assert.Equal(4, gains.Count);
            Assert.Equal(60.0, gains["Ore"], 6);
            // A burn spends fuel: a negative delta is no gain, but the name stays.
            Assert.Equal(0.0, gains["LiquidFuel"], 6);
            // Only at the anchor start: gone at the dock.
            Assert.Equal(0.0, gains["Oxidizer"], 6);
            // Only at the dock: the whole amount is gain.
            Assert.Equal(12.5, gains["MonoPropellant"], 6);
        }

        [Fact]
        public void ComputeFullRunGains_EnvironmentalResourcesSkipped_UndefinedNamesKept()
        {
            var anchor = Manifest(new[] { TransportPid },
                Res(("ElectricCharge", 0.0), ("IntakeAir", 0.0)), Res());
            var gains = RouteHarvestAnalysis.ComputeFullRunGains(anchor,
                DockWindow(Res(("ElectricCharge", 100.0), ("IntakeAir", 3.0), ("Kerbalium", 7.0))));

            Assert.False(gains.ContainsKey("ElectricCharge"));
            Assert.False(gains.ContainsKey("IntakeAir"));
            // Rejection direction: an undefined name must stay in and fail closed.
            Assert.Equal(7.0, gains["Kerbalium"], 6);
        }

        [Fact]
        public void ComputeFullRunGains_NoDockManifest_AnchorNamesReadAsZeroGain()
        {
            var anchor = Manifest(new[] { TransportPid }, Res(("Ore", 10.0)), Res());
            var gains = RouteHarvestAnalysis.ComputeFullRunGains(anchor, DockWindow(null));
            Assert.Single(gains);
            Assert.Equal(0.0, gains["Ore"], 6);
        }

        // ------------------------------------------------------------------
        // ComputeHarvestedTotals
        // ------------------------------------------------------------------

        [Fact]
        public void ComputeHarvestedTotals_EmptyLineage_AllEmpty()
        {
            var harvested = Totals(new List<RouteHarvestAnalysis.LineageLeg>(), 0, -1, 0.0, 500.0,
                out RouteHarvestWindow first, out var plausible, out var slack);
            Assert.Empty(harvested);
            Assert.Null(first);
            Assert.Empty(plausible);
            Assert.Empty(slack);
        }

        [Fact]
        public void ComputeHarvestedTotals_LegsWithoutWindows_AllEmpty()
        {
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 500.0, Manifest(new[] { TransportPid }, Res(("Ore", 0.0)), Res(("Ore", 0.0))))),
            };
            var harvested = Totals(lineage, 0, 0, 0.0, 500.0,
                out RouteHarvestWindow first, out var plausible, out var slack);
            Assert.Empty(harvested);
            Assert.Null(first);
            Assert.Empty(plausible);
            Assert.Empty(slack);
        }

        [Fact]
        public void ComputeHarvestedTotals_SingleRatedWindow_HarvestPlausibleAndSlack()
        {
            var rates = new Dictionary<string, double> { ["Ore"] = 0.1 };
            RouteHarvestWindow w = Window(100.0, 200.0, Res(("Ore", 0.0)), Res(("Ore", 50.0)), rates);
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 500.0, Manifest(new[] { TransportPid }, Res(("Ore", 0.0)), Res(("Ore", 50.0))), w)),
            };
            var harvested = Totals(lineage, 0, 0, 0.0, 500.0,
                out RouteHarvestWindow first, out var plausible, out var slack);

            Assert.Same(w, first);
            Assert.Equal(50.0, harvested["Ore"], 6);
            // 0.1 Ore/s x 100 s x 1.5 margin.
            Assert.Equal(15.0, plausible["Ore"], 6);
            // 0.1 Ore/s x 0.08 s x 1.5 per edge, two edges.
            Assert.Equal(0.1 * RouteHarvestCapture.HarvestEdgeFrameSeconds
                * RouteHarvestCapture.PlausibilityMargin * RouteHarvestCapture.HarvestWindowEdges,
                slack["Ore"], 9);
        }

        [Fact]
        public void ComputeHarvestedTotals_SpanFilterIsInclusiveAndByWindowStart()
        {
            RouteHarvestWindow before = Window(99.0, 150.0, Res(("Ore", 0.0)), Res(("Ore", 1.0)));
            RouteHarvestWindow atAnchor = Window(100.0, 150.0, Res(("Ore", 0.0)), Res(("Ore", 2.0)));
            RouteHarvestWindow atDock = Window(500.0, 600.0, Res(("Ore", 0.0)), Res(("Ore", 4.0)));
            RouteHarvestWindow after = Window(500.5, 600.0, Res(("Ore", 0.0)), Res(("Ore", 8.0)));
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 600.0, Manifest(new[] { TransportPid }, Res(), Res()),
                    after, atDock, before, atAnchor)),
            };
            var harvested = Totals(lineage, 0, 0, 100.0, 500.0,
                out RouteHarvestWindow first, out _, out _);

            // Only the two windows that START inside [anchorStart, dock].
            Assert.Equal(6.0, harvested["Ore"], 6);
            // Earliest in-span start, whatever the list order.
            Assert.Same(atAnchor, first);
        }

        [Fact]
        public void ComputeHarvestedTotals_LegsAfterArrivalNeverCount()
        {
            // The dock-merge child's window opened at exactly the dock UT lies in
            // the inclusive span, but the leg scan stops at the arrival leg.
            RouteHarvestWindow mergeWindow = Window(500.0, 600.0, Res(("Ore", 0.0)), Res(("Ore", 30.0)));
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 500.0, Manifest(new[] { TransportPid }, Res(), Res()))),
                Seam(Rec("merge", 500.0, 600.0, Manifest(new[] { TransportPid, DepotPid }, Res(), Res()), mergeWindow),
                    BranchPointType.Dock),
            };
            var harvested = Totals(lineage, 0, 0, 0.0, 500.0,
                out RouteHarvestWindow first, out _, out _);
            Assert.Empty(harvested);
            Assert.Null(first);
        }

        [Fact]
        public void ComputeHarvestedTotals_NullNaNAndOpenWindows()
        {
            RouteHarvestWindow nanStart = Window(double.NaN, 150.0, Res(("Ore", 0.0)), Res(("Ore", 9.0)));
            // Still open: no end manifest, so it harvests nothing but is the first window.
            RouteHarvestWindow open = Window(120.0, double.NaN, Res(("Ore", 0.0)), null,
                new Dictionary<string, double> { ["Ore"] = 1.0 });
            var rec = Rec("a", 0.0, 500.0, Manifest(new[] { TransportPid }, Res(), Res()), nanStart, open);
            rec.RouteHarvestWindows.Insert(0, null);
            var lineage = new List<RouteHarvestAnalysis.LineageLeg> { Root(rec) };

            var harvested = Totals(lineage, 0, 0, 0.0, 500.0,
                out RouteHarvestWindow first, out var plausible, out var slack);
            Assert.Empty(harvested);
            Assert.Same(open, first);
            Assert.Empty(plausible);
            Assert.Empty(slack);
        }

        [Fact]
        public void ComputeHarvestedTotals_MultiResourceWindows_RoutablePositiveDeltasOnly()
        {
            RouteHarvestWindow w1 = Window(100.0, 200.0,
                Res(("Ore", 10.0), ("LiquidFuel", 50.0), ("ElectricCharge", 0.0)),
                Res(("Ore", 40.0), ("LiquidFuel", 20.0), ("ElectricCharge", 80.0), ("Kerbalium", 3.0)));
            RouteHarvestWindow w2 = Window(300.0, 400.0,
                Res(("Ore", 40.0)),
                Res(("Ore", 45.0), ("MonoPropellant", 2.0)));
            var lineage = new List<RouteHarvestAnalysis.LineageLeg>
            {
                Root(Rec("a", 0.0, 500.0, Manifest(new[] { TransportPid }, Res(), Res()), w1, w2)),
            };
            var harvested = Totals(lineage, 0, 0, 0.0, 500.0,
                out RouteHarvestWindow first, out var plausible, out _);

            Assert.Equal(2, harvested.Count);
            Assert.Equal(35.0, harvested["Ore"], 6);
            Assert.Equal(2.0, harvested["MonoPropellant"], 6);
            // Unrated windows: plausible equals the witnessed harvest.
            Assert.Equal(35.0, plausible["Ore"], 6);
            Assert.Equal(2.0, plausible["MonoPropellant"], 6);
            Assert.Same(w1, first);
        }

        /// <summary>Two chain legs with the same scope, a gap between them, and Ore rising across it.</summary>
        private static List<RouteHarvestAnalysis.LineageLeg> BridgeLineage(
            double leg1End, double leg2Start, double leg1EndOre, double leg2StartOre,
            RouteHarvestWindow leg1Window, RouteHarvestWindow leg2Window,
            uint[] leg2Pids = null)
        {
            Recording leg1 = Rec("leg1", 0.0, leg1End,
                Manifest(new[] { TransportPid }, Res(("Ore", 0.0)), Res(("Ore", leg1EndOre), ("LiquidFuel", 50.0))));
            Recording leg2 = Rec("leg2", leg2Start, 500.0,
                Manifest(leg2Pids ?? new[] { TransportPid },
                    Res(("Ore", leg2StartOre), ("LiquidFuel", 40.0), ("ElectricCharge", 100.0), ("Kerbalium", 5.0)),
                    Res(("Ore", leg2StartOre))));
            if (leg1Window != null) leg1.RouteHarvestWindows = new List<RouteHarvestWindow> { leg1Window };
            if (leg2Window != null) leg2.RouteHarvestWindows = new List<RouteHarvestWindow> { leg2Window };
            return new List<RouteHarvestAnalysis.LineageLeg> { Root(leg1), Chain(leg2) };
        }

        [Fact]
        public void ComputeHarvestedTotals_BridgedGap_DeltaAndRateTimesGapPlausible()
        {
            var rates = new Dictionary<string, double> { ["Ore"] = 0.5 };
            RouteHarvestWindow w1 = Window(100.0, 300.0, Res(("Ore", 0.0)), Res(("Ore", 20.0)), rates,
                closedAtStop: true);
            var lineage = BridgeLineage(300.0, 310.0, 20.0, 50.0, w1, null);

            var harvested = Totals(lineage, 0, 1, 0.0, 500.0,
                out _, out var plausible, out var slack);

            // 20 (window) + 30 (bridged boundary). Fuel fell, EC is environmental and
            // Kerbalium is undefined: none of them is credited at the boundary.
            Assert.Single(harvested);
            Assert.Equal(50.0, harvested["Ore"], 6);
            // Window: min(20, 0.5 x 200 x 1.5 = 150) = 20; bridge: min(30, 0.5 x 10 x 1.5 = 7.5).
            Assert.Equal(27.5, plausible["Ore"], 6);
            // Bridges add no edge slack: only the one window's.
            Assert.Equal(0.5 * RouteHarvestCapture.HarvestEdgeFrameSeconds
                * RouteHarvestCapture.PlausibilityMargin * RouteHarvestCapture.HarvestWindowEdges,
                slack["Ore"], 9);
        }

        [Fact]
        public void ComputeHarvestedTotals_BridgeViaNextLegOpenedAtStart_ZeroGapPlausibleIsZero()
        {
            var rates = new Dictionary<string, double> { ["Ore"] = 1.0 };
            RouteHarvestWindow w2 = Window(300.0, 400.0, Res(("Ore", 50.0)), Res(("Ore", 60.0)), rates,
                openedAtStart: true);
            var lineage = BridgeLineage(300.0, 300.0, 20.0, 50.0, null, w2);

            var harvested = Totals(lineage, 0, 1, 0.0, 500.0, out _, out var plausible, out _);

            Assert.Equal(40.0, harvested["Ore"], 6);   // 30 bridged + 10 window
            Assert.Equal(10.0, plausible["Ore"], 6);   // the zero-length seam limits to 0
        }

        [Fact]
        public void ComputeHarvestedTotals_BridgeWindowWithoutRates_PlausibleUncapped()
        {
            RouteHarvestWindow w1 = Window(100.0, 300.0, Res(("Ore", 0.0)), Res(("Ore", 20.0)),
                closedAtStop: true);
            var lineage = BridgeLineage(300.0, 300.0, 20.0, 50.0, w1, null);

            var harvested = Totals(lineage, 0, 1, 0.0, 500.0, out _, out var plausible, out _);
            Assert.Equal(50.0, harvested["Ore"], 6);
            Assert.Equal(50.0, plausible["Ore"], 6);
        }

        [Fact]
        public void ComputeHarvestedTotals_BridgeRefusals()
        {
            RouteHarvestWindow closedAtStop() =>
                Window(100.0, 300.0, Res(("Ore", 0.0)), Res(("Ore", 20.0)), closedAtStop: true);

            // No converter witnessed at the boundary: the +30 stays unaccounted.
            RouteHarvestWindow notAtStop = Window(100.0, 300.0, Res(("Ore", 0.0)), Res(("Ore", 20.0)));
            Assert.Equal(20.0, Totals(BridgeLineage(300.0, 310.0, 20.0, 50.0, notAtStop, null),
                0, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);

            // Pid scopes differ across the seam.
            Assert.Equal(20.0, Totals(BridgeLineage(300.0, 310.0, 20.0, 50.0, closedAtStop(), null,
                    new[] { TransportPid, DepotPid }),
                0, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);

            // The seam is not strictly before the dock.
            Assert.Equal(20.0, Totals(BridgeLineage(300.0, 500.0, 20.0, 50.0, closedAtStop(), null),
                0, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);

            // The seam lies before the anchor: only anchor..arrival seams bridge.
            Assert.Equal(20.0, Totals(BridgeLineage(300.0, 310.0, 20.0, 50.0, closedAtStop(), null),
                1, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);

            // A falling boundary delta is never harvest.
            var falling = Totals(BridgeLineage(300.0, 310.0, 20.0, 5.0, closedAtStop(), null),
                0, 1, 0.0, 500.0, out _, out _, out _);
            Assert.Equal(20.0, falling["Ore"], 6);
        }

        [Fact]
        public void ComputeHarvestedTotals_BridgeSkipsLegsWithoutManifests()
        {
            RouteHarvestWindow w1 = Window(100.0, 300.0, Res(("Ore", 0.0)), Res(("Ore", 20.0)),
                closedAtStop: true);
            var lineage = BridgeLineage(300.0, 310.0, 20.0, 50.0, w1, null);
            lineage[1].Rec.RouteRunManifest.StartTransportResources = null;
            Assert.Equal(20.0, Totals(lineage, 0, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);

            lineage[0].Rec.RouteRunManifest = null;
            Assert.Equal(20.0, Totals(lineage, 0, 1, 0.0, 500.0, out _, out _, out _)["Ore"], 6);
        }
    }
}
