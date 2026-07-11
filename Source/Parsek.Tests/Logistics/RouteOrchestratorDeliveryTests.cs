using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the delivery applier (item 6 Phase B). Most tests exercise the
    /// testable seam <see cref="RouteOrchestrator.ApplyDeliveryFromPlan"/>
    /// directly by injecting fake writer delegates, so the assertions don't
    /// need a live <c>Vessel</c> / <c>Funding</c>. The end-to-end
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// path is covered by <see cref="ProcessOneRoute_DeliversThenDispatchesInSameTick"/>
    /// and <see cref="EndpointLost_PendingDelivery_DoesNotApply"/> /
    /// <see cref="Paused_PendingDelivery_DoesNotApply"/> at the bottom.
    /// </summary>
    [Collection("Sequential")]
    public class RouteOrchestratorDeliveryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteOrchestratorDeliveryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Fakes
        // ==================================================================

        /// <summary>
        /// Capturing writer bundle for <see cref="RouteOrchestrator.ApplyDeliveryFromPlan"/>.
        /// Each delegate records its calls; the actuals reader returns the sum
        /// of resource amounts written so far for that resource, which models
        /// the production loaded-path contract (writer drives the actual,
        /// reader reports total transferred).
        /// </summary>
        private sealed class CapturingWriters
        {
            public readonly List<(string Name, double Amount)> ResourceCalls = new List<(string, double)>();
            public readonly List<(InventoryPayloadItem Item, int Slot)> InventoryCalls = new List<(InventoryPayloadItem, int)>();
            public readonly List<double> FundsDebits = new List<double>();
            public readonly List<GameAction> EmittedActions = new List<GameAction>();

            public void WriteResource(string name, double amount) => ResourceCalls.Add((name, amount));

            public double ReadActualResource(string name)
            {
                double total = 0.0;
                for (int i = 0; i < ResourceCalls.Count; i++)
                    if (ResourceCalls[i].Name == name)
                        total += ResourceCalls[i].Amount;
                return total;
            }

            public void WriteInventory(InventoryPayloadItem item, int slot) => InventoryCalls.Add((item, slot));

            public int ReadInventoryActualCount() => InventoryCalls.Count;

            public void DebitFunds(double cost) => FundsDebits.Add(cost);

            public void EmitAction(GameAction a) => EmittedActions.Add(a);
        }

        // ==================================================================
        // Fixture builders
        // ==================================================================

        private static Route BuildInTransitKscRoute(
            string id = "route-1",
            int completedCycles = 0,
            int skippedCycles = 0,
            bool isKscOrigin = true,
            double kscFundsCost = 1000.0)
        {
            return new Route
            {
                Id = id,
                Status = RouteStatus.InTransit,
                IsKscOrigin = isKscOrigin,
                CompletedCycles = completedCycles,
                SkippedCycles = skippedCycles,
                CurrentCycleStartUT = 100.0,
                TransitDuration = 60.0,
                NextDispatchUT = 1_000_000.0, // no double-dispatch in the same tick
                PendingDeliveryUT = 150.0,
                PendingStopIndex = 0,
                KscDispatchFundsCost = kscFundsCost,
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
                CostManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 120.0 },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef
                    {
                        RecordingId = "rec-" + id,
                        TreeId = "tree-1",
                        RouteProofHash = "deadbeef",
                    },
                },
            };
        }

        private static DeliveryPlan BuildFullFillPlan(Dictionary<string, double> manifest)
        {
            var resources = new List<ResourceDeliveryLine>();
            foreach (var kv in manifest)
                resources.Add(new ResourceDeliveryLine(kv.Key, kv.Value, kv.Value));
            return new DeliveryPlan(resources, Array.Empty<InventoryDeliveryLine>(), isPartial: false, isZero: false);
        }

        private static RouteOrchestrator.ApplyDeliveryContext BuildContext(
            CapturingWriters writers,
            string cycleId = "cycle-0",
            double ut = 200.0,
            int stopIndex = 0,
            bool isCareer = true,
            bool isKscOrigin = true,
            double kscFundsCost = 1000.0,
            bool bumpCompletedCycle = true)
        {
            return new RouteOrchestrator.ApplyDeliveryContext
            {
                CycleId = cycleId,
                CurrentUT = ut,
                StopIndex = stopIndex,
                IsCareer = isCareer,
                IsKscOrigin = isKscOrigin,
                KscFundsCost = isCareer && isKscOrigin ? kscFundsCost : 0.0,
                ResourceWriter = writers.WriteResource,
                ResourceActualReader = writers.ReadActualResource,
                InventoryWriter = writers.WriteInventory,
                InventoryActualCountReader = writers.ReadInventoryActualCount,
                FundsDebiter = writers.DebitFunds,
                LedgerEmitter = writers.EmitAction,
                // M4a A3 (Horn A): the direct ApplyDeliveryFromPlan tests model a
                // single complete delivery, so the cycle-complete CompletedCycles
                // bump fires (the pre-A3 unconditional behaviour). Multi-stop earlier
                // windows would pass false; no direct-core test exercises that.
                BumpCompletedCycle = bumpCompletedCycle,
            };
        }

        // ==================================================================
        // Tests
        // ==================================================================

        // catches: missing funds debit OR wrong sign.
        [Fact]
        public void HappyPath_KscCareer_FullFill_EmitsActionAndDebitsFunds()
        {
            var route = BuildInTransitKscRoute(kscFundsCost: 1234.0);
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers, isCareer: true, isKscOrigin: true, kscFundsCost: 1234.0);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // Resource writes executed for each manifest entry.
            Assert.Equal(2, writers.ResourceCalls.Count);
            Assert.Contains(writers.ResourceCalls, c => c.Name == "LiquidFuel" && c.Amount == 100.0);
            Assert.Contains(writers.ResourceCalls, c => c.Name == "Oxidizer" && c.Amount == 120.0);

            // Funds debited exactly once with the positive cost magnitude
            // (the sign is applied at the LiveDebitFunds production callsite).
            Assert.Single(writers.FundsDebits);
            Assert.Equal(1234.0, writers.FundsDebits[0]);

            // RouteCargoDelivered emitted with the full manifest, no requested
            // manifest (no shortfall), correct cycle/stop identity, and the
            // KSC funds cost surfaced.
            Assert.Single(writers.EmittedActions);
            GameAction a = writers.EmittedActions[0];
            Assert.Equal(GameActionType.RouteCargoDelivered, a.Type);
            Assert.Equal(route.Id, a.RouteId);
            Assert.Equal("cycle-0", a.RouteCycleId);
            Assert.Equal(0, a.RouteStopIndex);
            Assert.NotNull(a.RouteResourceManifest);
            Assert.Equal(2, a.RouteResourceManifest.Count);
            Assert.Equal(100.0, a.RouteResourceManifest["LiquidFuel"]);
            Assert.Equal(120.0, a.RouteResourceManifest["Oxidizer"]);
            Assert.Null(a.RouteRequestedResourceManifest);
            Assert.Equal(1234f, a.RouteKscFundsCost);

            // Route state: cycle counter advanced, pending cleared, status Active.
            Assert.Equal(1, route.CompletedCycles);
            Assert.False(route.PendingDeliveryUT.HasValue);
            Assert.Equal(-1, route.PendingStopIndex);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        // C-3: closes the production-stride coverage gap. The multi-stop fire tests
        // use the DeliveryRowEmitterForTesting seam, which MIRRORS the production
        // stride (stopIndex*8 + 3) rather than RUNNING ApplyDeliveryFromPlan's own
        // stride computation. This drives the REAL ApplyDeliveryFromPlan with
        // ctx.StopIndex = 1 and asserts the emitted RouteCargoDelivered row carries
        // RouteStopIndex == 1 and Sequence == 11 (1*SeqStride + 3) - the actual
        // production-side stride, not a test mirror.
        [Fact]
        public void ApplyDeliveryFromPlan_StopIndex1_EmitsStopIndex1AndSequence11()
        {
            var route = BuildInTransitKscRoute(kscFundsCost: 500.0);
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var writers = new CapturingWriters();
            // ctx.StopIndex = 1: the second window of a multi-stop cycle. The stride
            // base is ctx.StopIndex * SeqStride, so the delivery row's Sequence is
            // 1*8 + 3 = 11. bumpCompletedCycle false models an EARLIER multi-stop
            // window (the caller owns the once-per-cycle bump).
            var ctx = BuildContext(
                writers, stopIndex: 1, isCareer: true, isKscOrigin: true,
                kscFundsCost: 500.0, bumpCompletedCycle: false);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            Assert.Single(writers.EmittedActions);
            GameAction a = writers.EmittedActions[0];
            Assert.Equal(GameActionType.RouteCargoDelivered, a.Type);
            Assert.Equal(1, a.RouteStopIndex);
            // Production stride: ctx.StopIndex(1) * SeqStride(8) + delivery offset(3).
            Assert.Equal(1 * RouteOrchestrator.SeqStride + 3, a.Sequence);
            Assert.Equal(11, a.Sequence);
            // Earlier window: the caller (ProcessMultiStopCrossings) owns the bump,
            // so ApplyDeliveryFromPlan with bumpCompletedCycle=false leaves the
            // counter alone (the cycleId the later windows compute stays stable).
            Assert.Equal(0, route.CompletedCycles);
        }

        // catches: unconditional funds debit leaking to Sandbox.
        [Fact]
        public void HappyPath_Sandbox_NoFundsDebit_StillEmitsAction()
        {
            var route = BuildInTransitKscRoute();
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var writers = new CapturingWriters();
            // Sandbox: isCareer=false (even though origin is KSC the gate must
            // still suppress the debit).
            var ctx = BuildContext(writers, isCareer: false, isKscOrigin: true, kscFundsCost: 1234.0);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // No funds debit.
            Assert.Empty(writers.FundsDebits);

            // But the cycle still completes and the delivered row goes out
            // with zero funds cost.
            Assert.Single(writers.EmittedActions);
            GameAction a = writers.EmittedActions[0];
            Assert.Equal(GameActionType.RouteCargoDelivered, a.Type);
            Assert.Equal(0f, a.RouteKscFundsCost);
            Assert.Equal(1, route.CompletedCycles);
        }

        // catches: partial-fill manifest missing or including fully-filled resources.
        [Fact]
        public void PartialFill_PopulatesRequestedManifest()
        {
            var route = BuildInTransitKscRoute();
            // LiquidFuel filled fully (100/100); Oxidizer filled half (60/120).
            var plan = new DeliveryPlan(
                new[]
                {
                    new ResourceDeliveryLine("LiquidFuel", 100.0, 100.0),
                    new ResourceDeliveryLine("Oxidizer", 120.0, 60.0),
                },
                Array.Empty<InventoryDeliveryLine>(),
                isPartial: true,
                isZero: false);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers, isCareer: true, isKscOrigin: true);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            Assert.Single(writers.EmittedActions);
            GameAction a = writers.EmittedActions[0];
            Assert.Equal(GameActionType.RouteCargoDelivered, a.Type);

            // Actual manifest carries BOTH resources (both had > 0 actuals).
            Assert.NotNull(a.RouteResourceManifest);
            Assert.Equal(2, a.RouteResourceManifest.Count);
            Assert.Equal(100.0, a.RouteResourceManifest["LiquidFuel"]);
            Assert.Equal(60.0, a.RouteResourceManifest["Oxidizer"]);

            // Requested manifest is populated ONLY for the partial resource.
            // The fully-filled one would just bloat the row.
            Assert.NotNull(a.RouteRequestedResourceManifest);
            Assert.Single(a.RouteRequestedResourceManifest);
            Assert.Equal(120.0, a.RouteRequestedResourceManifest["Oxidizer"]);
            Assert.DoesNotContain("LiquidFuel", a.RouteRequestedResourceManifest.Keys);

            // Status transition uses the partial reason string.
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Contains(logLines, l => l.Contains("delivered-partial"));
        }

        // catches: zero-fill silently treated as no-op (cycle never completes,
        // infinite-pending).
        [Fact]
        public void ZeroFill_StillEmitsRouteCargoDeliveredWithEmptyActual()
        {
            var route = BuildInTransitKscRoute();
            // Destination is completely full — every resource gets 0 available.
            var plan = new DeliveryPlan(
                new[]
                {
                    new ResourceDeliveryLine("LiquidFuel", 100.0, 0.0),
                    new ResourceDeliveryLine("Oxidizer", 120.0, 0.0),
                },
                Array.Empty<InventoryDeliveryLine>(),
                isPartial: true,
                isZero: true);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // Even on zero fill, the cycle MUST complete — otherwise
            // PendingDeliveryUT stays set forever and the route gets stuck.
            Assert.Single(writers.EmittedActions);
            GameAction a = writers.EmittedActions[0];
            Assert.Equal(GameActionType.RouteCargoDelivered, a.Type);

            // Actual manifest is null/empty (no resource delivered > 0).
            Assert.True(a.RouteResourceManifest == null || a.RouteResourceManifest.Count == 0);

            // Requested manifest carries every requested resource.
            Assert.NotNull(a.RouteRequestedResourceManifest);
            Assert.Equal(2, a.RouteRequestedResourceManifest.Count);
            Assert.Equal(100.0, a.RouteRequestedResourceManifest["LiquidFuel"]);
            Assert.Equal(120.0, a.RouteRequestedResourceManifest["Oxidizer"]);

            // Cycle counter advanced, pending cleared, status Active.
            Assert.Equal(1, route.CompletedCycles);
            Assert.False(route.PendingDeliveryUT.HasValue);
            Assert.Equal(-1, route.PendingStopIndex);
            Assert.Equal(RouteStatus.Active, route.Status);
        }

        // catches: apply path forging through a missing destination.
        [Fact]
        public void EndpointLostMidTransit_EmitsRouteEndpointLost_NoFundsNoDelivered()
        {
            // This test exercises the live wrapper (RouteOrchestrator.Tick →
            // ProcessOneRoute → ApplyDelivery). The fake env's
            // TryResolveEndpointVessel surfaces a null Vessel, which the
            // applier treats as "endpoint destroyed at delivery."
            var route = BuildInTransitKscRoute();
            RouteStore.AddRoute(route);
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // RouteEndpointLost emitted, NO RouteCargoDelivered, NO funds row.
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteEndpointLost);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            // No FundsSpending row either (the applier never reaches the funds
            // step on an endpoint-lost abort).
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.FundsSpending);

            // Status flipped, pending cleared, completed cycles NOT bumped.
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            Assert.False(route.PendingDeliveryUT.HasValue);
            Assert.Equal(-1, route.PendingStopIndex);
            Assert.Equal(0, route.CompletedCycles);
        }

        // catches: crash/recovery double-emit; would corrupt CompletedCycles.
        [Fact]
        public void IdempotentReplay_SkipsWhenActionAlreadyInELS()
        {
            var route = BuildInTransitKscRoute();
            RouteStore.AddRoute(route);

            // Pre-seed the ledger with an already-delivered row for cycle-0
            // (this simulates the orchestrator running, the action being
            // persisted to the save, and then the save being reloaded — at
            // which point the same cycle's PendingDeliveryUT is still set in
            // the in-memory Route but the ELS already carries the row).
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles);
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 0,
            });

            int actionsBefore = Ledger.Actions.Count;
            // Env will succeed-but-null-vessel; the idempotency check fires
            // FIRST so we never reach the endpoint-resolution path.
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // No new ledger actions. No funds row, no second delivered row,
            // no endpoint-lost row.
            Assert.Equal(actionsBefore, Ledger.Actions.Count);

            // Status flipped back to Active with the "delivered-replay" reason;
            // pending cleared so the route progresses past the boundary.
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.False(route.PendingDeliveryUT.HasValue);
            Assert.Equal(-1, route.PendingStopIndex);
            // CompletedCycles MUST be advanced: the ledger row says the cycle
            // was delivered, so the route's counter must reflect that. If it
            // stayed at 0, the next dispatch would re-use "cycle-0" — colliding
            // with the very row we replayed against and forever looping the
            // dispatch/replay/dispatch redundancy. See
            // <see cref="IdempotentReplay_ThenNextDispatch_AdvancesCycleId"/>
            // for the no-collision contract.
            Assert.Equal(1, route.CompletedCycles);

            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("Delivery") && l.Contains("replay"));
        }

        // catches: P2-1 regression — replay branch failing to advance the
        // cycle counter would let the next dispatch reuse the replayed
        // cycleId. Pre-seed cycle-0 delivered, drive a Tick where delivery
        // (replay) AND dispatch are both due, and assert the new dispatch
        // emits cycle-1 (NOT cycle-0).
        [Fact]
        public void IdempotentReplay_ThenNextDispatch_AdvancesCycleId()
        {
            var route = BuildInTransitKscRoute();
            // Both due: delivery boundary at 150 and dispatch slot at 180.
            route.PendingDeliveryUT = 150.0;
            route.NextDispatchUT = 180.0;
            RouteStore.AddRoute(route);

            // Pre-seed an already-delivered row for cycle-0 — the replay
            // branch in ApplyDelivery short-circuits to "delivered-replay"
            // and (with the P2-1 fix) bumps CompletedCycles to 1.
            string replayedCycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles);
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = replayedCycleId,
                RouteStopIndex = 0,
                Sequence = 0,
            });
            Assert.Equal("cycle-0", replayedCycleId);

            int actionsBefore = Ledger.Actions.Count;
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // Replay branch advanced CompletedCycles so the new dispatch uses
            // the NEXT cycle id (cycle-1), not the replayed one (cycle-0).
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(RouteStatus.InTransit, route.Status);

            int newCount = Ledger.Actions.Count - actionsBefore;
            Assert.Equal(2, newCount);

            // The new dispatched row pins the no-collision contract.
            GameAction newDispatched = null;
            GameAction newDebited = null;
            for (int i = actionsBefore; i < Ledger.Actions.Count; i++)
            {
                GameAction a = Ledger.Actions[i];
                if (a.Type == GameActionType.RouteDispatched) newDispatched = a;
                else if (a.Type == GameActionType.RouteCargoDebited) newDebited = a;
            }
            Assert.NotNull(newDispatched);
            Assert.NotNull(newDebited);
            Assert.Equal("cycle-1", newDispatched.RouteCycleId);
            Assert.NotEqual("cycle-0", newDispatched.RouteCycleId);
            // The debited row is the dispatch's pair and must share the new
            // cycleId; Sequence=1 pins it after the dispatched row at the
            // same UT (mirrors ApplyDispatch).
            Assert.Equal("cycle-1", newDebited.RouteCycleId);
            Assert.Equal(newDispatched.UT, newDebited.UT);
            Assert.True(newDebited.Sequence > newDispatched.Sequence);
        }

        // catches: reason string regression for UI consumers.
        [Fact]
        public void StatusTransition_PartialReason_VsFullReason()
        {
            // Full fill → "delivered"
            {
                var route = BuildInTransitKscRoute(id: "route-full");
                var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
                var writers = new CapturingWriters();
                var ctx = BuildContext(writers, cycleId: "cycle-0");

                RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

                Assert.Equal(RouteStatus.Active, route.Status);
                Assert.Contains(logLines, l =>
                    l.Contains("[Route]") && l.Contains("InTransit→Active") && l.Contains("delivered")
                    && !l.Contains("delivered-partial"));
            }

            logLines.Clear();
            Ledger.ResetForTesting();

            // Partial fill → "delivered-partial"
            {
                var route = BuildInTransitKscRoute(id: "route-partial");
                var plan = new DeliveryPlan(
                    new[] { new ResourceDeliveryLine("LiquidFuel", 100.0, 50.0) },
                    Array.Empty<InventoryDeliveryLine>(),
                    isPartial: true,
                    isZero: false);
                var writers = new CapturingWriters();
                var ctx = BuildContext(writers, cycleId: "cycle-0");

                RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

                Assert.Equal(RouteStatus.Active, route.Status);
                Assert.Contains(logLines, l =>
                    l.Contains("[Route]") && l.Contains("InTransit→Active") && l.Contains("delivered-partial"));
            }
        }

        // catches: a regression where PauseAfterCurrentCycle is set on a route
        // but the delivery applier still transitions back to Active (auto-loop
        // continues). The "Send Once" semantic depends on this flag being
        // honored on delivery completion.
        [Fact]
        public void StatusTransition_PauseAfterCurrentCycle_TransitionsToPausedNotActive()
        {
            var route = BuildInTransitKscRoute(id: "route-oneshot");
            route.PauseAfterCurrentCycle = true;
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers, cycleId: "cycle-0");

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // Route transitioned to Paused (not Active) and the flag is
            // cleared so a subsequent un-pause + dispatch doesn't auto-pause
            // again unless re-armed.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("InTransit→Paused") && l.Contains("delivered-then-paused"));
        }

        // catches: a regression where partial delivery + PauseAfterCurrentCycle
        // emits the wrong status-transition reason token.
        [Fact]
        public void StatusTransition_PauseAfterCurrentCycle_PartialFill_EmitsPartialPausedReason()
        {
            var route = BuildInTransitKscRoute(id: "route-oneshot-partial");
            route.PauseAfterCurrentCycle = true;
            var plan = new DeliveryPlan(
                new[] { new ResourceDeliveryLine("LiquidFuel", 100.0, 50.0) },
                Array.Empty<InventoryDeliveryLine>(),
                isPartial: true,
                isZero: false);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers, cycleId: "cycle-0");

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("InTransit→Paused") && l.Contains("delivered-partial-then-paused"));
        }

        // catches: catch-up loop not continuing after delivery — would require
        // two ticks where one is sufficient.
        [Fact]
        public void ProcessOneRoute_DeliversThenDispatchesInSameTick()
        {
            // Route is in transit, has a pending delivery due, AND its
            // NextDispatchUT is also due. One Tick must deliver THEN dispatch.
            var route = BuildInTransitKscRoute();
            route.PendingDeliveryUT = 150.0;
            route.NextDispatchUT = 180.0;
            RouteStore.AddRoute(route);

            // EndpointLostFakeEnv returns null Vessel, which will EndpointLost
            // the route — we don't want that here. Use a fake that surfaces a
            // null vessel only when requested otherwise. For this test, we
            // need a TRUE endpoint resolution at delivery time. Since the
            // fake's null vessel path goes through ApplyDelivery → EndpointLost,
            // we need a different shape: pre-seed an idempotent-skip so the
            // delivery applier short-circuits to "delivered-replay" without
            // needing a real Vessel reference. Then verify that on the same
            // tick the dispatch eval STILL runs and dispatches the next cycle.
            string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles);
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 0,
            });

            int actionsBefore = Ledger.Actions.Count;
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // After the same tick:
            // (1) delivery short-circuited via idempotency → route became Active.
            // (2) dispatch eval saw Active + NextDispatchUT due → route became InTransit.
            Assert.Equal(RouteStatus.InTransit, route.Status);
            Assert.Equal(200.0, route.CurrentCycleStartUT);
            Assert.Equal(200.0 + route.DispatchInterval, route.NextDispatchUT);

            // Ledger picked up the new RouteDispatched + RouteCargoDebited pair
            // (the delivered row was pre-seeded; the catch-up loop continuing
            // is proved by these new rows landing in the same tick).
            int newCount = Ledger.Actions.Count - actionsBefore;
            Assert.Equal(2, newCount);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteDispatched && a.UT == 200.0);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDebited && a.UT == 200.0);
        }

        // catches: status gate regression — applying delivery for paused route
        // charges funds player didn't authorize.
        [Fact]
        public void Paused_PendingDelivery_DoesNotApply()
        {
            var route = BuildInTransitKscRoute();
            route.Status = RouteStatus.Paused;
            route.PendingDeliveryUT = 150.0;
            RouteStore.AddRoute(route);
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // NO delivery applied: no ledger rows, no completed cycle bump,
            // pending UT still set (route resumes on unpause), status still
            // Paused.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.True(route.PendingDeliveryUT.HasValue);
            Assert.Equal(150.0, route.PendingDeliveryUT.Value);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Empty(Ledger.Actions);
        }

        // catches: writer/probe leaking deliveries into player-closed tanks.
        // The probe's flowState skip is mirrored on the writer side via the
        // shared ShouldDeliverToResource helper; this pure-helper test pins
        // the policy without needing a live PartResource. A regression that
        // removes the flowState check inside the writer would still leave
        // this test passing — but the in-game test path (Phase C) covers
        // the live mutation. This test pins the policy seam itself: a future
        // refactor that turns the helper into `return true` blows up here.
        [Fact]
        public void ShouldDeliverToResource_ClosedTank_ReturnsFalse()
        {
            // flowState=false (closed tank), normal flow mode → blocked.
            Assert.False(RouteOrchestrator.ShouldDeliverToResource(false, ResourceFlowMode.ALL_VESSEL));
            Assert.False(RouteOrchestrator.ShouldDeliverToResource(false, ResourceFlowMode.STAGE_PRIORITY_FLOW));
            // flowState=false is the dominant gate: even NO_FLOW + closed is still false.
            Assert.False(RouteOrchestrator.ShouldDeliverToResource(false, ResourceFlowMode.NO_FLOW));
        }

        // catches: a SolidFuel/EVAPropellant/Ablator delivery silently dumping
        // into the destination — those are NO_FLOW per stock contract.
        [Fact]
        public void ShouldDeliverToResource_NoFlowResource_ReturnsFalse()
        {
            // NO_FLOW mode with open tank → still blocked.
            Assert.False(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.NO_FLOW));
        }

        // catches: helper accidentally blocking the happy path (every normal
        // resource on an open tank must remain deliverable).
        [Fact]
        public void ShouldDeliverToResource_OpenTank_NormalFlow_ReturnsTrue()
        {
            Assert.True(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.ALL_VESSEL));
            Assert.True(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.STAGE_PRIORITY_FLOW));
            Assert.True(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.STACK_PRIORITY_SEARCH));
            Assert.True(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.ALL_VESSEL_BALANCE));
            Assert.True(RouteOrchestrator.ShouldDeliverToResource(true, ResourceFlowMode.STAGE_STACK_FLOW));
        }

        // catches: P2-4 regression — a successful delivery leaving a stale
        // NextEligibilityCheckUT on the route, blocking future ticks behind
        // an old retry deadline that was set during a pre-dispatch wait.
        [Fact]
        public void HappyPath_ClearsNextEligibilityCheckUT()
        {
            var route = BuildInTransitKscRoute();
            // Simulate a stale retry timer left over from a prior wait state
            // (the dispatch eval consumed the wait and cleared status, but a
            // pre-Phase-B regression could have left this field non-null).
            route.NextEligibilityCheckUT = 12345.0;
            var plan = BuildFullFillPlan(route.Stops[0].DeliveryManifest);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, plan, ctx);

            // Successful delivery exits any wait state — timer must be cleared.
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(1, route.CompletedCycles);
        }

        // catches: P2-1 regression — a future refactor that calls
        // ApplyInTransitComplete directly, bypassing the evaluator's
        // "already pending" Skip gate, must NOT overwrite the existing
        // PendingDeliveryUT. The applier-side guard in RouteOrchestrator.cs
        // around line ~381 is defense-in-depth; this test pins it.
        [Fact]
        public void ApplyInTransitComplete_PendingDeliveryUtAlreadySet_DoesNotReset()
        {
            var route = BuildInTransitKscRoute();
            route.Status = RouteStatus.InTransit;
            route.PendingDeliveryUT = 100.0;
            route.PendingStopIndex = 3; // distinctive non-zero so reset would also be caught

            // Drive the applier directly with a different "current UT" — a
            // broken guard would overwrite PendingDeliveryUT=100.0 with 200.0.
            RouteOrchestrator.ApplyInTransitComplete(route, 200.0);

            // Existing pending state preserved bit-for-bit.
            Assert.True(route.PendingDeliveryUT.HasValue);
            Assert.Equal(100.0, route.PendingDeliveryUT.Value);
            Assert.Equal(3, route.PendingStopIndex);

            // The defensive guard emits a Verbose breadcrumb so operators
            // can trace why a re-set was suppressed.
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("InTransitComplete")
                && l.Contains("already has PendingDeliveryUT")
                && l.Contains("skipping re-set"));
        }

        // catches: P2-2 regression — probe captures isLoaded at construction
        // time but writer re-evaluates vessel.loaded && !vessel.packed per
        // call (or vice versa). If those diverge mid-tick (KSP synchronously
        // transitions packed state on warp boundaries, focus changes, scene
        // events), the planner sees one source of free capacity while the
        // writer mutates the other branch — under-fill or write into a
        // snapshot about to be re-initialized. Pin the contract structurally:
        // both consumers must accept the SAME injected isLoaded and store it
        // verbatim, so the orchestrator-side capture in ApplyDelivery is the
        // single source of truth.
        [Fact]
        public void Delivery_ProbeAndWriter_UseSameLoadedGate()
        {
            // Same emptyish plan structure for both pure-construction cases;
            // we are pinning the gate-threading shape, not exercising the
            // mutation paths.
            var plan = new DeliveryPlan(
                Array.Empty<ResourceDeliveryLine>(),
                Array.Empty<InventoryDeliveryLine>(),
                isPartial: false, isZero: true);
            var route = BuildInTransitKscRoute();

            // Construct both with isLoaded=true. The probe + writer must
            // store the value we passed in (NOT re-evaluate it from the
            // null Vessel). A constructor that re-derives the gate from
            // vessel.loaded would throw NRE here; a constructor that
            // ignores the parameter would yield isLoaded=false and fail
            // the assertion.
            var probeLoaded = new LiveDeliveryCapacityProbe(vessel: null, isLoaded: true);
            var writerLoaded = new LiveDeliveryWriters(route, vessel: null, plan, isLoaded: true);
            Assert.True(probeLoaded.isLoaded);
            Assert.True(writerLoaded.isLoaded);
            Assert.Equal(probeLoaded.isLoaded, writerLoaded.isLoaded);

            // And the symmetric unloaded case — same single source of truth.
            var probeUnloaded = new LiveDeliveryCapacityProbe(vessel: null, isLoaded: false);
            var writerUnloaded = new LiveDeliveryWriters(route, vessel: null, plan, isLoaded: false);
            Assert.False(probeUnloaded.isLoaded);
            Assert.False(writerUnloaded.isLoaded);
            Assert.Equal(probeUnloaded.isLoaded, writerUnloaded.isLoaded);

            // Cross-pairing must NOT match — defensive: confirms the field is
            // actually consulted (a constant-true / constant-false bug would
            // pass the symmetric checks above but fail here).
            Assert.NotEqual(probeLoaded.isLoaded, probeUnloaded.isLoaded);
            Assert.NotEqual(writerLoaded.isLoaded, writerUnloaded.isLoaded);
        }

        // catches: status gate regression — would emit RouteCargoDelivered on
        // a dead route.
        [Fact]
        public void EndpointLost_PendingDelivery_DoesNotApply()
        {
            var route = BuildInTransitKscRoute();
            route.Status = RouteStatus.EndpointLost;
            route.PendingDeliveryUT = 150.0;
            RouteStore.AddRoute(route);
            var env = new EndpointLostFakeEnv();

            RouteOrchestrator.Tick(200.0, env);

            // Status untouched — the route is already aborted; delivery must
            // not silently revive it.
            Assert.Equal(RouteStatus.EndpointLost, route.Status);
            // No RouteCargoDelivered emitted.
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.Equal(0, route.CompletedCycles);
        }

        // ==================================================================
        // End-to-end fake env (only the live-wrapper tests use this)
        // ==================================================================

        /// <summary>
        /// Minimal fake env for the few end-to-end (Tick-driven) delivery
        /// tests. Returns null Vessel from <see cref="TryResolveEndpointVessel"/>
        /// — applier treats this as endpoint-lost-at-delivery. Tests that
        /// don't want the lost path either pre-seed an idempotent-skip ledger
        /// row or pin <see cref="RouteStatus.Paused"/> / <c>EndpointLost</c>
        /// to suppress the applier entirely.
        /// </summary>
        private sealed class EndpointLostFakeEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer => false;
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason)
            {
                reason = string.Empty;
                return true;
            }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            {
                vessel = null;
                reason = "fake-null-vessel";
                return false;
            }
            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                lackingResource = string.Empty;
                return true;
            }
            public bool KscFundsAvailable(Route route, out double shortfall)
            {
                shortfall = 0.0;
                return true;
            }
            public bool DestinationHasCapacity(Route route, out string fullResource)
            {
                fullResource = string.Empty;
                return true;
            }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // Last-partial-delivery report (destination-capacity gate follow-up)
        // ==================================================================

        // catches: a partial delivery not recording the loss report, or a
        // subsequent FULL delivery leaving the stale report standing.
        [Fact]
        public void PartialDelivery_RecordsReport_FullDeliveryClearsIt()
        {
            var route = BuildInTransitKscRoute();
            var partialPlan = new DeliveryPlan(
                new[]
                {
                    new ResourceDeliveryLine("LiquidFuel", 100.0, 100.0),
                    new ResourceDeliveryLine("Oxidizer", 120.0, 60.0),
                },
                Array.Empty<InventoryDeliveryLine>(),
                isPartial: true,
                isZero: false);
            var writers = new CapturingWriters();
            var ctx = BuildContext(writers, ut: 500.0);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, partialPlan, ctx);

            Assert.NotNull(route.LastPartialDeliverySummary);
            Assert.Contains("Oxidizer", route.LastPartialDeliverySummary);
            Assert.DoesNotContain("LiquidFuel", route.LastPartialDeliverySummary);
            Assert.Equal(500.0, route.LastPartialDeliveryUT);

            // The next FULL delivery clears the report.
            route.PendingDeliveryUT = 150.0; // re-arm the fixture
            var fullPlan = BuildFullFillPlan(new Dictionary<string, double>
            {
                { "LiquidFuel", 100.0 },
            });
            var writers2 = new CapturingWriters();
            var ctx2 = BuildContext(writers2, cycleId: "cycle-1", ut: 600.0);

            RouteOrchestrator.ApplyDeliveryFromPlan(route, fullPlan, ctx2);

            Assert.Null(route.LastPartialDeliverySummary);
            Assert.Equal(-1.0, route.LastPartialDeliveryUT);
        }

        // catches: the summary including full-fill lines (noise), losing the
        // actual/requested numbers, or missing the skipped-inventory clause.
        [Fact]
        public void BuildPartialDeliverySummary_Shapes()
        {
            var plan = new DeliveryPlan(
                new[]
                {
                    new ResourceDeliveryLine("LiquidFuel", 100.0, 100.0), // full
                    new ResourceDeliveryLine("Oxidizer", 120.0, 60.0),    // short
                },
                new[]
                {
                    new InventoryDeliveryLine(
                        new InventoryPayloadItem { IdentityHash = "h", PartName = "evaJetpack", Quantity = 2 },
                        -1), // skipped: no slot
                    new InventoryDeliveryLine(
                        new InventoryPayloadItem { IdentityHash = "h2", PartName = "sensorThermometer", Quantity = 1 },
                        0), // stored
                },
                isPartial: true,
                isZero: false);

            string summary = RouteOrchestrator.BuildPartialDeliverySummary(
                plan, name => name == "Oxidizer" ? 60.0 : 100.0,
                inventoryActualCount: 1, inventoryLinesAttempted: 1);

            Assert.Equal("Oxidizer 60/120; evaJetpack 0/2 (no slot)", summary);

            // Rejected stored-part writes get their own clause.
            string withRejects = RouteOrchestrator.BuildPartialDeliverySummary(
                plan, name => name == "Oxidizer" ? 60.0 : 100.0,
                inventoryActualCount: 0, inventoryLinesAttempted: 1);
            Assert.Contains("1 stored-part write(s) rejected by the container", withRejects);

            // Defensive: a partial plan with no identifiable short line still
            // yields non-empty text.
            string fallback = RouteOrchestrator.BuildPartialDeliverySummary(
                DeliveryPlan.Empty(), _ => 0.0, 0, 0);
            Assert.False(string.IsNullOrEmpty(fallback));

            // Length cap: a pathological manifest cannot bloat the .sfs.
            var manyLines = new List<ResourceDeliveryLine>();
            for (int i = 0; i < 50; i++)
                manyLines.Add(new ResourceDeliveryLine("Resource" + i, 100.0, 1.0));
            var bigPlan = new DeliveryPlan(
                manyLines, Array.Empty<InventoryDeliveryLine>(), isPartial: true, isZero: false);
            string capped = RouteOrchestrator.BuildPartialDeliverySummary(
                bigPlan, _ => 1.0, 0, 0);
            Assert.True(capped.Length <= 243, $"summary length {capped.Length} exceeds cap");
            Assert.EndsWith("...", capped);
        }
    }
}
