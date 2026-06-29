// UNVERIFIED: not compiled/tested in this environment
//
// Phase-3 end-to-end proof for the logistics <-> time-rewind determinism fix (Rec-1).
// Plan:   docs/dev/plans/fix-logistics-rewind-determinism.md (Phase 3 + section 6).
// Report: docs/dev/research/logistics-time-rewind-compat-report.md section 4.
//
// THE FIX UNDER PROOF (already implemented; these tests do NOT change it):
//   At a successful rewind restore, ReconciliationBundle.Restore(bundle, cutoffUT)
//   drops free-standing route ledger rows with UT > cutoffUT via the pure
//   Parsek.Logistics.RouteLedgerRetire. So after a rewind the colliding rows are
//   gone and the live re-fly re-emits the cycle (re-charging funds + re-applying
//   cargo) instead of being dedup-suppressed.
//
// HEADLESS LIMITATION (why the assertions are shaped the way they are):
//   This is a pure-xUnit suite with NO live KSP. The live physical cargo writers
//   (LiveDeliveryWriters / LiveOriginDebitWriters / LiveInventoryPickupWriter)
//   early-return on null KSP singletons, and ComputeDispatchFundsCostForRoute /
//   SumRecoveredCredits early-return 0.0 headlessly (they need committed recordings
//   with a VesselSnapshot in the ERS plus KSP's PartLoader / PartResourceLibrary).
//   Therefore this suite CANNOT assert the destination TANK is re-filled and CANNOT
//   reproduce a non-zero KSC funds cost through the LIVE re-fire path. Instead it
//   proves the two HEADLESS-observable halves of the economic contract:
//     (a) the dispatch FIRES (is NOT dedup-suppressed) and RE-EMITS the rows after
//         the retire empties the colliding cycle from the ledger, and
//     (b) the FundsModule charge is correct ONCE (asserted by feeding the
//         post-restore ledger through a fresh FundsModule, the canonical headless
//         "recalc" used by FundsModuleTests).
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
//   - FundsModuleTests.cs:1020-1031, 1078-1084 (MakeRouteDebit + ProcessAction/
//                                            GetRunningBalance recalc-charge pattern)
//   - FundsModuleTests.cs:1006-1018, 1036-1043 (MakeRouteCredit + credit-as-earning charge)
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
    /// RE-EMITS each cycle once (no dedup suppression) and the FundsModule charge is
    /// correct exactly once. Mirror-image of
    /// <see cref="RouteLoopDeliveryFireTests.ReplayedCycleId_EmitsNothing_NoDoubleCharge"/>:
    /// the dedup still suppresses a *present* duplicate (correct within one timeline),
    /// but after the retire empties the colliding cycle the SAME cycleId + UT FIRES.
    ///
    /// <para>[Collection("Sequential")] + full static reset (Ledger / RouteStore /
    /// RecordingStore / RouteOrchestrator seams) per the shared-static rule; the
    /// economic-charge half is asserted through a fresh <see cref="FundsModule"/>
    /// fed the post-restore ledger (the headless recalc).</para>
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
            RouteOrchestrator.RecoveryCreditFunderForTesting = null;

            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = null;
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
        // the three-row FIRE is genuine. fundsCostIfCareerKsc stamps the DELIVERED row
        // (the dispatch debit's KSC cost is 0 headlessly - see ComputeDispatchFunds...).
        private void InstallFakeDeliveryApplier(double fundsCostIfCareerKsc = 0.0)
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                bool isCareerKsc = env.IsCareer && route.IsKscOrigin;
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                    RouteKscFundsCost = isCareerKsc ? (float)fundsCostIfCareerKsc : 0f,
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
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
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

        private static GameAction MakeDebited(double ut, string routeId, string cycleId, float kscCost, int stopIndex = -1) => new GameAction
        {
            Type = GameActionType.RouteCargoDebited,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = stopIndex,
            Sequence = 1,
            RouteKscFundsCost = kscCost,
        };

        private static GameAction MakeDelivered(double ut, string routeId, string cycleId, float kscCost = 0f, int stopIndex = 0) => new GameAction
        {
            Type = GameActionType.RouteCargoDelivered,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = stopIndex,
            Sequence = 0,
            RouteKscFundsCost = kscCost,
        };

        private static GameAction MakeRecoveryCredited(double ut, string routeId, string cycleId, float amount) => new GameAction
        {
            Type = GameActionType.RouteRecoveryCredited,
            UT = ut,
            RouteId = routeId,
            RouteCycleId = cycleId,
            RouteStopIndex = -1,
            Sequence = 0,
            RouteKscFundsCost = amount, // positive magnitude; type carries the sign
            Effective = true,
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

        // Convenience: seed-funds + sum of route fund deltas the ledger would charge.
        private static int CountActions(GameActionType type, string routeId = null, string cycleId = null) =>
            Ledger.Actions.Count(a => a.Type == type
                && (routeId == null || a.RouteId == routeId)
                && (cycleId == null || a.RouteCycleId == cycleId));

        // ==================================================================
        // (1) Funds-once + cargo-row-re-emitted (KSC origin)
        // ==================================================================

        // Seed cycle-0 fired-before-rewind rows (Dispatched + Debited[KscFundsCost=5000]
        // + Delivered) at UT 2000. Capture() a bundle (full ledger; Capture unchanged).
        // Restore(bundle, 1500) -> the three future route rows are DROPPED. Then drive
        // the cycle-0 crossing live: it FIRES (not dedup-suppressed) and RE-EMITS the
        // rows. A recalc over the resulting ledger charges 5000 EXACTLY once.
        [Fact]
        public void Funds_KscCycleReFires_AfterRetire_ChargesFiveThousandOnce()
        {
            // Live route carries the reverted-via-.sfs counters: it believes it has done
            // nothing since RP (cycle-0 not yet observed).
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);

            // --- pre-rewind ledger: seed funds + cycle-0 fired at UT 2000 (> cutoff 1500).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, route.Id, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, route.Id, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(2000.0, route.Id, "cycle-0"));

            // Pre-retire: the surviving ledger would charge the 5000 (the bug).
            Assert.Equal(25000.0, RecalcRunningBalance());

            // Capture holds the FULL pre-rewind ledger (Capture is unchanged).
            var bundle = ReconciliationBundle.Capture();
            Assert.Equal(3, bundle.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));

            // --- the rewind restore with cutoff 1500 DROPS the three future route rows.
            ReconciliationBundle.Restore(bundle, 1500.0);

            Assert.Equal(0, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(0, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            // The non-route seed survives -> a recalc now shows NO charge (no phantom).
            Assert.Equal(30000.0, RecalcRunningBalance());
            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("retired 3") && l.Contains("Rec-1"));

            // --- live re-fly: the same cycle-0 crossing now FIRES (empty slate).
            InstallUnitResolver(BuildUnit());           // span [1000,1300], dock 1150
            InstallFakeDeliveryApplier(fundsCostIfCareerKsc: 5000.0);
            RouteOrchestrator.Tick(1150.0, new EligibleEnv { IsCareer = true });

            // Re-emitted exactly once (NOT dedup-suppressed: this is the inverse of
            // ReplayedCycleId_EmitsNothing).
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(1, route.CompletedCycles);

            // The fake delivery applier stamped the DELIVERED row with the 5000 cost,
            // proving the cost flows on the re-fired cycle's row. NOTE: FundsModule
            // charges RouteCargoDebited (not RouteCargoDelivered), and the LIVE re-fired
            // debit's KSC cost is 0 headlessly (ComputeDispatchFundsCostForRoute returns
            // 0 without an ERS VesselSnapshot + KSP PartResourceLibrary). So the
            // 5000-charged-EXACTLY-once FundsModule proof is asserted on a CONTROLLED
            // ledger in Funds_PostRetireLedger_ChargesDebitedRowOnce below; the
            // live-writer + real-cost path is the in-game test (plan Phase 3 step 12).
            var delivered = Ledger.Actions.Single(a =>
                a.Type == GameActionType.RouteCargoDelivered && a.RouteId == route.Id);
            Assert.Equal(5000f, delivered.RouteKscFundsCost);
        }

        // Controlled economic proof for case 1: after the retire empties cycle-0 and the
        // re-fly re-emits a RouteCargoDebited carrying the real 5000 cost, a recalc over
        // the resulting ledger charges 5000 EXACTLY once (not twice from a surviving
        // duplicate, not zero from a missing row). Models the re-emitted debit explicitly
        // because the live re-fire's cost is 0 headlessly (see the TODO above).
        [Fact]
        public void Funds_PostRetireLedger_ChargesDebitedRowOnce()
        {
            string routeId = "route-loop";

            // Pre-rewind: seed + cycle-0 fired at UT 2000 (debit cost 5000).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, routeId, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(2000.0, routeId, "cycle-0"));

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0); // drop the 3 future rows
            Assert.Equal(30000.0, RecalcRunningBalance()); // no phantom charge after drop

            // Re-fly re-emits cycle-0 with the REAL 5000 cost (modeled explicitly, since
            // the headless live re-fire's debit cost is 0).
            Ledger.AddAction(MakeDispatched(1150.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(1150.0, routeId, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(1150.0, routeId, "cycle-0"));

            // Charged exactly ONCE (30000 - 5000), and there is exactly one debit row.
            Assert.Equal(25000.0, RecalcRunningBalance());
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDebited, routeId, "cycle-0"));
        }

        // ==================================================================
        // (2) Non-KSC pure-physical: debit carries 0 funds; row re-emits, no charge,
        //     CompletedCycles advances exactly once.
        // ==================================================================

        [Fact]
        public void NonKsc_CycleReFires_NoFundsCharged_CompletedCyclesAdvancesOnce()
        {
            var route = BuildLoopRoute(isKscOrigin: false, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);

            // Pre-rewind: cycle-0 fired at UT 2000 with a 0-funds debit (non-KSC: the
            // physical manifest IS the cost, funds row is 0).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, route.Id, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, route.Id, "cycle-0", kscCost: 0f));
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
            // The re-emitted debit carries 0 funds -> the recalc still shows no charge.
            var debited = Ledger.Actions.Single(a =>
                a.Type == GameActionType.RouteCargoDebited && a.RouteId == route.Id);
            Assert.Equal(0f, debited.RouteKscFundsCost);
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
            Ledger.AddAction(MakeDebited(2000.0, routeId, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(2000.0, routeId, "cycle-0"));

            // --- rewind #1 to cutoff 1500: drops cycle-0 (UT 2000 > 1500).
            var bundle1 = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle1, 1500.0);
            Assert.Equal(0, Ledger.Actions.Count(RouteLedgerRetire.IsFreeStandingRouteAction));

            // --- re-fly emits cycle-0 again, this time landing at UT 1600 (between the
            // two cutoffs). This row survives rewind #2 (cutoff 1800 > ... no: 1600 < 1800
            // so it is kept by rewind #2; the post-1800 rows are what get dropped).
            Ledger.AddAction(MakeDispatched(1600.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(1600.0, routeId, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(1600.0, routeId, "cycle-0"));
            // ... plus cycle-1 at UT 1900 (> 1800: this one is the abandoned future of
            // rewind #2).
            Ledger.AddAction(MakeDispatched(1900.0, routeId, "cycle-1"));
            Ledger.AddAction(MakeDebited(1900.0, routeId, "cycle-1", kscCost: 5000f));
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
        // (4) Recovery-credit straddle: a future RouteRecoveryCredited is dropped at
        //     rewind so it cannot be a phantom credit; one re-flushed credit charges once.
        // ==================================================================

        // APPROXIMATED (see TODO): the LIVE re-flush (EmitPendingRecoveryCredit) reads
        // SumRecoveredCredits over the ERS/ELS, which is 0 headlessly (no committed
        // recordings with recovery rows). So the re-flushed credit is modeled as an
        // explicit RouteRecoveryCredited row, and the "arm reverted via the .sfs ROUTES
        // node" is modeled by resetting PendingRecoveryCreditCycleId to its at-RP value.
        [Fact]
        public void RecoveryCreditStraddle_FutureCreditDropped_SingleReflushChargesOnce()
        {
            var route = BuildLoopRoute(isKscOrigin: true, lastObservedLoopCycleIndex: -1);
            // The route armed a pending credit pre-rewind, then it flushed at UT 2000.
            route.PendingRecoveryCreditCycleId = "cycle-0";
            RouteStore.AddRoute(route);

            // Pre-rewind ledger: seed + an already-emitted recovery credit at UT 2000.
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeRecoveryCredited(2000.0, route.Id, "cycle-0", amount: 1200f));
            // Pre-retire the credit would inflate funds (the phantom).
            Assert.Equal(31200.0, RecalcRunningBalance());

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0); // drop the future credit row

            Assert.Equal(0, CountActions(GameActionType.RouteRecoveryCredited, route.Id, "cycle-0"));
            Assert.Equal(30000.0, RecalcRunningBalance()); // phantom credit gone

            // Model the .sfs ROUTES revert of the arm: PendingRecoveryCreditCycleId reads
            // its at-RP value (still owed for cycle-0 because the dispatch reverted too).
            route.PendingRecoveryCreditCycleId = "cycle-0";

            // Re-fly re-flushes the credit ONCE (modeled explicitly; the live
            // EmitPendingRecoveryCredit reads SumRecoveredCredits == 0 headlessly).
            Ledger.AddAction(MakeRecoveryCredited(1150.0, route.Id, "cycle-0", amount: 1200f));

            Assert.Equal(1, CountActions(GameActionType.RouteRecoveryCredited, route.Id, "cycle-0"));
            Assert.Equal(31200.0, RecalcRunningBalance()); // credited exactly once

            // TODO(build-env): drive the credit re-flush through RouteOrchestrator.Tick ->
            // EmitPendingRecoveryCredit with a committed source recording carrying a
            // RouteRecoveryCredited-able recovery row in the ERS, so SumRecoveredCredits
            // returns 1200 and the live path emits the row itself. Headless that sum is 0,
            // so the re-flushed row is modeled here; the live cadence is the in-game test.
        }

        // ==================================================================
        // (5) Phantom-charge avoidance: a cycle dropped at rewind but NOT re-flown
        //     charges NOTHING (the abandoned row is gone; no phantom).
        // ==================================================================

        [Fact]
        public void PhantomAvoidance_DroppedCycleNotReflown_ChargesNothing()
        {
            string routeId = "route-loop";

            // Pre-rewind: seed + cycle-0 fired at UT 2000 (KSC cost 5000).
            Ledger.AddAction(MakeSeed(0.0, 30000f));
            Ledger.AddAction(MakeDispatched(2000.0, routeId, "cycle-0"));
            Ledger.AddAction(MakeDebited(2000.0, routeId, "cycle-0", kscCost: 5000f));
            Ledger.AddAction(MakeDelivered(2000.0, routeId, "cycle-0"));
            Assert.Equal(25000.0, RecalcRunningBalance()); // the buggy phantom charge

            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, 1500.0); // drop cycle-0

            // The player PAUSES the route so the crossing never re-fires (no re-emit).
            // A recalc over the surviving ledger charges NOTHING for cycle-0.
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

            // The SAME cycleId + SAME crossing UT now FIRES (full three-row cycle).
            RouteOrchestrator.Tick(1150.0, new EligibleEnv());
            Assert.Equal(1, CountActions(GameActionType.RouteDispatched, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDebited, route.Id, "cycle-0"));
            Assert.Equal(1, CountActions(GameActionType.RouteCargoDelivered, route.Id, "cycle-0"));
            Assert.Equal(1, route.CompletedCycles);
        }
    }
}
