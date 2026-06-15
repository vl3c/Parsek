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
    /// Pins the M3 Phase 4 two-direction loop applier (plan Phase 4 tasks 1-3,
    /// design D6 / OQ4): a route whose cycle stop carries a <c>PickupManifest</c>
    /// physically debits its endpoint at the dock crossing and emits ONE
    /// <see cref="GameActionType.RouteCargoPickedUp"/> row; a PURE-pickup route
    /// (no delivery manifest) skips the delivery half (no
    /// <see cref="GameActionType.RouteCargoDelivered"/> row); and the per-cycle
    /// ELS replay backstop is re-keyed onto the direction-agnostic
    /// <see cref="GameActionType.RouteDispatched"/> row so a pickup-only route's
    /// endpoint debit is IDEMPOTENT across a re-presentation (the headline
    /// correctness fix - the pre-M3 delivery-keyed backstop never fired for a
    /// pickup-only route, so its debit re-applied every reload).
    ///
    /// <para>Drives the full <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>
    /// loop path with the <see cref="RouteOrchestrator.LoopUnitResolverForTesting"/>,
    /// <see cref="RouteOrchestrator.DeliveryApplierForTesting"/>, and
    /// <see cref="RouteOrchestrator.PickupDebitApplierForTesting"/> seams so the
    /// dispatch-debit half + pickup half + (optional) delivery half are exercised
    /// without a live Vessel.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteLoopPickupFireTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteLoopPickupFireTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            RouteOrchestrator.InventoryPickupApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            RouteOrchestrator.InventoryPickupApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers (mirror RouteLoopDeliveryFireTests)
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

        // Fake delivery applier identical to the delivery-fire-tests contract:
        // emit RouteCargoDelivered + bump CompletedCycles + clear pending.
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

        // Fake pickup-debit applier: returns a hand-built outcome (a full debit of
        // the manifest with a fixed endpoint pid) and records every invocation so
        // the test can assert the endpoint + manifest the production path resolved.
        private int pickupSeamCalls;
        private RouteEndpoint lastPickupEndpoint;
        private Dictionary<string, double> lastPickupManifest;

        private void InstallFakePickupApplier(uint endpointPid = 777u,
            Dictionary<string, double> requestedOnShortfall = null, bool isShort = false,
            bool unresolved = false)
        {
            pickupSeamCalls = 0;
            RouteOrchestrator.PickupDebitApplierForTesting = (ep, manifest, env) =>
            {
                pickupSeamCalls++;
                lastPickupEndpoint = ep;
                lastPickupManifest = manifest;
                Dictionary<string, double> actual = null;
                if (!unresolved && manifest != null)
                {
                    actual = new Dictionary<string, double>(manifest, StringComparer.Ordinal);
                    if (requestedOnShortfall != null)
                    {
                        // Short apply: actual is half the requested.
                        actual = new Dictionary<string, double>(StringComparer.Ordinal);
                        foreach (var kv in manifest)
                            actual[kv.Key] = kv.Value / 2.0;
                    }
                }
                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = actual,
                    RequestedOnShortfall = requestedOnShortfall,
                    OriginVesselPid = unresolved ? 0u : endpointPid,
                    Short = isShort || unresolved,
                    Unresolved = unresolved,
                };
            };
        }

        // Fake INVENTORY pickup-debit applier (M3 Phase 5): returns a hand-built
        // outcome (a full pickup of the manifest with a fixed endpoint pid) and
        // records every invocation so the test can assert the endpoint + inventory
        // the production path resolved.
        private int inventoryPickupSeamCalls;
        private RouteEndpoint lastInventoryEndpoint;
        private List<InventoryPayloadItem> lastInventoryManifest;

        private void InstallFakeInventoryPickupApplier(uint endpointPid = 555u, bool unresolved = false)
        {
            inventoryPickupSeamCalls = 0;
            RouteOrchestrator.InventoryPickupApplierForTesting = (ep, manifest, env) =>
            {
                inventoryPickupSeamCalls++;
                lastInventoryEndpoint = ep;
                lastInventoryManifest = manifest;
                List<InventoryPayloadItem> actual = null;
                if (!unresolved && manifest != null && manifest.Count > 0)
                {
                    actual = new List<InventoryPayloadItem>();
                    foreach (var it in manifest)
                        actual.Add(it.DeepClone());
                }
                return new RouteOrchestrator.InventoryPickupOutcome
                {
                    ActualPickedUp = actual,
                    RequestedOnShortfall = null,
                    EndpointVesselPid = unresolved ? 0u : endpointPid,
                    Short = unresolved,
                    Unresolved = unresolved,
                };
            };
        }

        private static InventoryPayloadItem MakeInvItem(string hash, string partName, int quantity)
        {
            var storedPart = new ConfigNode("STOREDPART");
            storedPart.AddValue("partName", partName);
            storedPart.AddValue("quantity", quantity.ToString(IC));
            return new InventoryPayloadItem
            {
                IdentityHash = hash,
                PartName = partName,
                Quantity = quantity,
                SlotsTaken = 1,
                StoredPartSnapshot = storedPart,
            };
        }

        // Builds a loop route with configurable delivery / pickup manifests.
        private static Route BuildLoopRoute(
            string id = "route-loop",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = true,
            double recordedDockUT = 1150.0,
            long lastObservedLoopCycleIndex = -1,
            Dictionary<string, double> deliveryManifest = null,
            Dictionary<string, double> pickupManifest = null,
            uint endpointPid = 42u)
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
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                CostManifest = new Dictionary<string, double>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = endpointPid },
                        DeliveryManifest = deliveryManifest,
                        PickupManifest = pickupManifest,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Dictionary<string, double> M(params (string, double)[] entries)
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (n, v) in entries) m[n] = v;
            return m;
        }

        // Eligible fake env (all gates pass; no live Vessel needed because both
        // halves are injected fakes).
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

        // ==================================================================
        // Pickup half fires + row shape
        // ==================================================================

        // catches: a route with a pickup manifest NOT firing the pickup half (no
        // RouteCargoPickedUp row), or the row missing the actual manifest /
        // endpoint pid. Mixed window: delivery + pickup both fire under one cycleId.
        [Fact]
        public void MixedWindow_FiresPickupAndDelivery_UnderOneCycleId()
        {
            var route = BuildLoopRoute(
                deliveryManifest: M(("LiquidFuel", 100.0)),
                pickupManifest: M(("Ore", 50.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            InstallFakePickupApplier(endpointPid: 777u);

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var dispatched = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteDispatched);
            var debited = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDebited);
            var pickedUp = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoPickedUp);
            var delivered = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDelivered);

            Assert.NotNull(dispatched);
            Assert.NotNull(debited);
            Assert.NotNull(pickedUp);
            Assert.NotNull(delivered);

            // All four rows share the same cycleId.
            Assert.Equal("cycle-0", dispatched.RouteCycleId);
            Assert.Equal("cycle-0", pickedUp.RouteCycleId);
            Assert.Equal("cycle-0", delivered.RouteCycleId);

            // The pickup seam saw the stop endpoint + the per-window manifest.
            Assert.Equal(1, pickupSeamCalls);
            Assert.Equal(42u, lastPickupEndpoint.VesselPersistentId);
            Assert.Equal(50.0, lastPickupManifest["Ore"]);

            // The pickup row carries the ACTUAL debited manifest + endpoint pid +
            // ZERO funds.
            Assert.Equal(50.0, pickedUp.RouteResourceManifest["Ore"]);
            Assert.Equal(777u, pickedUp.RouteOriginVesselPid);
            Assert.Equal(0f, pickedUp.RouteKscFundsCost);
        }

        // catches (M3 Phase 5, plan D7): a route whose cycle stop carries an
        // INVENTORY pickup manifest NOT firing the inventory pickup half, or the
        // RouteCargoPickedUp row not carrying the picked-up inventory. A
        // pure-INVENTORY-pickup route (no resource pickup, no delivery) fires the
        // pickup half via the inventory dimension alone.
        [Fact]
        public void InventoryPickup_FiresInventoryHalf_RowCarriesInventoryManifest()
        {
            var inventoryManifest = new List<InventoryPayloadItem>
            {
                MakeInvItem("ore-container-hash", "smallCargoContainer", 1),
            };
            var route = BuildLoopRoute(); // no resource pickup, no delivery
            route.Stops[0].InventoryPickupManifest = inventoryManifest;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeInventoryPickupApplier(endpointPid: 555u);
            // No delivery applier, no resource pickup applier: pure inventory pickup.

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var pickedUp = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.NotNull(pickedUp);
            Assert.Equal(1, inventoryPickupSeamCalls);
            // The inventory seam saw the stop endpoint + the per-window inventory.
            Assert.Equal(42u, lastInventoryEndpoint.VesselPersistentId);
            Assert.NotNull(lastInventoryManifest);
            Assert.Equal("ore-container-hash", lastInventoryManifest[0].IdentityHash);
            // The pickup row carries the ACTUAL picked-up inventory manifest + the
            // endpoint pid (from the inventory dimension, since the resource pickup
            // returned pid 0). NO delivery row (pure inventory pickup).
            Assert.NotNull(pickedUp.RouteInventoryManifest);
            Assert.Single(pickedUp.RouteInventoryManifest);
            Assert.Equal("ore-container-hash", pickedUp.RouteInventoryManifest[0].IdentityHash);
            Assert.Equal(555u, pickedUp.RouteOriginVesselPid);
            Assert.Equal(0f, pickedUp.RouteKscFundsCost);
            Assert.Null(Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoDelivered));
        }

        // catches (Sequence / RouteModule out-of-order guard): the pickup row
        // front-running the dispatch row. RouteDispatched MUST stay Seq0; the
        // pickup row gets Seq2 (after dispatch 0 + debit 1) so the ledger walker
        // sees dispatch before the pickup at the shared UT.
        [Fact]
        public void PickupRow_SequenceIsAfterDispatch()
        {
            var route = BuildLoopRoute(pickupManifest: M(("Ore", 50.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier();
            // No delivery applier: pure-pickup route.

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var dispatched = Ledger.Actions.First(a => a.Type == GameActionType.RouteDispatched);
            var pickedUp = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoPickedUp);

            Assert.Equal(0, dispatched.Sequence);
            Assert.Equal(2, pickedUp.Sequence);

            // And the dispatch row physically precedes the pickup row in the ledger
            // (same UT, the walker reads in order).
            var all = Ledger.Actions.ToList();
            int dispatchedIdx = all.FindIndex(a => a.Type == GameActionType.RouteDispatched);
            int pickedUpIdx = all.FindIndex(a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.True(dispatchedIdx < pickedUpIdx);
        }

        // ==================================================================
        // Pure-pickup skips the delivery half (D6)
        // ==================================================================

        // catches (deliverable 3): a pure-pickup route still emitting a
        // RouteCargoDelivered row (the pre-M3 EmitLoopCycle ALWAYS called
        // ApplyDelivery, emitting a delivered row even on an empty plan).
        [Fact]
        public void PurePickup_EmitsNoDeliveredRow()
        {
            var route = BuildLoopRoute(pickupManifest: M(("Ore", 50.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier();
            // Install the delivery applier too, to PROVE it is never invoked.
            bool deliveryApplierCalled = false;
            RouteOrchestrator.DeliveryApplierForTesting = (r, ut, env) => { deliveryApplierCalled = true; };

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.False(deliveryApplierCalled, "pure-pickup route must not call the delivery half");
            // The pickup + dispatch + debit rows DID fire.
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteDispatched);
            // CompletedCycles advanced (the pure-pickup analogue of ApplyDelivery's bump).
            Assert.Equal(1, route.CompletedCycles);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("pure-pickup") && l.Contains("delivery half skipped"));
        }

        // catches (deliverable 3 inverse): an existing delivery-only route firing
        // a phantom pickup or losing its delivered row. Delivery-only is UNCHANGED.
        [Fact]
        public void DeliveryOnly_UnchangedNoPickupRow()
        {
            var route = BuildLoopRoute(deliveryManifest: M(("LiquidFuel", 100.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            // Install the pickup seam to PROVE it is never invoked.
            InstallFakePickupApplier();

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.Equal(0, pickupSeamCalls);
        }

        // ==================================================================
        // The headline correctness fix: pickup-only replay idempotency (OQ4)
        // ==================================================================

        // catches (deliverable 4 / risk register #1): a pickup-ONLY route's
        // endpoint debit RE-APPLYING across a re-presentation. The pre-M3 replay
        // backstop keyed on RouteCargoDelivered (which a pickup-only route never
        // emits), so the guard never fired and the debit re-applied every reload.
        // The OQ4 re-key keys on RouteDispatched (direction-agnostic), so a
        // re-presented crossing emits NOTHING (including no second pickup debit).
        [Fact]
        public void PurePickup_ReplayedCycle_IsIdempotent_NoSecondDebit()
        {
            var route = BuildLoopRoute(pickupManifest: M(("Ore", 50.0)), lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier();
            var env = new EligibleEnv();

            // First crossing: fires the pickup-only cycle.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(1, pickupSeamCalls);
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoPickedUp));
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched));
            Assert.Equal(1, route.CompletedCycles);

            // Simulate a save/reload mid-cycle: reset the in-memory cursor to -1 so
            // the SAME cycle-0 crossing is re-presented, with the dispatch row still
            // in the ledger from the first fire.
            route.LastObservedLoopCycleIndex = -1;
            route.CompletedCycles = 0; // pre-reload counter state
            int pickupCallsBefore = pickupSeamCalls;
            int actionsBefore = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, env); // re-presents cycle-0

            // The dispatch-keyed backstop fired: NOTHING new emitted, and the
            // pickup debit was NOT invoked a second time (the correctness fix).
            Assert.Equal(actionsBefore, Ledger.Actions.Count);
            Assert.Equal(pickupCallsBefore, pickupSeamCalls);
            // Still exactly one pickup row (no double-debit).
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoPickedUp));
            // CompletedCycles bumped so the route progresses past cycle-0.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("already in ledger") && l.Contains("dispatch-keyed"));
        }

        // catches: a MIXED loop route (delivery + pickup) re-applying EITHER half
        // across a re-presentation. The single dispatch-keyed backstop must dominate
        // BOTH halves on the loop path (EmitLoopCycle returns before either applier),
        // so a re-presented crossing emits nothing and neither the pickup debit nor
        // the delivery fires a second time - the single-key idempotency contract.
        [Fact]
        public void MixedWindow_ReplayedCycle_IsIdempotent_NoSecondFire()
        {
            var route = BuildLoopRoute(
                deliveryManifest: M(("LiquidFuel", 100.0)),
                pickupManifest: M(("Ore", 50.0)),
                lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            InstallFakePickupApplier(endpointPid: 777u);
            var env = new EligibleEnv();

            // First crossing: fires the full mixed cycle (dispatch + debit + pickup + delivery).
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(1, pickupSeamCalls);
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoPickedUp));
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoDelivered));
            Assert.Equal(1, route.CompletedCycles);

            // Simulate a save/reload mid-cycle: re-present the SAME cycle-0 crossing
            // with the dispatch row still in the ledger from the first fire.
            route.LastObservedLoopCycleIndex = -1;
            route.CompletedCycles = 0;
            int pickupCallsBefore = pickupSeamCalls;
            int actionsBefore = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, env); // re-presents cycle-0

            // Dispatch-keyed backstop fired: NOTHING new, neither half re-applied.
            Assert.Equal(actionsBefore, Ledger.Actions.Count);
            Assert.Equal(pickupCallsBefore, pickupSeamCalls);
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoPickedUp));
            Assert.Single(Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoDelivered));
            Assert.Equal(1, route.CompletedCycles);
        }

        // catches (deliverable 4): the re-key not preserving the CompletedCycles
        // bump on the replay branch, so a pickup-only route replay-skips FOREVER
        // (recomputes the same cycleId every crossing). Pre-seed a RouteDispatched
        // row for cycle-0; the replay branch must bump CompletedCycles so the NEXT
        // crossing computes cycle-1.
        [Fact]
        public void PurePickup_ReplayBranch_BumpsCompletedCycles_AdvancesId()
        {
            var route = BuildLoopRoute(pickupManifest: M(("Ore", 50.0)), lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier();
            var env = new EligibleEnv();

            // Pre-seed a dispatched row for cycle-0 (reloaded with stale cursor).
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = 150.0,
                RouteId = route.Id,
                RouteCycleId = "cycle-0",
                RouteStopIndex = -1,
                Sequence = 0,
            });
            int before = Ledger.Actions.Count;

            RouteOrchestrator.Tick(1150.0, env);

            // Replay-skipped: no new rows, no pickup invocation.
            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Equal(0, pickupSeamCalls);
            // CompletedCycles bumped so the next crossing advances past cycle-0.
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
        }

        // ==================================================================
        // Crashed-disposition: a lost delivery endpoint still debits the pickup
        // ==================================================================

        // catches: a downstream delivery failure suppressing the pickup debit. The
        // pickup half fires BEFORE the delivery half under one cycleId, so even if
        // the delivery endpoint is lost at delivery time the pickup row + endpoint
        // debit are already committed (the pickup is independent of the delivery
        // disposition). Uses the production ApplyDelivery (delivery seam null) with
        // a KSC origin + a lost delivery endpoint.
        [Fact]
        public void MixedWindow_DeliveryEndpointLost_PickupStillDebited()
        {
            var route = BuildLoopRoute(
                isKscOrigin: true,
                deliveryManifest: M(("LiquidFuel", 100.0)),
                pickupManifest: M(("Ore", 50.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier(endpointPid: 777u);
            // DeliveryApplierForTesting NOT installed -> production ApplyDelivery,
            // which fails to resolve the destination (the env below returns
            // (true, null) at delivery time).

            RouteOrchestrator.Tick(1150.0, new DeliveryLostEnv());

            // The pickup half committed regardless of the delivery disposition.
            var pickedUp = Ledger.Actions.FirstOrDefault(a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.NotNull(pickedUp);
            Assert.Equal(777u, pickedUp.RouteOriginVesselPid);
            Assert.Equal(1, pickupSeamCalls);
            // The delivery half lost its endpoint.
            Assert.Contains(Ledger.Actions, a => a.Type == GameActionType.RouteEndpointLost);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
        }

        // Eligible at crossing time but the DELIVERY endpoint resolves null at
        // delivery time (mirrors RouteLoopDeliveryFireTests' EligibleButDeliveryLostEnv).
        private sealed class DeliveryLostEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason)
            { vessel = null; reason = "no-live-vessels"; return false; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // Short pickup: requested-on-shortfall recorded + warn
        // ==================================================================

        // catches: a short pickup apply not recording the requested manifest on the
        // pickup row, or the clamp landing silently (no Warn).
        [Fact]
        public void PickupShortAtApply_RecordsRequestedManifest_Warns()
        {
            var route = BuildLoopRoute(pickupManifest: M(("Ore", 100.0)));
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakePickupApplier(
                endpointPid: 777u,
                requestedOnShortfall: M(("Ore", 100.0)),
                isShort: true);

            RouteOrchestrator.Tick(1150.0, new EligibleEnv());

            var pickedUp = Ledger.Actions.First(a => a.Type == GameActionType.RouteCargoPickedUp);
            Assert.NotNull(pickedUp.RouteRequestedResourceManifest);
            Assert.Equal(100.0, pickedUp.RouteRequestedResourceManifest["Ore"]);
            Assert.Equal(50.0, pickedUp.RouteResourceManifest["Ore"]); // half (clamped)
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("[Route]") && l.Contains("SHORT at apply"));
        }

        // ==================================================================
        // Pure-pickup decision helpers (deliverable 3 unit coverage)
        // ==================================================================

        [Fact]
        public void RouteHasPickupManifest_TrueOnlyWithNonEmptyManifest()
        {
            Assert.True(RouteOrchestrator.RouteHasPickupManifest(
                BuildLoopRoute(pickupManifest: M(("Ore", 50.0)))));
            Assert.False(RouteOrchestrator.RouteHasPickupManifest(
                BuildLoopRoute(pickupManifest: new Dictionary<string, double>())));
            Assert.False(RouteOrchestrator.RouteHasPickupManifest(BuildLoopRoute()));
        }

        [Fact]
        public void RouteHasDeliveryManifest_TrueOnlyWithNonEmptyManifest()
        {
            Assert.True(RouteOrchestrator.RouteHasDeliveryManifest(
                BuildLoopRoute(deliveryManifest: M(("LiquidFuel", 100.0)))));
            Assert.False(RouteOrchestrator.RouteHasDeliveryManifest(
                BuildLoopRoute(deliveryManifest: new Dictionary<string, double>())));
            Assert.False(RouteOrchestrator.RouteHasDeliveryManifest(
                BuildLoopRoute(pickupManifest: M(("Ore", 50.0))))); // pure-pickup
        }
    }
}
