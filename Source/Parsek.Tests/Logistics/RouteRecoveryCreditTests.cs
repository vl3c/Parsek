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
    /// Pins the deferred per-cycle recovery CREDIT (logistics-recovery-credit
    /// plan). Drives the loop-route path through the
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// seams (<see cref="RouteOrchestrator.LoopUnitResolverForTesting"/>,
    /// <see cref="RouteOrchestrator.DeliveryApplierForTesting"/>) plus the new
    /// <see cref="RouteOrchestrator.RecoveryCreditFunderForTesting"/> fund seam
    /// (records the live credit amount without touching the Funding static). The
    /// credit AMOUNT is resolved from a committed source tree
    /// (<see cref="RecordingStore.AddCommittedTreeForTesting"/>) + recovery
    /// FundsEarning rows in the ledger, so SumRecoveredCredits feeds the emit
    /// exactly like production.
    ///
    /// Covers the section-9 matrix: T-PAIR, T-STEADY-STATE, T-BLOCK, T-MODE-GATE,
    /// T-PAUSE-FLUSH (immediate TryPause, armed pause-after-cycle, EndpointLost),
    /// T-NODOUBLE, T-CRASH-WINDOW, T-FUNDS-OUT-THEN-BACK, T-AMOUNT, T-SUP,
    /// T-SUP-NOBLOCK.
    ///
    /// SCOPE NOTE: T-FUNDS-OUT-THEN-BACK here is the DEFERRAL proof (funds go out
    /// at dispatch and come back one crossing LATER, never same-tick). It is a
    /// forward-only timeline assertion and does NOT exercise a rewind. The separate
    /// REWIND-REVERSAL proof (T-REWIND, the safety-critical rollback path: a cutoff
    /// walk un-credits the recovery by exactly the credit amount, and Option A's
    /// gross debit cutoff is symmetric) lives in
    /// <c>RewindUtCutoffTests.RouteRecoveryCredit_CutoffBeforeCredit_*</c> /
    /// <c>RouteCargoDebit_CutoffBeforeDebit_*</c>, which drive the real engine +
    /// PatchFunds path. Do NOT read the deferral test below as the rollback test.
    /// </summary>
    [Collection("Sequential")]
    public class RouteRecoveryCreditTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();
        private readonly List<double> liveCredits = new List<double>();

        private const string TreeId = "tree-credit";
        private const string RootRecId = "rec-root";
        private const string RecoveryRecId = "rec-recovery"; // post-undock recovery leg
        private const double Recovered = 7300.0;

        public RouteRecoveryCreditTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekScenario.ResetInstanceForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = amount => liveCredits.Add(amount);
            liveCredits.Clear();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.RecoveryCreditFunderForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RecordingStore.ClearCommittedTreesInternal();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Fixtures / seams
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

        // Mirrors ApplyDelivery's observable contract WITHOUT a live Vessel: emit
        // RouteCargoDelivered + bump CompletedCycles + clear pending delivery. The
        // dispatch-debit half and the recovery-credit flush run for real.
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
            string id = "route-credit",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = true,
            double recordedDockUT = 1150.0,
            long lastObservedLoopCycleIndex = -1)
        {
            return new Route
            {
                Id = id,
                Status = status,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = TreeId, // makes IsLoopRoute true + tree-id resolves
                RecordedDockUT = recordedDockUT,
                DockMemberRecordingId = RootRecId,
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
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
                    new RouteSourceRef { RecordingId = RootRecId, TreeId = TreeId, RouteProofHash = "deadbeef" },
                },
            };
        }

        // Commit a source tree whose member set includes both the root recording
        // and the (post-undock) recovery leg, so SumRecoveredCredits scopes over
        // the WHOLE tree (gotcha G1).
        private static void InstallSourceTree()
        {
            var root = new Recording { RecordingId = RootRecId, TreeId = TreeId, VesselName = "transport" };
            var recovery = new Recording { RecordingId = RecoveryRecId, TreeId = TreeId, VesselName = "transport-home" };
            var tree = new RecordingTree { Id = TreeId, RootRecordingId = RootRecId };
            tree.Recordings[RootRecId] = root;
            tree.Recordings[RecoveryRecId] = recovery;
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        // Seed a recovery FundsEarning row whose RecordingId is in the source tree.
        // Returns the action so a test can tombstone it.
        private static GameAction SeedRecoveryRow(double funds = Recovered, string recId = RecoveryRecId)
        {
            var row = new GameAction
            {
                Type = GameActionType.FundsEarning,
                FundsSource = FundsEarningSource.Recovery,
                RecordingId = recId,
                FundsAwarded = (float)funds,
                UT = 500.0,
            };
            Ledger.AddAction(row);
            return row;
        }

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

        private sealed class BlockedEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool OriginHasCargoResult { get; set; } = true;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = OriginHasCargoResult ? string.Empty : "LiquidFuel"; return OriginHasCargoResult; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        private static List<GameAction> Credits()
        {
            return Ledger.Actions.Where(a => a.Type == GameActionType.RouteRecoveryCredited).ToList();
        }

        // Minimal capturing writer bundle for ApplyDeliveryFromPlan (the armed
        // pause-after-cycle flush test). Mirrors RouteOrchestratorDeliveryTests'
        // CapturingWriters: writer drives the actual, reader reports total written.
        private sealed class CapturingDeliveryWriters
        {
            public readonly List<(string Name, double Amount)> ResourceCalls = new List<(string, double)>();
            public readonly List<double> FundsDebits = new List<double>();

            public void WriteResource(string name, double amount) => ResourceCalls.Add((name, amount));

            public double ReadActualResource(string name)
            {
                double total = 0.0;
                for (int i = 0; i < ResourceCalls.Count; i++)
                    if (ResourceCalls[i].Name == name)
                        total += ResourceCalls[i].Amount;
                return total;
            }

            public void WriteInventory(InventoryPayloadItem item, int slot) { }
            public int ReadInventoryActualCount() => 0;
            public void DebitFunds(double cost) => FundsDebits.Add(cost);
        }

        private static DeliveryPlan BuildFullFillPlan(Dictionary<string, double> manifest)
        {
            var resources = new List<ResourceDeliveryLine>();
            foreach (var kv in manifest)
                resources.Add(new ResourceDeliveryLine(kv.Key, kv.Value, kv.Value));
            return new DeliveryPlan(resources, Array.Empty<InventoryDeliveryLine>(), isPartial: false, isZero: false);
        }

        // ==================================================================
        // T-AMOUNT: amount == SumRecoveredCredits; zero-recovery clears pending
        // ==================================================================

        // catches: the credit amount drifting from SumRecoveredCredits over the
        // source tree (single recovery row).
        [Fact]
        public void EmitPendingRecoveryCredit_Amount_EqualsSumRecoveredCredits()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1000.0;
            var env = new EligibleEnv { IsCareer = true };

            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);

            Assert.True(emitted);
            var credit = Assert.Single(Credits());
            Assert.Equal((float)Recovered, credit.RouteKscFundsCost);
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Equal(1300.0, credit.UT);
            // Live stock credit applied once.
            Assert.Single(liveCredits);
            Assert.Equal(Recovered, liveCredits[0]);
            // Pending marker cleared.
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Equal(-1.0, route.PendingRecoveryCreditDispatchUT);
        }

        // catches: multiple recovery rows not summed (boosters + transport).
        [Fact]
        public void EmitPendingRecoveryCredit_Amount_SumsMultipleRecoveries()
        {
            InstallSourceTree();
            SeedRecoveryRow(1200.0, RootRecId);
            SeedRecoveryRow(6100.0, RecoveryRecId);
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-0";
            var env = new EligibleEnv { IsCareer = true };

            RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);

            var credit = Assert.Single(Credits());
            Assert.Equal(7300f, credit.RouteKscFundsCost); // 1200 + 6100
        }

        // catches: a zero-recovery tree retrying the dispatched cycle forever
        // (the pending marker must be cleared, no credit row).
        [Fact]
        public void EmitPendingRecoveryCredit_ZeroRecovery_NoRow_ClearsPending()
        {
            InstallSourceTree(); // tree exists but NO recovery rows seeded
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-0";
            var env = new EligibleEnv { IsCareer = true };

            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);

            Assert.False(emitted);
            Assert.Empty(Credits());
            Assert.Empty(liveCredits);
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("credit-skip zero-recovery"));
        }

        // ==================================================================
        // T-PAIR: prior-cycle pairing across two consecutive crossings
        // ==================================================================

        // catches: the credit firing in the dispatching cycle's OWN tick
        // (net-at-dispatch collapse). The credit must land at the NEXT crossing,
        // keyed on the PRIOR cycle.
        [Fact]
        public void TwoCrossings_CreditFiresNextCrossing_KeyedOnPriorCycle()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv { IsCareer = true };

            // FIRST crossing dispatches cycle-0; NO credit yet; pending armed.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Empty(Credits());
            Assert.Equal("cycle-0", route.PendingRecoveryCreditCycleId);
            double firstCrossingUT = 1150.0;

            // SECOND crossing (next cadence period) dispatches cycle-1 AND flushes
            // cycle-0's credit at the second crossing's UT.
            RouteOrchestrator.Tick(1450.0, env);

            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);   // PRIOR cycle, not cycle-1
            Assert.True(credit.UT > firstCrossingUT, "credit UT must be a strictly later tick (real deferral)");
            Assert.Equal((float)Recovered, credit.RouteKscFundsCost);
            // cycle-1 now owes its own credit.
            Assert.Equal("cycle-1", route.PendingRecoveryCreditCycleId);
        }

        // ==================================================================
        // T-FUNDS-OUT-THEN-BACK: the DEFERRAL proof (forward-only funds timeline).
        // This is NOT the rewind-reversal proof. It asserts the credit lands one
        // crossing LATER than the dispatch it pays back (timing honesty), so it is
        // satisfiable only under the prior-cycle pairing. The "funds go back out on
        // a rewind" property is proven separately and independently by T-REWIND in
        // RewindUtCutoffTests (cutoff walk + PatchFunds). Keep the two distinct:
        // deferral here, reversibility there.
        // ==================================================================

        // catches: the credit collapsing to net-at-dispatch. After crossing 1 the
        // live credit list is EMPTY (funds down by gross only); only after
        // crossing 2 does the credit land. (Deferral, not reversal: see T-REWIND.)
        [Fact]
        public void FundsTimeline_DownByGrossOnly_AfterFirstCrossing()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier(fundsCostIfCareerKsc: 12500.0);
            var env = new EligibleEnv { IsCareer = true };

            // After crossing 1: gross debited (a RouteCargoDebited row exists for
            // cycle-0), but NO live recovery credit yet (out, not yet back). This is
            // the timing honesty: funds are DOWN by gross only after crossing 1.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Empty(liveCredits);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.RouteCargoDebited && a.RouteCycleId == "cycle-0");

            // After crossing 2: cycle-0's credit lands (funds back).
            RouteOrchestrator.Tick(1450.0, env);
            Assert.Single(liveCredits);
            Assert.Equal(Recovered, liveCredits[0]);
        }

        // ==================================================================
        // T-STEADY-STATE: N crossings, one credit each after the first
        // ==================================================================

        // catches: the deferral tail drifting (each crossing after the first emits
        // exactly one credit, all the same constant amount; one fewer credit than
        // debit at every step).
        [Fact]
        public void ThreeCrossings_OneCreditEach_AfterFirst_ConstantAmount()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv { IsCareer = true };

            RouteOrchestrator.Tick(1150.0, env); // cycle-0 dispatch, 0 credits
            Assert.Empty(Credits());
            RouteOrchestrator.Tick(1450.0, env); // cycle-1 dispatch, 1 credit (cycle-0)
            Assert.Single(Credits());
            RouteOrchestrator.Tick(1750.0, env); // cycle-2 dispatch, 2 credits (cycle-0, cycle-1)
            Assert.Equal(2, Credits().Count);

            // All credits carry the same constant amount.
            Assert.All(Credits(), c => Assert.Equal((float)Recovered, c.RouteKscFundsCost));
            // Three debits, two credits (deferral tail: one fewer credit than debits).
            int debits = Ledger.Actions.Count(a => a.Type == GameActionType.RouteCargoDebited);
            Assert.Equal(3, debits);
            Assert.Equal(2, Credits().Count);
            // The credits are keyed on the PRIOR cycles (cycle-0, cycle-1).
            var creditCycleIds = Credits().Select(c => c.RouteCycleId).OrderBy(s => s).ToList();
            Assert.Equal(new[] { "cycle-0", "cycle-1" }, creditCycleIds);
        }

        // ==================================================================
        // T-BLOCK: blocked cycle owns no credit but flushes the prior cycle's
        // ==================================================================

        // catches (1): a blocked cycle emitting ANY row for its own id.
        // catches (2): a blocked crossing FAILING to flush the prior dispatched
        // cycle's owed credit.
        [Fact]
        public void BlockedCrossing_NoOwnRow_ButFlushesPriorCredit()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            // Crossing 1 (eligible): dispatch cycle-0, arm pending.
            RouteOrchestrator.Tick(1150.0, new EligibleEnv { IsCareer = true });
            Assert.Equal("cycle-0", route.PendingRecoveryCreditCycleId);
            Assert.Empty(Credits());

            // Crossing 2 (BLOCKED): origin lacks cargo -> emits nothing of its own,
            // but flushes cycle-0's owed credit at this blocked crossing's UT.
            var blocked = new BlockedEnv { IsCareer = true, OriginHasCargoResult = false };
            RouteOrchestrator.Tick(1450.0, blocked);

            // The blocked cycle's own id (cycle-1) has NO debit / delivered / credit.
            Assert.DoesNotContain(Ledger.Actions, a =>
                a.RouteCycleId == "cycle-1" &&
                (a.Type == GameActionType.RouteCargoDebited
                 || a.Type == GameActionType.RouteCargoDelivered
                 || a.Type == GameActionType.RouteRecoveryCredited));
            Assert.Equal(1, route.SkippedCycles);
            // The PRIOR dispatched cycle's credit DID flush.
            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            // Pending cleared after flush.
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        // ==================================================================
        // T-MODE-GATE: off-axis (Sandbox / non-KSC) sets no marker, emits nothing
        // ==================================================================

        [Fact]
        public void NonCareer_NoPendingMarker_NoCredit()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute(isKscOrigin: true);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var sandbox = new EligibleEnv { IsCareer = false };

            RouteOrchestrator.Tick(1150.0, sandbox);
            RouteOrchestrator.Tick(1450.0, sandbox);

            Assert.Empty(Credits());
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        [Fact]
        public void NonKscOrigin_Career_NoPendingMarker_NoCredit()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute(isKscOrigin: false);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var career = new EligibleEnv { IsCareer = true };

            RouteOrchestrator.Tick(1150.0, career);
            RouteOrchestrator.Tick(1450.0, career);

            Assert.Empty(Credits());
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        // catches: a stale Career pending marker carried into Sandbox emitting a
        // credit instead of being cleared.
        [Fact]
        public void StalePendingMarker_ReopenedInSandbox_ClearedNotEmitted()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1000.0;
            var sandbox = new EligibleEnv { IsCareer = false };

            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, sandbox);

            Assert.False(emitted);
            Assert.Empty(Credits());
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("credit-skip non-career-ksc"));
        }

        // ==================================================================
        // T-PAUSE-FLUSH: the final owed credit flushes on pause, once
        // ==================================================================

        // catches: the last dispatched cycle's credit being stranded when the
        // route pauses (its "next crossing" never comes), and a double-credit on
        // resume.
        [Fact]
        public void Pause_FlushesFinalOwedCredit_Once_NoDuplicateOnResume()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv { IsCareer = true };

            // Dispatch cycle-0 (pending armed).
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal("cycle-0", route.PendingRecoveryCreditCycleId);
            Assert.Empty(Credits());

            // Pause: flush the final owed credit (route is Active, immediate pause).
            bool paused = RouteOrchestrator.TryPause(route, 1300.0, env);
            Assert.True(paused);
            Assert.Equal(RouteStatus.Paused, route.Status);
            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Null(route.PendingRecoveryCreditCycleId);

            // Resume + next crossing: NO duplicate cycle-0 credit. TryActivate
            // resets the loop cursor so the next crossing dispatches a fresh cycle.
            RouteOrchestrator.TryActivate(route, 1400.0);
            RouteOrchestrator.Tick(1450.0, env);
            Assert.Single(Credits()); // still only the one cycle-0 credit
        }

        // catches: the armed pause-after-cycle tail (ApplyDeliveryFromPlan honoring
        // Route.PauseAfterCurrentCycle) NOT flushing the owed recovery credit before
        // it transitions the route to Paused. That cycle set its own pending marker
        // during EmitLoopCycle, and there is no further crossing, so the credit must
        // be flushed at the armed-pause transition (section 5.4) or it strands.
        [Fact]
        public void ArmedPauseAfterCycle_FlushesOwedRecoveryCredit_BeforePaused()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute(status: RouteStatus.InTransit);
            // This cycle dispatched + armed its own pending credit during EmitLoopCycle.
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1150.0;
            route.PauseAfterCurrentCycle = true;
            route.PendingStopIndex = 0;

            var writers = new CapturingDeliveryWriters();
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var ctx = new RouteOrchestrator.ApplyDeliveryContext
            {
                CycleId = "cycle-0",
                CurrentUT = 1450.0,
                StopIndex = 0,
                IsCareer = true,
                IsKscOrigin = true,
                KscFundsCost = 0.0,
                ResourceWriter = writers.WriteResource,
                ResourceActualReader = writers.ReadActualResource,
                InventoryWriter = writers.WriteInventory,
                InventoryActualCountReader = writers.ReadInventoryActualCount,
                FundsDebiter = writers.DebitFunds,
                LedgerEmitter = Ledger.AddAction,
            };

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // Route paused, pause-flag consumed.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            // The owed recovery credit flushed at the armed-pause UT, keyed on cycle-0.
            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Equal(1450.0, credit.UT);
            Assert.Equal((float)Recovered, credit.RouteKscFundsCost);
            Assert.Single(liveCredits);
            Assert.Equal(Recovered, liveCredits[0]);
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        // catches: the EndpointLost-at-delivery transition (logistics-recovery-credit
        // section 5.4) failing to flush the route's last dispatched cycle's credit.
        // The live-Vessel ApplyDelivery wrapper is not xUnit-reachable, so this
        // exercises the SAME EmitPendingRecoveryCredit call the EndpointLost path
        // makes (route still Career-KSC, pending marker set, a real later UT), and
        // asserts the credit lands exactly once. The call-site wiring at the
        // EndpointLost transition is verified by reading.
        [Fact]
        public void EndpointLostAtDelivery_FlushCall_EmitsOwedRecoveryCredit()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute(status: RouteStatus.InTransit);
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1150.0;
            var env = new EligibleEnv { IsCareer = true };

            // Mirror the EndpointLost-at-delivery flush: same call, same env shape.
            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1450.0, env);

            Assert.True(emitted);
            var credit = Assert.Single(Credits());
            Assert.Equal("cycle-0", credit.RouteCycleId);
            Assert.Equal(1450.0, credit.UT);
            Assert.Single(liveCredits);
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        // ==================================================================
        // T-NODOUBLE: the keyed backstop suppresses a re-presented credit
        // ==================================================================

        // catches: a save/reload double-tick re-emitting the same (routeId,
        // cycleId) credit. The second call hits IsRecoveryCreditAlreadyInLedger,
        // emits nothing, and clears the stale pending marker.
        [Fact]
        public void RepresentedCrossing_CreditAlreadyInLedger_EmitsOnce()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-0";
            var env = new EligibleEnv { IsCareer = true };

            bool first = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);
            Assert.True(first);
            Assert.Single(Credits());

            // Re-present the SAME pending marker (simulate reload between emit + clear).
            route.PendingRecoveryCreditCycleId = "cycle-0";
            bool second = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);

            Assert.False(second);
            Assert.Single(Credits()); // still exactly one
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("replay") && l.Contains("credit already in ledger"));
        }

        // ==================================================================
        // T-CRASH-WINDOW: owed credit survives a reload; backstop suppresses dup
        // ==================================================================

        // catches: a permanently-missing credit when the pending marker is on disk
        // but the credit row is not yet in ELS (crash before the next crossing).
        [Fact]
        public void PendingOnDisk_CreditNotYetInLedger_FlushesOnNextCrossing()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            // Route reloaded with a pending marker set but no credit row in ledger.
            var route = BuildLoopRoute(lastObservedLoopCycleIndex: 0);
            route.CompletedCycles = 1; // cycle-0 already delivered before the crash
            route.PendingRecoveryCreditCycleId = "cycle-0";
            route.PendingRecoveryCreditDispatchUT = 1150.0;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            var env = new EligibleEnv { IsCareer = true };

            // Next crossing (cycle-1 at 1450) flushes cycle-0's owed credit.
            RouteOrchestrator.Tick(1450.0, env);

            Assert.Contains(Credits(), c => c.RouteCycleId == "cycle-0");
        }

        // catches: the inverse crash (credit already in ELS but pending marker
        // also set) double-emitting. The keyed backstop must suppress + clear.
        [Fact]
        public void CreditInLedger_AndPendingSet_BackstopSuppresses()
        {
            InstallSourceTree();
            SeedRecoveryRow(Recovered);
            var route = BuildLoopRoute();
            // The credit row already landed for cycle-0.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteRecoveryCredited,
                UT = 1300.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteKscFundsCost = (float)Recovered,
            });
            // ... but the pending marker survived (crash between emit and clear).
            route.PendingRecoveryCreditCycleId = "cycle-0";
            var env = new EligibleEnv { IsCareer = true };

            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1300.0, env);

            Assert.False(emitted);
            Assert.Single(Credits()); // the pre-seeded one only
            Assert.Null(route.PendingRecoveryCreditCycleId);
        }

        // ==================================================================
        // T-SUP: tombstoned source recovery -> future credit computes zero;
        //        already-emitted credit rows left intact (RESOLUTION 1)
        // ==================================================================

        [Fact]
        public void TombstonedRecovery_FutureCreditComputesZero_PastCreditsIntact()
        {
            InstallSourceTree();
            GameAction recoveryRow = SeedRecoveryRow(Recovered);

            // A past credit already landed for cycle-0 (RESOLUTION 1: kept).
            var pastCredit = new GameAction
            {
                Type = GameActionType.RouteRecoveryCredited,
                UT = 1300.0,
                RouteId = "route-credit",
                RouteCycleId = "cycle-0",
                RouteKscFundsCost = (float)Recovered,
            };
            Ledger.AddAction(pastCredit);

            // Tombstone the underlying source recovery FundsEarning row + bump the
            // tombstone version so ELS hides it.
            var scenario = new ParsekScenario { LedgerTombstones = new List<LedgerTombstone>() };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.LedgerTombstones.Add(new LedgerTombstone
            {
                TombstoneId = "tomb-recovery",
                ActionId = recoveryRow.ActionId,
                RetiringRecordingId = "rec-refly",
                UT = 600.0,
                CreatedRealTime = "2026-06-06T00:00:00Z",
            });
            scenario.BumpTombstoneStateVersion();

            // A FUTURE credit (cycle-1 pending) now computes zero recovery because
            // SumRecoveredCredits reads ELS (tombstoned row hidden).
            var route = BuildLoopRoute();
            route.PendingRecoveryCreditCycleId = "cycle-1";
            var env = new EligibleEnv { IsCareer = true };

            bool emitted = RouteOrchestrator.EmitPendingRecoveryCredit(route, 1600.0, env);

            Assert.False(emitted); // zero recovery -> no new credit
            Assert.Null(route.PendingRecoveryCreditCycleId);
            // The already-emitted PAST credit row is left intact (RESOLUTION 1).
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.RouteRecoveryCredited && a.RouteCycleId == "cycle-0");
        }

        // ==================================================================
        // T-SUP-NOBLOCK: RouteRecoveryCredited is excluded from the supersede
        //                strict / retry block
        // ==================================================================

        [Fact]
        public void RouteRecoveryCredited_ExcludedFromWorldStateChangingPredicate()
        {
            var credit = new GameAction
            {
                Type = GameActionType.RouteRecoveryCredited,
                RouteId = "route-credit",
                RouteCycleId = "cycle-0",
                RecordingId = "rec-attached", // even with a synthetic RecordingId
                RouteKscFundsCost = (float)Recovered,
            };

            bool worldStateChanging = SupersedeCommit.IsWorldStateChangingRecordingAction(
                credit, new List<GameAction>());

            Assert.False(worldStateChanging);
        }
    }
}
