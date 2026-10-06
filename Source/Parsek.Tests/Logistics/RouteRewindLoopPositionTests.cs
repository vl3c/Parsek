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
    /// The rewind exits' loop-position restore (todo
    /// ROUTE-REWIND-CURSOR-RESET-REFIRES-LATEST-CROSSING). Both exits run the shared
    /// <see cref="RouteRewindClassifier.ReconcileStoreAtRewind"/>, whose cursor reset (-1) made
    /// the first tick re-fire the crossing that most recently passed before the cutoff under a
    /// fresh cycle id the dispatch dedup cannot match: delivered and charged twice. Each exit now
    /// gives every kept route its loop position back from the loaded save's own route copy:
    /// the go-back rewind from the rewind save it parsed before the scene load
    /// (<see cref="RouteLoadReconcile.CaptureRewindSaveRoutes"/> into
    /// <see cref="RewindContext.RewindSaveRoutes"/>, consumed by <c>HandleRewindOnLoad</c>), the
    /// Re-Fly start from the RP quicksave's OnLoad node through
    /// <see cref="ReconciliationBundle.Restore(ReconciliationBundle, double, IReadOnlyList{Route}, double)"/>.
    /// The go-back exit is driven in its <c>HandleRewindOnLoad</c> call shape (OnLoad is not
    /// xUnit-drivable; the source gates below pin that shape and the two capture sites).
    /// </summary>
    [Collection("Sequential")]
    public class RouteRewindLoopPositionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<double> liveCredits = new List<double>();

        public enum Exit
        {
            GoBack,
            ReFlyStart,
        }

        public RouteRewindLoopPositionTests()
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
            RouteOrchestrator.RecoveryCreditFunderForTesting = amount => liveCredits.Add(amount);
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

        // span [1000, 1300] (300 s); cadence == span, so one crossing == one cycle; dock UT 1150.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: phaseAnchorUT);
        }

        // Mirrors ApplyDelivery's observable contract without a live Vessel; the dispatch
        // half runs for real.
        private static void InstallFakeDeliveryApplier()
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                });
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        private static Route BuildLoopRoute(string id = "route-loop", double createdUT = 900.0)
        {
            return new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = RouteStatus.Active,
                CreatedUT = createdUT,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-" + id,
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-" + id,
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
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
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
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

        // The route as a save written at this moment carries it.
        private static Route SnapshotAsSaved(Route route)
        {
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Route copy = Route.DeserializeFrom(node);
            Assert.NotNull(copy);
            return copy;
        }

        private static List<GameAction> DispatchRows()
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched).ToList();
        }

        /// <summary>
        /// Runs one rewind exit at <paramref name="cutoffUT"/> with the loaded save's route copy.
        /// GoBack: the capture <c>ExecuteRewindSaveLoad</c> makes, then <c>HandleRewindOnLoad</c>'s
        /// route block (retire, shared reconcile, restore from the carried copy). ReFlyStart: the
        /// bundle captured before the load, restored at the post-load UT.
        /// </summary>
        private static void RunExit(
            Exit exit, double cutoffUT, IReadOnlyList<Route> savedRoutes, double loadedSaveUT)
        {
            if (exit == Exit.GoBack)
            {
                RewindContext.SetRewindSaveRoutes(savedRoutes, loadedSaveUT);
                Ledger.RetireFutureRouteActionsAtRewind(cutoffUT, out List<GameAction> kept);
                RouteRewindClassifier.ReconcileStoreAtRewind(
                    new List<Route>(RouteStore.CommittedRoutes),
                    new List<Route>(RouteStore.DormantRoutes),
                    cutoffUT,
                    kept,
                    logTag: "Rewind",
                    logPrefix: "OnLoad go-back");
                RouteLoadReconcile.RestoreLoopPositionAtRewindExit(
                    RewindContext.RewindSaveRoutes,
                    cutoffUT,
                    RewindContext.RewindSaveClockUT,
                    logTag: "Rewind",
                    logPrefix: "OnLoad go-back");
                return;
            }

            ReconciliationBundle bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, cutoffUT, savedRoutes, loadedSaveUT);
        }

        private static Route Committed(string id)
            => RouteStore.CommittedRoutes.Single(r => r.Id == id);

        // ------------------------------------------------------------------
        // The double fire
        // ------------------------------------------------------------------

        // catches: the defect. The save sits at 1200, between the cycle-0 crossing (dock 1150,
        // dispatched before the save, its cargo in the loaded world) and the cycle-1 crossing
        // (1450, the abandoned future). The cursor reset made the first tick after the rewind
        // fire crossing 0 again under cycle-1's fresh id: delivered and charged twice.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void CrossingJustBeforeTheCutoff_FiresOnceNotTwice(Exit exit)
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);               // cycle-0 crossing
            Route savedAt1200 = SnapshotAsSaved(route);
            RouteOrchestrator.Tick(1450.0, env);               // cycle-1, the abandoned future
            Assert.Equal(2, DispatchRows().Count);

            RunExit(exit, 1200.0, new[] { savedAt1200 }, 1200.0);
            Route live = Committed(route.Id);

            RouteOrchestrator.Tick(1201.0, env);               // the first tick after the rewind
            var single = Assert.Single(DispatchRows());
            Assert.Equal(1150.0, single.UT);
            Assert.Equal(0, live.LastObservedLoopCycleIndex);

            RouteOrchestrator.Tick(1460.0, env);               // the re-flown cycle-1 crossing
            var rows = DispatchRows();
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, a => a.UT == 1460.0);        // owed after the cutoff: fires once
            Assert.Equal(2, rows.Select(a => a.RouteCycleId).Distinct().Count());
            Assert.Equal(1, live.LastObservedLoopCycleIndex);
        }

        // catches: the mirror. With nothing on the route after the save the exit must leave the
        // loop position as it was (a same-instant Re-Fly, a rewind straight back to the save):
        // the reset alone re-fired the last crossing even here.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void NothingAfterTheSave_LoopPositionUnchanged_NoFire(Exit exit)
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            Route savedAt1200 = SnapshotAsSaved(route);
            long cursorBefore = route.LastObservedLoopCycleIndex;
            long stopCursorBefore = route.Stops[0].LastFiredCycleIndex;
            double anchorBefore = route.LoopAnchorUT;

            RunExit(exit, 1200.0, new[] { savedAt1200 }, 1200.0);
            Route live = Committed(route.Id);

            Assert.Equal(cursorBefore, live.LastObservedLoopCycleIndex);
            Assert.Equal(stopCursorBefore, live.Stops[0].LastFiredCycleIndex);
            Assert.Equal(anchorBefore, live.LoopAnchorUT);
            RouteOrchestrator.Tick(1201.0, env);
            Assert.Single(DispatchRows());
        }

        // ------------------------------------------------------------------
        // Loop anchor and partner cursor
        // ------------------------------------------------------------------

        // catches: the anchor left in the abandoned future. TryActivate after the save moved
        // LoopAnchorUT (the loop clock's phase anchor, the cursors' index space) to 2600; a
        // saved cursor read against it, or a reset against it, holds every crossing owed
        // between the save and the re-activation.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void ReactivationAfterTheSave_TheSavedAnchorComesBackWithTheCursor(Exit exit)
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            RouteOrchestrator.LoopUnitResolverForTesting =
                (r, ut) => BuildUnit(phaseAnchorUT: Math.Max(r.LoopAnchorUT, 1300.0));
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            // Anchor 1000 -> phase 1300: dock instants 1450, 1750, 2050, 2350, 2650, 2950, ...
            foreach (double ut in new[] { 1450.0, 1750.0, 2050.0, 2350.0 })
                RouteOrchestrator.Tick(ut, env);
            Assert.Equal(3, route.LastObservedLoopCycleIndex);
            Route savedAt2400 = SnapshotAsSaved(route);

            Assert.True(RouteOrchestrator.TryPause(route, 2500.0, env));
            Assert.True(RouteOrchestrator.TryActivate(route, 2600.0));
            Assert.Equal(2600.0, route.LoopAnchorUT);
            RouteOrchestrator.Tick(2760.0, env);
            Assert.Equal(5, DispatchRows().Count);

            RunExit(exit, 2400.0, new[] { savedAt2400 }, 2400.0);
            Route live = Committed(route.Id);

            Assert.Equal(1000.0, live.LoopAnchorUT);
            Assert.Equal(3, live.LastObservedLoopCycleIndex);
            for (double ut = 2660.0; ut <= 3700.0; ut += 100.0)
                RouteOrchestrator.Tick(ut, env);

            // The crossings at 2650, 2950, 3250 and 3550 each fire once, on the first tick past
            // their dock instant; none before the cutoff fires again.
            var reflown = DispatchRows().Where(a => a.UT > 2400.0).Select(a => a.UT).ToList();
            Assert.Equal(new[] { 2660.0, 2960.0, 3260.0, 3560.0 }, reflown);
            Assert.Equal(4, DispatchRows().Count(a => a.UT <= 2400.0));
        }

        // catches: a linked pair held by the abandoned future's alternation cursor. The pair
        // alternates on LastConsumedPartnerCycle against the partner's CompletedCycles, which the
        // reconcile rebuilds from the kept rows; left above it, both routes wait forever.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void PartnerCursor_ComesBackForAnUnchangedLink(Exit exit)
        {
            var a = BuildLoopRoute("a");
            var b = BuildLoopRoute("b");
            a.LinkedRouteId = "b";
            b.LinkedRouteId = "a";
            a.LastConsumedPartnerCycle = 1;
            b.LastConsumedPartnerCycle = 1;
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            Route savedA = SnapshotAsSaved(a);
            Route savedB = SnapshotAsSaved(b);
            a.LastConsumedPartnerCycle = 4;                    // the abandoned future
            b.LastConsumedPartnerCycle = 3;

            RunExit(exit, 1200.0, new[] { savedA, savedB }, 1200.0);

            Assert.Equal(1, Committed("a").LastConsumedPartnerCycle);
            Assert.Equal(1, Committed("b").LastConsumedPartnerCycle);
            Assert.Equal("b", Committed("a").LinkedRouteId);
        }

        // ------------------------------------------------------------------
        // Mirror cases: changed clock, route created after the save, no save copy
        // ------------------------------------------------------------------

        // catches: a saved cursor restored into another index space. A cadence change after the
        // save rebases the cursors; the reset stays (the next crossing fires) while the anchor
        // still goes back, as on the in-session load.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void ClockChangedAfterTheSave_CursorsStayReset_AnchorComesBack(Exit exit)
        {
            var route = BuildLoopRoute();
            route.LastObservedLoopCycleIndex = 3;
            route.Stops[0].LastFiredCycleIndex = 3;
            RouteStore.AddRoute(route);
            Route saved = SnapshotAsSaved(route);
            route.CadenceMultiplier = 2;                       // retimed in the abandoned future
            route.DispatchInterval = 600.0;
            route.LoopAnchorUT = 2600.0;
            route.LastObservedLoopCycleIndex = 7;

            RunExit(exit, 2400.0, new[] { saved }, 2400.0);
            Route live = Committed(route.Id);

            Assert.Equal(-1, live.LastObservedLoopCycleIndex);
            Assert.Equal(-1, live.Stops[0].LastFiredCycleIndex);
            Assert.Equal(1000.0, live.LoopAnchorUT);
            Assert.Contains(logLines, l => l.Contains("clockChanged=1"));
        }

        // catches: the restore reaching a route the save never held. A route created after the
        // cutoff goes dormant exactly as before, and a kept route the save does not carry keeps
        // the reset.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void RouteCreatedAfterTheSave_GoesDormant_RouteMissingFromTheSave_KeepsTheReset(Exit exit)
        {
            var kept = BuildLoopRoute("kept");
            kept.LastObservedLoopCycleIndex = 2;
            var unsaved = BuildLoopRoute("unsaved");          // created before, absent from the copy
            unsaved.LastObservedLoopCycleIndex = 5;
            var late = BuildLoopRoute("late", createdUT: 1300.0);
            late.LastObservedLoopCycleIndex = 6;
            RouteStore.AddRoute(kept);
            RouteStore.AddRoute(unsaved);
            RouteStore.AddRoute(late);
            Route savedKept = SnapshotAsSaved(kept);

            RunExit(exit, 1200.0, new[] { savedKept }, 1200.0);

            Assert.Equal(2, Committed("kept").LastObservedLoopCycleIndex);
            Assert.Equal(-1, Committed("unsaved").LastObservedLoopCycleIndex);
            Assert.DoesNotContain(RouteStore.CommittedRoutes, r => r.Id == "late");
            Assert.Contains(RouteStore.DormantRoutes, r => r.Id == "late");
            Assert.Contains(logLines, l => l.Contains("missingFromSave=1"));
        }

        // catches: an exit with no save copy in hand (a rewind save without a Parsek scenario,
        // the route-blind bundle overload) restoring from nothing. The reset stands and the
        // exit says why.
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void NoSavedRouteCopy_TheResetStands(Exit exit)
        {
            var route = BuildLoopRoute();
            route.LastObservedLoopCycleIndex = 4;
            RouteStore.AddRoute(route);

            RunExit(exit, 1200.0, null, double.NaN);

            Assert.Equal(-1, Committed(route.Id).LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("loop position not restored") && l.Contains("no-save-copy"));
        }

        // ------------------------------------------------------------------
        // Owed recovery credit
        // ------------------------------------------------------------------

        // catches: the credit the cutoff still owes lost to a flush in the abandoned future, or
        // paid by the exit itself (the exits run inside OnLoad: no ledger write).
        [Theory]
        [InlineData(Exit.GoBack)]
        [InlineData(Exit.ReFlyStart)]
        public void OwedCreditFlushedAfterTheSave_ComesBackUnpaid(Exit exit)
        {
            var route = BuildLoopRoute();
            route.LastObservedLoopCycleIndex = 0;
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1150.0;
            RouteStore.AddRoute(route);
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = -1, Sequence = 0,
            });
            Route savedAt1200 = SnapshotAsSaved(route);
            // The abandoned future: cycle-0's credit paid at the cycle-1 crossing.
            route.PendingRecoveryCreditCycleId = null;
            route.PendingRecoveryCreditDispatchUT = -1.0;
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteRecoveryCredited, UT = 1450.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = -1, Sequence = 0,
            });

            RunExit(exit, 1200.0, new[] { savedAt1200 }, 1200.0);
            Route live = Committed(route.Id);

            Assert.Equal("cycle-0", live.PendingRecoveryCreditCycleId);
            Assert.Equal(1150.0, live.PendingRecoveryCreditDispatchUT);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteRecoveryCredited);
            Assert.Empty(liveCredits);
        }

        // catches: the Re-Fly start's real entry dropping the node on the floor. Drives
        // RewindInvoker.ConsumePostLoad with the RP quicksave's ParsekScenario node (written by
        // the same RouteStore.SaveRoutesTo OnSave uses) and the bundle captured before the load;
        // the strip half is deferred and never run.
        [Fact]
        public void ConsumePostLoad_ReadsTheRpQuicksaveRoutes_FirstTickDoesNotFireTwice()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            var rpScenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(rpScenarioNode);               // the RP quicksave at 1200
            RouteOrchestrator.Tick(1450.0, env);

            var slot = new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "recOrigin", Controllable = true };
            var rp = new RewindPoint
            {
                RewindPointId = "rp_routes",
                BranchPointId = "bp_routes",
                ChildSlots = new List<ChildSlot> { slot },
                UT = 1200.0,
            };
            try
            {
                RewindInvokeContext.Pending = true;
                RewindInvokeContext.SessionId = "sess_routes";
                RewindInvokeContext.RewindPoint = rp;
                RewindInvokeContext.Selected = slot;
                RewindInvokeContext.CapturedBundle = ReconciliationBundle.Capture();
                RewindInvokeContext.HasCapturedBundle = true;
                RewindInvokeContext.TempQuicksavePath = null;
                KerbalsModule.LoadedSaveUTProviderForTesting = () => 1200.0;
                RewindInvoker.FlightReadyProbeOverrideForTesting = () => false;
                RewindInvoker.DeferUntilFlightReadyOverrideForTesting = (a, path) => { };

                RewindInvoker.ConsumePostLoad(rpScenarioNode);
            }
            finally
            {
                KerbalsModule.LoadedSaveUTProviderForTesting = null;
                RewindInvoker.FlightReadyProbeOverrideForTesting = null;
                RewindInvoker.DeferUntilFlightReadyOverrideForTesting = null;
                RewindInvokeContext.Clear();
            }

            Route live = Committed(route.Id);
            Assert.Equal(0, live.LastObservedLoopCycleIndex);
            RouteOrchestrator.Tick(1201.0, env);
            Assert.Equal(1150.0, Assert.Single(DispatchRows()).UT);
            Assert.Contains(logLines, l => l.Contains("[ReconciliationBundle]")
                && l.Contains("Restore: loop position restored from the loaded save")
                && l.Contains("loopPositionsRestored=1"));
        }

        [Theory]
        [InlineData(0, true, 1200.0, 1200.0, "no-kept-routes")]
        [InlineData(1, false, 1200.0, 1200.0, "no-save-copy")]
        [InlineData(1, true, double.NaN, 1200.0, "no-usable-cutoff")]
        [InlineData(1, true, 1200.0, double.NaN, "no-loaded-save-clock")]
        [InlineData(1, true, 1200.0, 1215.0, "save-newer-than-cutoff")]
        [InlineData(1, true, 1200.0, 1200.0, null)]
        [InlineData(1, true, 1200.0, 1185.0, null)]
        public void DescribeRestoreSkip_NamesWhyNothingIsRestored(
            int keptCount, bool haveCopy, double cutoffUT, double loadedSaveUT, string expected)
        {
            IReadOnlyList<Route> copy = haveCopy ? new List<Route>() : null;
            Assert.Equal(expected,
                RouteLoadReconcile.DescribeRestoreSkip(keptCount, copy, cutoffUT, loadedSaveUT));
        }

        // ------------------------------------------------------------------
        // The go-back capture: the rewind save's own ParsekScenario node
        // ------------------------------------------------------------------

        private static ProtoScenarioModule Proto(string moduleName, IEnumerable<Route> routes)
        {
            var node = new ConfigNode("SCENARIO");
            node.AddValue("name", moduleName);
            node.AddValue("scene", "5, 7, 6, 8");
            if (routes != null)
            {
                ConfigNode routesNode = node.AddNode("ROUTES");
                foreach (Route r in routes)
                    r.SerializeInto(routesNode.AddNode("ROUTE"));
            }
            return new ProtoScenarioModule(node);
        }

        // catches: the go-back exit reading the wrong save. Its OnLoad node is persistent.sfs
        // (the Space Center reloads it), so the rewind save's routes must be read from the
        // parsed save's own ParsekScenario proto before the scene load and carried across.
        [Fact]
        public void CaptureRewindSaveRoutes_ReadsTheParsedSavesParsekScenarioRoutes()
        {
            var route = BuildLoopRoute();
            route.LastObservedLoopCycleIndex = 3;
            var other = BuildLoopRoute("not-parsek");
            var scenarios = new List<ProtoScenarioModule>
            {
                Proto("ResearchAndDevelopment", new[] { other }),
                Proto("ParsekScenario", new[] { route }),
            };

            RouteLoadReconcile.CaptureRewindSaveRoutes(scenarios, 1185.0, "Rewind");

            Route carried = Assert.Single(RewindContext.RewindSaveRoutes);
            Assert.Equal(route.Id, carried.Id);
            Assert.Equal(3, carried.LastObservedLoopCycleIndex);
            Assert.NotSame(route, carried);
            Assert.Equal(1185.0, RewindContext.RewindSaveClockUT);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("rewind save's own route copy") && l.Contains("routes=1"));

            // A save holding no routes is a known empty copy; no Parsek scenario is unknown.
            Assert.Empty(RouteStore.ReadSavedCommittedRoutesFromScenarios(
                new List<ProtoScenarioModule> { Proto("ParsekScenario", null) }));
            Assert.Null(RouteStore.ReadSavedCommittedRoutesFromScenarios(
                new List<ProtoScenarioModule> { Proto("ResearchAndDevelopment", null) }));
            Assert.Null(RouteStore.ReadSavedCommittedRoutesFromScenarios(null));

            RewindContext.EndRewind();
            Assert.Null(RewindContext.RewindSaveRoutes);
            Assert.True(double.IsNaN(RewindContext.RewindSaveClockUT));
        }

        // ------------------------------------------------------------------
        // Wiring (OnLoad / the scene load are not xUnit-drivable)
        // ------------------------------------------------------------------

        private static string ReadSource(string fileName)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", fileName);
            Assert.True(File.Exists(path), $"{fileName} not found at {path}");
            // Normalize line endings and drop // comments so a comment never satisfies a gate.
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf("//", StringComparison.Ordinal);
                if (c >= 0)
                    lines[i] = lines[i].Substring(0, c);
            }
            return string.Join("\n", lines);
        }

        private static string Between(string source, string start, string end)
        {
            int s = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(s >= 0, "marker missing: " + start);
            int e = source.IndexOf(end, s + start.Length, StringComparison.Ordinal);
            Assert.True(e > s, "end marker missing after " + start + ": " + end);
            return source.Substring(s, e - s);
        }

        [Fact]
        public void HandleRewindOnLoad_RestoresTheLoopPositionAfterTheReconcileBeforeTheCareerRestore()
        {
            string body = Between(ReadSource("ParsekScenario.cs"),
                "HandleRewindOnLoad:entry", "HandleRewindOnLoad:exit");

            int reconcileIdx = body.IndexOf(
                "Logistics.RouteRewindClassifier.ReconcileStoreAtRewind(", StringComparison.Ordinal);
            int restoreIdx = body.IndexOf(
                "Logistics.RouteLoadReconcile.RestoreLoopPositionAtRewindExit(", StringComparison.Ordinal);
            int pruneIdx = body.IndexOf("GameStateStore.PruneBaselinesAfterUT(", StringComparison.Ordinal);
            int endIdx = body.IndexOf("RewindContext.EndRewind()", StringComparison.Ordinal);

            Assert.True(reconcileIdx >= 0, "the shared reconcile is missing from HandleRewindOnLoad");
            Assert.True(restoreIdx > reconcileIdx,
                "HandleRewindOnLoad must restore the loop position AFTER the shared reconcile (which resets it)");
            Assert.True(pruneIdx > restoreIdx,
                "the loop-position restore must run BEFORE the career-state cutoff walk");
            Assert.True(endIdx > restoreIdx, "the carried rewind-save routes are cleared by EndRewind after use");
            string call = body.Substring(restoreIdx, pruneIdx - restoreIdx);
            Assert.Contains("RewindContext.RewindSaveRoutes", call);
            Assert.Contains("RewindContext.RewindSaveClockUT", call);
        }

        [Fact]
        public void ExecuteRewindSaveLoad_CapturesTheParsedSavesRoutesBeforeTheSceneLoad()
        {
            string body = Between(ReadSource("RecordingStore.cs"),
                "private static bool ExecuteRewindSaveLoad(", "internal static bool InitiateRewindToCareerStart(");

            int loadIdx = body.IndexOf("GamePersistence.LoadGame(", StringComparison.Ordinal);
            int captureIdx = body.IndexOf(
                "Logistics.RouteLoadReconcile.CaptureRewindSaveRoutes(game.scenarios", StringComparison.Ordinal);
            int sceneIdx = body.IndexOf("HighLogic.LoadScene(GameScenes.SPACECENTER)", StringComparison.Ordinal);

            Assert.True(loadIdx >= 0, "ExecuteRewindSaveLoad no longer parses the rewind save");
            Assert.True(captureIdx > loadIdx,
                "the rewind save's routes must be read from the parsed game after LoadGame");
            Assert.True(sceneIdx > captureIdx,
                "and before the Space Center load replaces the game with persistent.sfs");
        }

        [Fact]
        public void ReFlyStart_PassesTheLoadedNodesRoutesToTheBundleRestore()
        {
            string consume = Between(ReadSource("RewindInvoker.cs"),
                "internal static void ConsumePostLoad(", "private static void RunStripActivateMarker(");
            Assert.Contains("RouteStore.ReadSavedCommittedRoutes(loadedScenarioNode)", consume);
            Assert.Contains("ReconciliationBundle.Restore(bundle, retireCutoffUT, loadedSaveRoutes, loadedSaveUT)", consume);

            string scenario = ReadSource("ParsekScenario.cs");
            string dispatch = Between(scenario,
                "private static void DispatchRewindPostLoadIfPending(", "private void HandleRewindOnLoad(");
            Assert.Contains("RewindInvoker.ConsumePostLoad(loadedScenarioNode)", dispatch);
            int callSites = 0;
            for (int i = scenario.IndexOf("DispatchRewindPostLoadIfPending(node);", StringComparison.Ordinal);
                 i >= 0;
                 i = scenario.IndexOf("DispatchRewindPostLoadIfPending(node);", i + 1, StringComparison.Ordinal))
            {
                callSites++;
            }
            Assert.Equal(2, callSites);
        }
    }
}
