// Phase-3 end-to-end proof for the logistics <-> time-rewind determinism fix (Rec-1).
// Plan:   docs/dev/plans/fix-logistics-rewind-determinism.md (Phase 3 + section 6).
// Report: docs/dev/research/logistics-time-rewind-compat-report.md section 4.
//
// THE FIX UNDER PROOF (already implemented; these tests do NOT change it):
//   At a successful rewind restore, ReconciliationBundle.Restore(bundle, cutoffUT)
//   drops free-standing route ledger rows with UT > cutoffUT via the pure
//   Parsek.Logistics.RouteLedgerRetire. So after a rewind the colliding rows are
//   gone and the live re-fly re-emits the cycle (re-applying cargo) instead of
//   being dedup-suppressed. Supply routes carry no money (todo
//   LOGISTICS-WITHOUT-MONEY), so no step here may move funds.
//
// HEADLESS LIMITATION (why the assertions are shaped the way they are):
//   This is a pure-xUnit suite with NO live KSP. The live physical cargo writers
//   (LiveDeliveryWriters / LiveOriginDebitWriters / LiveInventoryPickupWriter)
//   early-return on null KSP singletons. Therefore this suite CANNOT assert the
//   destination TANK is re-filled. Instead it proves the headless-observable halves:
//     (a) the dispatch FIRES (is NOT dedup-suppressed) and RE-EMITS the rows after
//         the retire empties the colliding cycle from the ledger, and
//     (b) a fresh FundsModule fed the ledger shows the route rows move no funds.
//   The actual physical tank re-delivery is the in-game test's job (plan Phase 3
//   step 12, the LOAD-BEARING in-game proof).
//
// Seams reused (file:line at authoring time):
//   - RouteLoopDeliveryFireTests.cs:28-50  (ctor/Dispose static-reset discipline)
//   - RouteLoopDeliveryFireTests.cs:59-99   (BuildUnit / InstallUnitResolver /
//                                            InstallFakeDeliveryApplier delivery seam)
//   - RouteLoopDeliveryFireTests.cs:101-156 (BuildLoopRoute + EligibleEnv fake env)
//   - RouteLoopDeliveryFireTests.cs:207-245 (Crossing_EmitsAllThreeRows: the FIRE shape)
//   - RouteLoopDeliveryFireTests.cs:751-786 (ReplayedCycleId_EmitsNothing: the dedup this
//                                            test is the INVERSE of)
//   - ReconciliationBundle.cs:79-166 (Capture full ledger), :174-296 (Restore overloads)
//   - RouteLedgerRetire.cs (the pure retire the Restore cutoff routes through)

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Rec-1 end-to-end proof: a rewind drops the abandoned-future free-standing route
    /// rows (via <see cref="ReconciliationBundle.Restore(ReconciliationBundle, double)"/>
    /// -&gt; <see cref="RouteLedgerRetire.RetireFutureRouteActions"/>), so the live re-fly
    /// RE-EMITS each cycle once (no dedup suppression) and the route rows move no
    /// funds. Mirror-image of
    /// <see cref="RouteLoopDeliveryFireTests.ReplayedCycleId_EmitsNothing_NoDoubleCharge"/>:
    /// the dedup still suppresses a *present* duplicate (correct within one timeline),
    /// but after the retire empties the colliding cycle the SAME cycleId + UT FIRES.
    ///
    /// <para>[Collection("Sequential")] + full static reset (Ledger / RouteStore /
    /// RecordingStore / RouteOrchestrator seams) per the shared-static rule; the
    /// no-funds half is asserted through a fresh <see cref="FundsModule"/> fed the
    /// post-restore ledger (the headless recalc).</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteRewindRedeliveryTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteRewindRedeliveryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            // Capture/Restore guard on ParsekScenario.Instance; null it so the scenario
            // lists are not touched (we only exercise the Ledger + RecordingStore +
            // route halves of the bundle).
            ParsekScenario.ResetInstanceForTesting();

            // Every shared static Restore() touches must be reset for isolation
            // (Restore unconditionally re-installs crew / groups / milestones too).
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting(); // also re-clears the Ledger; harmless.
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();

            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;

            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers (reused verbatim from RouteLoopDeliveryFireTests)
        // ==================================================================

        // span [1000, 1300] (300s); cadence == span -> one crossing == one cycle.
        // dock UT 1150 inside the span.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        // Fake delivery half (no live Vessel): emits RouteCargoDelivered + bumps
        // CompletedCycles + clears pending. The dispatch-debit half runs for real, so
        // the FIRE is genuine.
        private void InstallFakeDeliveryApplier()
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
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

        private static Route BuildLoopRoute(
            string id = "route-loop",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = true,
            double recordedDockUT = 1150.0,
            long lastObservedLoopCycleIndex = -1,
            double dispatchInterval = 300.0)
        {
            return new Route
            {
                Id = id,
                Status = status,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = "tree-1", // makes IsLoopRoute true
                RecordedDockUT = recordedDockUT,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
                DispatchInterval = dispatchInterval,
                TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 120.0 },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double>
                        {
                            { "LiquidFuel", 100.0 },
                            { "Oxidizer", 120.0 },
                        },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        // Eligible env: all gates pass; vessel=null (no live Vessel needed because the
        // delivery half is the injected fake and the physical writers early-return).
        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource, out double shortfall) { shortfall = 0.0; lackingResource = string.Empty; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ----- ledger-row builders (field names mirror FundsModuleTests) -----

        private static GameAction MakeSeed(double ut, float initialFunds) => new GameAction
        {
            UT = ut,
            Type = GameActionType.FundsInitial,
            InitialFunds = initialFunds,
        };

        private static GameAction MakeDispatched(double ut, string routeId, string cycleId, int stopIndex = -1) => new GameAction
        {
            Type = GameActionType.RouteDispatched,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = stopIndex,
            Sequence = 0,
        };

        private static GameAction MakeDebited(double ut, string routeId, string cycleId, int stopIndex = -1) => new GameAction
        {
            Type = GameActionType.RouteCargoDebited,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = stopIndex,
            Sequence = 1,
        };

        private static GameAction MakeDelivered(double ut, string routeId, string cycleId, int stopIndex = 0) => new GameAction
        {
            Type = GameActionType.RouteCargoDelivered,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = stopIndex,
            Sequence = 0,
        };

        // The canonical headless "recalc": feed every action in Ledger order through a
        // fresh FundsModule and return the resulting running balance. Mirrors
        // FundsModuleTests' ProcessAction(...) + GetRunningBalance() pattern exactly.
        private static double RecalcRunningBalance()
        {
            var module = new FundsModule();
            foreach (var a in Ledger.Actions)
                module.ProcessAction(a);
            return module.GetRunningBalance();
        }

        private static int CountActions(GameActionType type, string routeId = null, string cycleId = null) =>
            Ledger.Actions.Count(a => a.Type == type
                && (routeId == null || a.RouteId == routeId)
                && (cycleId == null || a.RouteCycleId == cycleId));

        // ==================================================================
        // (1) Cargo rows re-emitted (Space Center origin), no funds at any step
        // ==================================================================

        // Seed cycle-0 fired-before-rewind rows (Dispatched + Delivered: a Space Center
        // origin writes no debit row) at UT 2000. Capture() a bundle (full ledger).
        // Restore(bundle, 1500) -> the two future route rows are DROPPED. Then drive
        // the cycle-0 crossing live: it FIRES (not dedup-suppressed) and RE-EMITS the
        // rows. Funds never move.
        [Fact]
        public void KscCycleReFires_AfterRetire_DispatchesFreeOnce()
        {
            // Live route carries the reverted-via-.sfs counters: it believes it has done
            // nothing since RP (cycle-0 not yet observed).
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);

            // --- pre-rewind ledger: seed funds + cycle-0 fired at UT 2000 (> cutoff 1500).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, route.Id, "cycle-0"));
            Ledger.AddAction(MakeDelivered(2000.0, route.Id, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance());

            // Capture holds the FULL pre-rewind ledger (Capture is unchanged).
            var bundle = ReconciliationBundle.Capture();
            Assert.Equal(2, bundle.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));

            // --- the rewind restore with cutoff 1500 DROPS the two future route rows.
            ReconciliationBundle.Restore(bundle, 1500.0);

            Assert.Equal(0, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance());
            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("retired 2") && l.Contains("Rec-1"));

            // --- live re-fly: the same cycle-0 crossing now FIRES (empty slate).
            InstallUnitResolver(BuildUnit());           // span [1000,1300], dock 1150
            InstallFakeDeliveryApplier();
            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            // Re-emitted exactly once (NOT dedup-suppressed: this is the inverse of
            // ReplayedCycleId_EmitsNothing), with no debit row and no funds change.
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(30000.0, RecalcRunningBalance());
        }

        // ==================================================================
        // (2) Non-KSC pure-physical: the debit row re-emits, no funds move,
        //     CompletedCycles advances exactly once.
        // ==================================================================

        [Fact]
        public void NonKsc_CycleReFires_NoFundsCharged_CompletedCyclesAdvancesOnce()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);

            // Pre-rewind: cycle-0 fired at UT 2000 (non-KSC: the physical manifest IS
            // the cost).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, route.Id, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, route.Id, "cycle-0"));
            Ledger.AddAction(MakeDelivered(2000.0, route.Id, "cycle-0"));

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0); // drop the 3 future route rows

            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance()); // funds untouched throughout

            // Live re-fly: cycle-0 crossing FIRES (non-KSC origin gate passes via env fake).
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            // Non-KSC needs the physical origin-debit seam (production ApplyOriginDebit
            // would early-return, but the row still emits). Stub a benign full debit.
            RouteOrchestrator.OriginDebitApplierForTesting = (r, ut, env) =>
                new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = new Dictionary<string, double>
                    {
                        { "LiquidFuel", 100.0 },
                        { "Oxidizer", 120.0 },
                    },
                    OriginVesselPid = 777u,
                };
            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            // Re-emitted once, funds unchanged, CompletedCycles advanced exactly once.
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(1, route.CompletedCycles);
            // The re-emitted debit moves no funds.
            Assert.Equal(30000.0, RecalcRunningBalance());
        }

        // ==================================================================
        // (3) Multi-rewind: orphan route rows never accumulate across stacked rewinds.
        // ==================================================================

        [Fact]
        public void MultiRewind_NoOrphanRouteRowAccumulation()
        {
            string routeId = "route-loop";

            // --- timeline 1: seed + cycle-0 fired at UT 2000.
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDelivered(2000.0, routeId, "cycle-0"));

            // --- rewind #1 to cutoff 1500: drops cycle-0 (UT 2000 > 1500).
            var bundle1 = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle1, 1500.0);
            Assert.Equal(0, Ledger.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));

            // --- re-fly emits cycle-0 again, this time landing at UT 1600 (between the
            // two cutoffs). This row survives rewind #2 (cutoff 1800 > ... no: 1600 < 1800
            // so it is kept by rewind #2; the post-1800 rows are what get dropped).
            Ledger.AddAction(MakeDispatched(1600.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(1600.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDelivered(1600.0, routeId, "cycle-0"));
            // ... plus cycle-1 at UT 1900 (> 1800: this one is the abandoned future of
            // rewind #2).
            Ledger.AddAction(MakeDispatched(1900.0, routeId, "cycle-1"));
            Ledger.AddAction(MakeDebited(1900.0, routeId, "cycle-1"));
            Ledger.AddAction(MakeDelivered(1900.0, routeId, "cycle-1"));

            // --- rewind #2 captures a ledger the prior rewind already cleaned, then
            // Restore(bundle2, 1800) drops only the UT-1900 cycle-1 rows; UT-1600 cycle-0
            // rows (<= 1800) are kept.
            var bundle2 = ReconciliationBundle.Capture();
            // Capture re-snapshots the LIVE (already-cleaned) ledger: 1 seed + 6 route rows.
            Assert.Equal(6, bundle2.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));
            ReconciliationBundle.Restore(bundle2, 1800.0);

            // Only the cycle-1 (UT 1900) rows dropped; cycle-0 (UT 1600) kept; no
            // accumulation of the original UT-2000 rows (they were never re-captured).
            Assert.Equal(3, Ledger.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));
            Assert.Equal(0, CountActions(GameActionType.RouteDispatched, routeId, "cycle-1"));
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, routeId, "cycle-0"));
            // Every surviving route row is at/below the latest cutoff.
            Assert.All(Ledger.Actions.Where(RouteLedgerRetire.IsFreeStandingRouteAction),
                a => Assert.True(a.UT <= 1800.0));
        }

        // ==================================================================
        // (4) A cycle dropped at rewind but NOT re-flown leaves no route row behind.
        // ==================================================================

        [Fact]
        public void DroppedCycleNotReflown_LeavesNoRouteRow()
        {
            string routeId = "route-loop";

            // Pre-rewind: seed + cycle-0 fired at UT 2000.
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDelivered(2000.0, routeId, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance()); // route rows move no funds

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0); // drop cycle-0

            // The player PAUSES the route so the crossing never re-fires (no re-emit).
            // No cycle-0 row survives, and funds never moved.
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, routeId, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance());

            // And the non-route seed is byte-identically preserved (the retire is
            // route-Type-gated: it can only ever remove route rows).
            Assert.Single(Ledger.Actions);
            Assert.Equal(GameActionType.FundsInitial, Ledger.Actions[0].Type);
        }

        // ==================================================================
        // Direct inverse of ReplayedCycleId_EmitsNothing_NoDoubleCharge (plan section 4.5):
        // with the colliding dispatch row PRESENT the dedup suppresses; once the retire
        // removes it the SAME cycleId + SAME UT FIRES. Both halves in one test so the
        // before/after is unmistakable.
        // ==================================================================

        [Fact]
        public void DedupSuppresses_WhenRowPresent_ThenFires_AfterRetireDropsIt()
        {
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            // Seed a cycle-0 RouteDispatched at UT 2000 (the surviving-rewind collision).
            Ledger.AddAction(MakeDispatched(2000.0, route.Id, "cycle-0"));

            // With the row PRESENT the crossing replay-skips (dedup) - emits nothing.
            RouteOrchestrator.Tick(1150.0, new EligibleEnv());
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            // CompletedCycles bumped by the replay backstop so the next cycleId advances.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("already in ledger") && l.Contains("replay"));

            // --- now the rewind retire removes that colliding row.
            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0);
            Assert.Equal(0, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));

            // Reset the live route's cursor to the at-RP value (reverted via .sfs ROUTES)
            // so the same crossing recomputes cycle-0 and is no longer dedup-blocked.
            route.CompletedCycles = 0;
            route.SkippedCycles = 0;
            route.LastObservedLoopCycleIndex = -1;
            logLines.Clear();

            // The SAME cycleId + SAME crossing UT now FIRES (a Space Center origin writes
            // dispatch + delivery, no debit row).
            RouteOrchestrator.Tick(1150.0, new EligibleEnv());
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(1, route.CompletedCycles);
        }
    }
}
