using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The go-back rewind's route state floor (PR #2040 review). The go-back keys its route
    /// cutoff to the rewind save's own UT, up to the 15 s lead-time windback after the clock, so
    /// the route rows it keeps in that window lie ahead of the clock. Every load that retires
    /// route rows after its own UT would take them for an abandoned future: a scene change inside
    /// the window (KSC to the VAB, a launch, an F9 at the KSC) re-ran the in-session reconcile
    /// at the clock, retired them again while the restore put the fired cursor back (the free run
    /// returned) and cleared one-shots armed after the rewind. The floor
    /// (<see cref="RouteStore.StateFloorUT"/>) marks the route state as settled through the
    /// rewind save's UT; saves written inside the window carry it, and every load of such a save
    /// keys its route cutoff to the later of its own UT and the floor. A save without one is a
    /// load back past the window and reconciles at its own UT.
    /// Fixture: span [1000, 1300], cadence 300, dock 1150, so crossings at 1150, 1450, 1750; the
    /// rewind save at 1460 (after the cycle-1 crossing, fired at 1452), the clock wound back to
    /// 1445.
    /// </summary>
    [Collection("Sequential")]
    public class RouteGoBackWindowFloorTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteGoBackWindowFloorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RewindContext.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = amount => { };
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = null;
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static GhostPlaybackLogic.LoopUnit BuildUnit()
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);
        }

        private static Route BuildLoopRoute()
        {
            return new Route
            {
                Id = "route-loop", Name = "route-loop", Status = RouteStatus.Active, CreatedUT = 900.0,
                IsKscOrigin = true, BackingMissionTreeId = "tree", RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec", LoopAnchorUT = 1000.0, LastObservedLoopCycleIndex = -1,
                DispatchInterval = 300.0, TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec", TreeId = "tree", RouteProofHash = "deadbeef" },
                },
            };
        }

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private readonly EligibleEnv env = new EligibleEnv();

        private static Route Snapshot(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            return Route.DeserializeFrom(node);
        }

        private static ConfigNode SaveNow()
        {
            var node = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(node);
            return node;
        }

        private static double[] DispatchUTs()
            => Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched).Select(a => a.UT).ToArray();

        private static Route Live() => RouteStore.CommittedRoutes.Single();

        private void InstallLoop()
        {
            RouteStore.AddRoute(BuildLoopRoute());
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();
            RouteOrchestrator.DeliveryApplierForTesting = (r, currentUT, e) =>
            {
                string cycleId = "cycle-" + (r.CompletedCycles + r.SkippedCycles)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered, UT = currentUT, RouteId = r.Id,
                    RouteCycleId = cycleId, RouteStopIndex = 0, Sequence = 0,
                });
                r.CompletedCycles += 1;
                r.PendingDeliveryUT = null;
                r.PendingStopIndex = -1;
                r.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        /// <summary>
        /// Crossings 0 (1150) and 1 (1452) fired, the rewind save written at 1460, the abandoned
        /// future's crossing 2 at 1755, then the go-back to the clock 1445. Returns the node a
        /// quicksave taken at 1400, before the rewind, would carry.
        /// </summary>
        private ConfigNode FlyThenGoBack()
        {
            InstallLoop();
            RouteOrchestrator.Tick(1150.0, env);
            ConfigNode savedAt1400 = SaveNow();
            RouteOrchestrator.Tick(1452.0, env);
            Route savedAt1460 = Snapshot(Live());
            RouteOrchestrator.Tick(1755.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1755.0 }, DispatchUTs());

            RewindContext.SetRewindSaveRoutes(new[] { savedAt1460 }, 1460.0);
            RouteLoadReconcile.ReconcileAtGoBackRewind(1445.0, RewindContext.RewindSaveRoutes, 1460.0);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            return savedAt1400;
        }

        // ------------------------------------------------------------------
        // The review probe: a forward scene change inside the window
        // ------------------------------------------------------------------

        // catches: the PR #2040 review finding. KSC -> VAB at 1450: OnSave writes the live state,
        // the editor's OnLoad is InSessionOther at 1450, and the kept cycle-1 rows after it read
        // as an abandoned future. The scene change must find nothing after the route state's
        // floor and leave the charge, the cursor and the next crossing alone.
        [Fact]
        public void SceneChangeInsideTheWindow_KeepsTheSettledCrossing_NoFreeRun()
        {
            FlyThenGoBack();
            ConfigNode outgoing = SaveNow();
            Assert.Equal(1460.0, RouteStore.ReadSavedRouteStateFloorUT(outgoing));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1450.0, 1450.0, outgoing);

            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(1, Live().LastObservedLoopCycleIndex);
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
            foreach (double ut in new[] { 1451.0, 1453.0, 1460.0 })
                RouteOrchestrator.Tick(ut, env);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            RouteOrchestrator.Tick(1760.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1760.0 }, DispatchUTs());
        }

        // catches: the second half of the finding (new against main): the same scene change
        // cleared a Send Once and a pause-after-this-run armed after the rewind.
        [Fact]
        public void SceneChangeInsideTheWindow_KeepsOneShotsArmedAfterTheRewind()
        {
            FlyThenGoBack();
            Live().SendOnceArmed = true;                      // armed at 1447
            Live().PauseAfterCurrentCycle = true;
            ConfigNode outgoing = SaveNow();

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1450.0, 1450.0, outgoing);

            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Assert.True(Live().SendOnceArmed);
            Assert.True(Live().PauseAfterCurrentCycle);
        }

        // The reviewer's control, kept: main's cutoff (the clock) leaves nothing after a
        // forward scene change, and writes no floor.
        [Fact]
        public void Control_GoBackWithoutAWindow_ForwardSceneChangeSkips_NoFloor()
        {
            InstallLoop();
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched, UT = 1452.0, RouteId = "route-loop",
                RouteCycleId = "cycle-1", RouteStopIndex = -1, Sequence = 0,
            });
            Route saved = Snapshot(Live());
            RouteLoadReconcile.ReconcileAtGoBackRewind(1445.0, new[] { saved }, 1445.0);
            Assert.True(double.IsNaN(RouteStore.StateFloorUT));
            Live().SendOnceArmed = true;
            ConfigNode outgoing = SaveNow();
            Assert.True(double.IsNaN(RouteStore.ReadSavedRouteStateFloorUT(outgoing)));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.InSessionOther, 1450.0, 1450.0, outgoing);
            Assert.Equal(RouteLoadReconcileOutcome.SkippedNothingAfterCutoff, outcome);
            Assert.True(Live().SendOnceArmed);
        }

        // ------------------------------------------------------------------
        // Loads that do go back
        // ------------------------------------------------------------------

        // catches: the floor shielding a genuine load back past the window. A save from before
        // the rewind carries no floor: the load reconciles at its own UT, retires the window's
        // rows, restores that save's cursor, clears the floor, and the crossing fires once.
        [Theory]
        [InlineData(true)]                                 // F9 in flight: QuickloadFlight
        [InlineData(false)]                                // F9 at the Space Center: InSessionOther
        public void LoadBackPastTheWindow_StillReconciles_TheCrossingFiresOnce(bool inFlight)
        {
            LoadKind kind = inFlight ? LoadKind.QuickloadFlight : LoadKind.InSessionOther;
            ConfigNode savedAt1400 = FlyThenGoBack();
            Assert.True(double.IsNaN(RouteStore.ReadSavedRouteStateFloorUT(savedAt1400)));

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                kind, 1400.0, 1400.0, savedAt1400);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.Equal(new[] { 1150.0 }, DispatchUTs());
            Assert.Equal(0, Live().LastObservedLoopCycleIndex);
            Assert.True(double.IsNaN(RouteStore.StateFloorUT));
            RouteOrchestrator.Tick(1452.0, env);
            RouteOrchestrator.Tick(1460.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
        }

        // catches: an F5 inside the window then F9 losing the window's charge. The quicksave's
        // route copy is the go-back's (cursor fired through 1460), so the quickload must keep the
        // rows up to the floor, retire only what came after it, and keep the floor alive.
        [Fact]
        public void QuickloadIntoASaveWrittenInsideTheWindow_KeepsItsSettledRows()
        {
            FlyThenGoBack();
            ConfigNode f5At1450 = SaveNow();
            RouteOrchestrator.Tick(1755.0, env);              // play on past the floor
            Assert.Equal(new[] { 1150.0, 1452.0, 1755.0 }, DispatchUTs());

            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.QuickloadFlight, 1450.0, 1450.0, f5At1450);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(1, Live().LastObservedLoopCycleIndex);
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
            foreach (double ut in new[] { 1451.0, 1460.0 })
                RouteOrchestrator.Tick(ut, env);
            Assert.Equal(2, DispatchUTs().Length);
            RouteOrchestrator.Tick(1760.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1760.0 }, DispatchUTs());
        }

        // catches: a revert to a launch made inside the window (rewind, VAB, launch, revert):
        // the revert prune dropped every untagged row after the launch, the window's route rows
        // with it, while the launch save's cursor said the crossing fired.
        [Fact]
        public void RevertToALaunchInsideTheWindow_KeepsTheSettledRouteRows()
        {
            FlyThenGoBack();
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.FundsSpending, UT = 1447.0,
                FundsSpendingSource = FundsSpendingSource.VesselBuild, FundsSpent = 100f,
            });
            ConfigNode launchSaveAt1447 = SaveNow();
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.FundsSpending, UT = 1449.0,
                FundsSpendingSource = FundsSpendingSource.KerbalHire, FundsSpent = 50f,
            });
            RouteOrchestrator.Tick(1755.0, env);

            double floor = RouteStore.ReadSavedRouteStateFloorUT(launchSaveAt1447);
            Ledger.PruneOrphanActionsAfterUT(1447.0, inclusive: false, keepRouteRowsThroughUT: floor);
            RouteLoadReconcileOutcome outcome = RouteLoadReconcile.ReconcileAtInSessionLoad(
                LoadKind.StockRevert,
                RouteLoadReconcile.ResolveCutoffUT(LoadKind.StockRevert, 1447.0, 1447.0),
                1447.0, launchSaveAt1447);

            Assert.Equal(RouteLoadReconcileOutcome.Reconciled, outcome);
            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FundsSpending && a.UT == 1449.0);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.FundsSpending && a.UT == 1447.0);
            Assert.Equal(1, Live().LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("[Ledger]")
                && l.Contains("PruneOrphanActionsAfterUT: kept") && l.Contains("route state floor UT 1460"));
            RouteOrchestrator.Tick(1452.0, env);
            Assert.Equal(2, DispatchUTs().Length);
        }

        // catches: a Re-Fly start from a separation inside the window (rewind, launch, stage
        // within 15 s, Re-Fly later): the bundle retired the window's rows at the RP's UT while
        // the RP quicksave's route copy (as of the floor) restored the fired cursor.
        [Fact]
        public void ReFlyStartFromAnRpQuicksaveInsideTheWindow_KeepsTheSettledRows()
        {
            FlyThenGoBack();
            ConfigNode rpQuicksaveAt1450 = SaveNow();
            RouteOrchestrator.Tick(1755.0, env);
            ReconciliationBundle bundle = ReconciliationBundle.Capture();

            double floor = RouteStore.ReadSavedRouteStateFloorUT(rpQuicksaveAt1450);
            double routeStateUT = RouteLoadReconcile.ApplyRouteStateFloor(1450.0, floor);
            ReconciliationBundle.Restore(bundle, 1450.0,
                RouteStore.ReadSavedCommittedRoutes(rpQuicksaveAt1450), routeStateUT);

            Assert.Equal(new[] { 1150.0, 1452.0 }, DispatchUTs());
            Assert.Equal(1, Live().LastObservedLoopCycleIndex);
            RouteOrchestrator.Tick(1452.0, env);
            Assert.Equal(2, DispatchUTs().Length);
            RouteOrchestrator.Tick(1760.0, env);
            Assert.Equal(new[] { 1150.0, 1452.0, 1760.0 }, DispatchUTs());
        }

        // ------------------------------------------------------------------
        // The floor's lifetime and the pure rules
        // ------------------------------------------------------------------

        // catches: the floor outliving the window or missing from it. Set by a go-back with a
        // window only; written by every save while alive; read by the cold load; dropped by a
        // load whose clock has passed it.
        [Fact]
        public void Floor_SetByTheGoBack_CarriedBySaves_DroppedOnceTheClockPassesIt()
        {
            FlyThenGoBack();
            Assert.Equal(1460.0, RouteStore.StateFloorUT);
            ConfigNode saved = SaveNow();
            Assert.Equal("1460", saved.GetValue("routeStateFloorUT"));
            Assert.Contains(logLines, l => l.Contains("OnLoad: go-back route reconcile at cutoff=1460 ")
                && l.Contains("stateFloorUT=1460"));

            RouteStore.StateFloorUT = double.NaN;
            RouteStore.LoadRoutesFrom(saved);                 // the cold path
            Assert.Equal(1460.0, RouteStore.StateFloorUT);

            RouteLoadReconcile.ReconcileAtInSessionLoad(LoadKind.InSessionOther, 1470.0, 1470.0, saved);
            Assert.True(double.IsNaN(RouteStore.StateFloorUT));
            Assert.Null(SaveNow().GetValue("routeStateFloorUT"));
        }

        [Theory]
        [InlineData(1450.0, 1460.0, 1460.0)]
        [InlineData(1500.0, 1460.0, 1500.0)]
        [InlineData(1460.0, 1460.0, 1460.0)]
        [InlineData(1450.0, double.NaN, 1450.0)]
        [InlineData(double.NaN, 1460.0, double.NaN)]
        public void ApplyRouteStateFloor_RaisesAUtBehindTheFloor(double ut, double floor, double expected)
        {
            Assert.Equal(expected, RouteLoadReconcile.ApplyRouteStateFloor(ut, floor));
        }

        [Theory]
        [InlineData(1450.0, 1460.0, 1460.0)]
        [InlineData(1460.0, 1460.0, double.NaN)]
        [InlineData(1500.0, 1460.0, double.NaN)]
        [InlineData(1450.0, double.NaN, double.NaN)]
        [InlineData(double.NaN, 1460.0, double.NaN)]
        public void ResolveFloorAfterLoad_KeepsTheCarriedFloorWhileTheClockIsBehindIt(
            double clock, double floor, double expected)
        {
            Assert.Equal(expected, RouteLoadReconcile.ResolveFloorAfterLoad(clock, floor));
        }

        [Theory]
        [InlineData(1450.0, true, 1460.0, 1460.0)]
        [InlineData(1450.0, true, 1450.0, 1450.0)]
        [InlineData(1450.0, false, 1460.0, 1450.0)]
        [InlineData(1450.0, true, double.NaN, 1450.0)]
        [InlineData(double.PositiveInfinity, true, 1460.0, double.PositiveInfinity)]
        public void ResolveLoadedRouteCutoffUT_RisesToTheCopysUtWhenItIsInHand(
            double cutoff, bool haveCopy, double copyUT, double expected)
        {
            IReadOnlyList<Route> copy = haveCopy ? new List<Route>() : null;
            Assert.Equal(expected, RouteLoadReconcile.ResolveLoadedRouteCutoffUT(cutoff, copy, copyUT));
        }

        // catches: a second go-back to a rewind save written inside the first one's window (a
        // launch in the window): that save's route copy is as of the floor it carries.
        [Fact]
        public void CaptureRewindSaveRoutes_RaisesTheCopysUtToTheFloorTheSaveCarries()
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", "ParsekScenario");
            node.AddValue("scene", "5, 7, 6, 8");
            node.AddValue("routeStateFloorUT", "1470");
            BuildLoopRoute().SerializeInto(node.AddNode("ROUTES").AddNode("ROUTE"));
            var scenarios = new List<ProtoScenarioModule> { new ProtoScenarioModule(node) };

            RouteLoadReconcile.CaptureRewindSaveRoutes(scenarios, 1460.0, "Rewind");

            Assert.Single(RewindContext.RewindSaveRoutes);
            Assert.Equal(1470.0, RewindContext.RewindSaveClockUT);
            Assert.Contains(logLines, l => l.Contains("carriedFloor=1470") && l.Contains("routeStateUT=1470"));
        }

        // ------------------------------------------------------------------
        // Wiring (OnLoad and the Re-Fly post-load are not xUnit-drivable)
        // ------------------------------------------------------------------

        private static string ReadSource(string fileName)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", fileName);
            Assert.True(File.Exists(path), fileName + " not found at " + path);
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (c >= 0)
                    lines[i] = lines[i].Substring(0, c);
            }
            return System.Text.RegularExpressions.Regex.Replace(string.Join("\n", lines), @"\s+", " ");
        }

        [Fact]
        public void RevertPruneAndReFlyStart_ReadTheLoadedSavesFloor()
        {
            string scenario = ReadSource("ParsekScenario.cs");
            Assert.Contains("Ledger.PruneOrphanActionsAfterUT( pruneCutoffUT, pruneInclusive, "
                + "keepRouteRowsThroughUT: Logistics.RouteStore.ReadSavedRouteStateFloorUT(node));", scenario);

            string invoker = ReadSource("RewindInvoker.cs");
            Assert.Contains("loadedFloorUT = Logistics.RouteStore.ReadSavedRouteStateFloorUT(loadedScenarioNode);", invoker);
            Assert.Contains("loadedSaveUT = Logistics.RouteLoadReconcile.ApplyRouteStateFloor(loadedClockUT, loadedFloorUT);", invoker);
            Assert.Contains("Logistics.RouteLoadReconcile.InstallFloorAfterLoad( loadedClockUT, loadedFloorUT, InvokeTag, \"ConsumePostLoad\");", invoker);
        }
    }
}
