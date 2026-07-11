using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime smoke check for the per-stop delivery applier (item 6
    /// of the logistics plan, Phase C). Injects a synthetic InTransit
    /// <see cref="Route"/> with <c>PendingDeliveryUT</c> already due and a
    /// <c>LiquidFuel</c> delivery manifest into <see cref="RouteStore"/>,
    /// pre-drains the active vessel's LiquidFuel so there's headroom, drives
    /// one <see cref="RouteOrchestrator.Tick(double)"/> through the production
    /// no-env overload (live <c>LiveRouteRuntimeEnvironment</c> + live
    /// <see cref="RouteEndpointResolver"/> + live <c>PartResource</c> writers),
    /// and asserts:
    ///
    ///   * Route transitioned out of <see cref="RouteStatus.InTransit"/> back
    ///     to <see cref="RouteStatus.Active"/> with <c>CompletedCycles == 1</c>.
    ///   * <c>PendingDeliveryUT</c> cleared and <c>PendingStopIndex == -1</c>.
    ///   * The active vessel's LiquidFuel amount increased by the manifest
    ///     amount (within a small tolerance — the loaded-path writer mutates
    ///     <see cref="PartResource.amount"/> directly).
    ///   * A new <see cref="GameActionType.RouteCargoDelivered"/> action exists
    ///     in <see cref="Ledger.Actions"/> tagged with the synthetic route id.
    ///
    /// xUnit covers <see cref="RouteOrchestrator.ApplyDeliveryFromPlan"/> with
    /// injected delegate writers; this test exercises the production-only path
    /// where the wrapper builds a <c>LiveDeliveryWriters</c> bundle that walks
    /// live <c>Vessel.parts</c> and mutates real <see cref="PartResource"/>
    /// amounts. A regression that NREs inside the live writer, mis-keys the
    /// flowState/NO_FLOW gate, or fails to clear pending state on success
    /// would surface here.
    ///
    /// The synthetic route is removed in <c>finally</c> and the pre-drain
    /// LiquidFuel amount is restored on the part so the test leaves no
    /// residue in <see cref="RouteStore.CommittedRoutes"/> or the live
    /// vessel state. The ledger row stays — <see cref="Ledger"/> is
    /// append-only and the synthetic route id is distinct from any real
    /// route's id so the row is harmless for downstream replay.
    /// </summary>
    public sealed class LogisticsDeliveryRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmount = 5.0;
        private const double ResourceTolerance = 0.01;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates RouteStore, Ledger, and live PartResource.amount under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "RouteOrchestrator.Tick applies a pending delivery to the active vessel: the route returns to Active with CompletedCycles=1, LiquidFuel is topped up by the manifest amount, and a RouteCargoDelivered ledger row is emitted")]
        public IEnumerator Delivery_LoadedVessel_AppliesResourceTransfer()
        {
            // Post-restore unpack wait: the isolated-batch baseline quickload
            // leaves the active vessel packed for a few frames, which used to
            // skip this test whenever it followed another destructive test
            // (same defect class as the M1 origin-debit tests, fixed there
            // 2026-06-11). Yields happen before any drain/seam.
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITION CHECKS -------------------------------------------------
            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium.fetch is null; cannot resolve current UT");

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            // Find any part on the active vessel that holds LiquidFuel. The
            // delivery applier walks vessel.parts in order and fills the first
            // tank with free capacity, so we just need at least one tank with
            // headroom after the pre-drain.
            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(activeVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "skipping — pick a vessel with at least one LF tank to run this test");

            // The delivery applier chooses the loaded-path writer (which mutates the live
            // PartResource this test reads) ONLY when the destination vessel is loaded+unpacked. A
            // vessel sitting on the pad in PRELAUNCH reports as unloaded, so the delivery writes the
            // ProtoPartResourceSnapshot instead and the live tank the test pre-drained never changes.
            // Skip rather than false-fail when the active vessel is not on the loaded path.
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={activeVessel.loaded}, packed={activeVessel.packed}); the delivery would take " +
                    "the unloaded proto-snapshot path which does not mutate the live PartResource this test reads");

            // PRE-DRAIN: ensure there's headroom for the synthetic delivery.
            // Cap the drain at max(0, current - DeliveryAmount) so a tiny tank
            // can still receive at least some of the manifest (the test
            // tolerates a partial fill — the applier sets CompletedCycles even
            // on partial deliveries — but the resource-amount assertion is
            // skipped when the tank's capacity is below DeliveryAmount).
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            double preDrainTarget = originalAmount - DeliveryAmount;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double postDrainAmount = fuelResource.amount;

            // EXPECTED post-delivery amount = post-drain + min(DeliveryAmount, headroom).
            // Headroom is capped at maxAmount-postDrain so we don't assert a
            // bigger fill than the tank physically holds.
            double headroom = maxAmount - postDrainAmount;
            double expectedDelta = DeliveryAmount < headroom ? DeliveryAmount : headroom;
            double expectedAmount = postDrainAmount + expectedDelta;

            string syntheticRouteId = "ingame-delivery-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            double currentUT = Planetarium.GetUniversalTime();

            // Build the synthetic InTransit route inline. RouteFixtureBuilder
            // lives in Source/Parsek.Tests/Generators/ and is not visible
            // from the production assembly, so we author the route directly
            // here — same field shape, just hand-written.
            //
            // PendingDeliveryUT is already past so the pre-evaluator delivery
            // hook in ProcessOneRoute fires on this tick. NextDispatchUT is
            // far in the future so the dispatch evaluator does not also fire
            // a second cycle in the same tick (which would re-set
            // PendingDeliveryUT and confuse the assertions).
            Route synthetic = new Route
            {
                Id = syntheticRouteId,
                Name = "Parsek Delivery Smoke Test",
                Status = RouteStatus.InTransit,
                IsKscOrigin = true,
                TransitDuration = 60.0,
                DispatchInterval = 600.0,
                NextDispatchUT = currentUT + 600.0,
                CurrentCycleStartUT = currentUT - 100.0,
                CurrentSegmentIndex = 0,
                PendingDeliveryUT = currentUT - 1.0,
                PendingStopIndex = 0,
                CompletedCycles = 0,
                KscDispatchFundsCost = 0.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, DeliveryAmount },
                },
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = activeVessel.persistentId,
                            BodyName = activeVessel.mainBody != null ? activeVessel.mainBody.bodyName : "Kerbin",
                            IsSurface = false,
                        },
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, DeliveryAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        DeliveryOffsetSeconds = 60.0,
                    },
                },
            };

            RouteStore.AddRoute(synthetic);
            bool addedToStore = true;

            string fuelPartNameForLog = fuelPart.partInfo != null ? fuelPart.partInfo.name : "<unknown>";

            try
            {
                ParsekLog.Verbose("TestRunner",
                    $"Delivery_LoadedVessel: pre-tick routeId={syntheticRouteId} " +
                    $"vessel={activeVessel.vesselName} pid={activeVessel.persistentId.ToString(IC)} " +
                    $"partName='{fuelPartNameForLog}' " +
                    $"originalAmount={originalAmount.ToString("R", IC)} " +
                    $"postDrainAmount={postDrainAmount.ToString("R", IC)} " +
                    $"maxAmount={maxAmount.ToString("R", IC)} " +
                    $"expectedAmount={expectedAmount.ToString("R", IC)} " +
                    $"beforeLedgerCount={beforeLedgerCount.ToString(IC)}");

                // ACT — production no-env overload. ProcessOneRoute's pre-evaluator
                // delivery hook fires because PendingDeliveryUT is already due.
                RouteOrchestrator.Tick(currentUT);

                // Yield one frame in case any deferred behavior settles on the
                // next FixedUpdate.
                yield return null;

                // ASSERTIONS ---------------------------------------------------

                // 1. Route state: delivered → Active, cycle completed, pending cleared.
                Route postTick;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(syntheticRouteId, out postTick),
                    "Synthetic route disappeared from store during Tick");
                InGameAssert.IsNotNull(postTick,
                    "TryGetRoute returned true but post-tick route was null");

                InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                    $"Expected route status to transition to Active after delivery, but was {postTick.Status}");
                InGameAssert.AreEqual(1, postTick.CompletedCycles,
                    $"Expected CompletedCycles=1 after delivery, but was {postTick.CompletedCycles.ToString(IC)}");
                string pendingForLog = postTick.PendingDeliveryUT.HasValue
                    ? postTick.PendingDeliveryUT.Value.ToString("R", IC)
                    : "<null>";
                InGameAssert.IsFalse(postTick.PendingDeliveryUT.HasValue,
                    $"PendingDeliveryUT should be cleared after delivery applied (was {pendingForLog})");
                InGameAssert.AreEqual(-1, postTick.PendingStopIndex,
                    $"Expected PendingStopIndex=-1 after delivery, but was {postTick.PendingStopIndex.ToString(IC)}");

                // 2. Live resource: vessel's LiquidFuel topped up by manifest amount
                //    (within tolerance). The applier writes pr.amount directly on
                //    the loaded path, so re-reading the SAME PartResource here is
                //    the correct probe — no need to walk the vessel again.
                double actualAmount = fuelResource.amount;
                double diff = Math.Abs(actualAmount - expectedAmount);
                InGameAssert.IsTrue(
                    diff < ResourceTolerance,
                    $"Expected LiquidFuel ~= {expectedAmount.ToString("R", IC)} on part " +
                    $"'{fuelPartNameForLog}' " +
                    $"after delivery, but was {actualAmount.ToString("R", IC)} (diff={diff.ToString("R", IC)} tol={ResourceTolerance.ToString("R", IC)})");

                // 3. Ledger row: a RouteCargoDelivered action with our route id
                //    must have been appended during the tick.
                int afterLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                bool deliveredRowFound = false;
                if (afterLedgerCount > beforeLedgerCount && Ledger.Actions != null)
                {
                    for (int i = beforeLedgerCount; i < afterLedgerCount; i++)
                    {
                        GameAction action = Ledger.Actions[i];
                        if (action == null) continue;
                        if (action.Type != GameActionType.RouteCargoDelivered) continue;
                        if (!string.Equals(action.RouteId, syntheticRouteId, StringComparison.Ordinal)) continue;
                        deliveredRowFound = true;
                        break;
                    }
                }
                InGameAssert.IsTrue(
                    deliveredRowFound,
                    $"No RouteCargoDelivered ledger row found for synthetic routeId={syntheticRouteId} " +
                    $"(beforeCount={beforeLedgerCount.ToString(IC)} afterCount={afterLedgerCount.ToString(IC)}). " +
                    "ApplyDeliveryFromPlan should have emitted one via ctx.LedgerEmitter.");

                ParsekLog.Info("TestRunner",
                    $"Delivery_LoadedVessel: PASS routeId={syntheticRouteId} " +
                    $"status={postTick.Status} completedCycles={postTick.CompletedCycles.ToString(IC)} " +
                    $"fuelBefore={postDrainAmount.ToString("R", IC)} fuelAfter={actualAmount.ToString("R", IC)} " +
                    $"newLedgerRows={(afterLedgerCount - beforeLedgerCount).ToString(IC)}");
            }
            finally
            {
                // TEARDOWN: remove the synthetic route and restore the pre-drain
                // LiquidFuel amount so the player's save / next batch test do
                // not see the pre-drain or the synthetic delivery. The ledger
                // row stays (Ledger is append-only); the synthetic RouteId is
                // distinct from any real route's id so the row is harmless.
                if (addedToStore)
                {
                    bool removed = RouteStore.RemoveRoute(syntheticRouteId);
                    ParsekLog.Verbose("TestRunner",
                        $"Delivery_LoadedVessel cleanup: RemoveRoute(synthetic)={removed}");
                }

                // Restore LiquidFuel to the value we read before the pre-drain
                // step. fuelResource is a reference into the live vessel, so
                // this writes directly back. Guard against a null reference if
                // the part was destroyed mid-test.
                try
                {
                    if (fuelResource != null)
                    {
                        fuelResource.amount = originalAmount;
                        ParsekLog.Verbose("TestRunner",
                            $"Delivery_LoadedVessel cleanup: restored LiquidFuel to {originalAmount.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"Delivery_LoadedVessel cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }


        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - fills and clears live ModuleInventoryPart slots on the active vessel; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session with two cargo containers.",
            Description = "Multi-module inventory delivery (loaded path): with the first inventory container full, the capacity probe hands out a slot on a LATER container and LiveDeliveryWriters stores the payload into exactly that (part, module, slot) address")]
        public IEnumerator Delivery_MultiModule_FirstContainerFullSecondReceives()
        {
            // Post-restore unpack wait (same rationale as the resource test above).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // PRECONDITION CHECKS -------------------------------------------------
            Vessel vessel = FlightGlobals.ActiveVessel;
            if (vessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel with two cargo containers");
            if (!(vessel.loaded && !vessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={vessel.loaded}, packed={vessel.packed}); this test exercises the loaded probe/writer branch");

            // Collect inventory modules in PROBE order: vessel part order, then
            // module order within the part. Indices here are the exact
            // InventorySlotAddress coordinates the probe/writer contract uses.
            var moduleRefs = new List<(int PartIndex, int ModuleIndex, ModuleInventoryPart Module)>();
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (p.Modules[m] is ModuleInventoryPart module && module.InventorySlots > 0)
                        moduleRefs.Add((i, m, module));
                }
            }
            if (moduleRefs.Count < 2)
                InGameAssert.Skip(
                    $"Active vessel '{vessel.vesselName}' has {moduleRefs.Count.ToString(IC)} inventory module(s); " +
                    "need at least two (e.g. a pod plus a cargo container) to prove the multi-module walk");

            var first = moduleRefs[0];

            // Resolve a small stock cargo part to fill slots with.
            string fillPartName = null;
            string[] candidates = { "evaRepairKit", "evaScience", "evaJetpack", "evaChute" };
            for (int c = 0; c < candidates.Length; c++)
            {
                if (PartLoader.getPartInfoByName(candidates[c]) != null)
                {
                    fillPartName = candidates[c];
                    break;
                }
            }
            if (fillPartName == null)
                InGameAssert.Skip("No known stock cargo part (evaRepairKit/evaScience/evaJetpack/evaChute) resolved via PartLoader");

            // Find the payload-donor slot: any empty slot on any module. The
            // stored part is immediately serialized to a STOREDPART node (the
            // canonical Parsek payload shape) and the slot cleared again.
            int donorModuleIdx = -1, donorSlot = -1;
            for (int r = 0; r < moduleRefs.Count && donorModuleIdx < 0; r++)
            {
                int s = FirstProbeEmptySlot(moduleRefs[r].Module);
                if (s >= 0) { donorModuleIdx = r; donorSlot = s; }
            }
            if (donorModuleIdx < 0)
                InGameAssert.Skip("Every inventory module on the active vessel is already full; cannot build a delivery payload");

            var filledFirstModuleSlots = new List<int>();
            InventorySlotAddress deliveredAddress = InventorySlotAddress.None;
            ModuleInventoryPart deliveredModule = null;
            ConfigNode payloadNode = null;
            // Tracks the donor fill until it is cleared, so a Skip/throw
            // between the donor store and the donor clear still tears it down.
            ModuleInventoryPart pendingDonorModule = null;
            int pendingDonorSlot = -1;

            try
            {
                ModuleInventoryPart donor = moduleRefs[donorModuleIdx].Module;
                if (!donor.StoreCargoPartAtSlot(fillPartName, donorSlot))
                    InGameAssert.Skip(
                        $"StoreCargoPartAtSlot('{fillPartName}', {donorSlot.ToString(IC)}) failed on the donor module; " +
                        "cannot build a delivery payload (volume/mass limit?)");
                pendingDonorModule = donor;
                pendingDonorSlot = donorSlot;
                payloadNode = new ConfigNode("STOREDPART");
                donor.storedParts[donorSlot].Save(payloadNode);
                bool donorCleared = donor.ClearPartAtSlot(donorSlot);
                InGameAssert.IsTrue(donorCleared, "ClearPartAtSlot failed to clear the payload-donor slot");
                pendingDonorModule = null;
                pendingDonorSlot = -1;

                // ARRANGE: fill EVERY empty slot of the FIRST inventory module so
                // the old first-module-only probe would report "no slot".
                for (int s = 0; s < first.Module.InventorySlots; s++)
                {
                    if (first.Module.storedParts != null && first.Module.storedParts.ContainsKey(s)) continue;
                    if (!first.Module.StoreCargoPartAtSlot(fillPartName, s))
                        InGameAssert.Skip(
                            $"Could not fill slot {s.ToString(IC)} of the first inventory module with '{fillPartName}' " +
                            "(volume/mass limit?); cannot establish the full-first-container precondition");
                    filledFirstModuleSlots.Add(s);
                }
                InGameAssert.IsTrue(FirstProbeEmptySlot(first.Module) < 0,
                    "First inventory module still reports an empty slot after the fill");

                // The EXPECTED address: first module after the (now full) first
                // one with an empty slot, in probe order.
                InventorySlotAddress expected = InventorySlotAddress.None;
                ModuleInventoryPart expectedModule = null;
                for (int r = 1; r < moduleRefs.Count; r++)
                {
                    int s = FirstProbeEmptySlot(moduleRefs[r].Module);
                    if (s >= 0)
                    {
                        expected = new InventorySlotAddress(moduleRefs[r].PartIndex, moduleRefs[r].ModuleIndex, s);
                        expectedModule = moduleRefs[r].Module;
                        break;
                    }
                }
                if (!expected.IsValid)
                    InGameAssert.Skip("No later inventory module has an empty slot left; cannot prove the multi-module handoff");

                // ACT 1: probe. The old single-module probe returned -1 here.
                var probe = new LiveDeliveryCapacityProbe(vessel, isLoaded: true);
                InventorySlotAddress address = probe.ProbeFirstEmptyInventorySlot();

                InGameAssert.IsTrue(address.IsValid,
                    "Probe returned None with the first container full but a later container empty - the multi-module walk regressed");
                InGameAssert.IsFalse(
                    address.PartIndex == first.PartIndex && address.ModuleIndex == first.ModuleIndex,
                    $"Probe assigned a slot on the FULL first module: {address}");
                InGameAssert.AreEqual(expected, address,
                    $"Probe address {address} does not match the deterministic expectation {expected}");

                // ACT 2: writer stores into exactly the assigned address.
                var route = new Route { Id = "ingame-multimodule-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
                var writers = new LiveDeliveryWriters(route, vessel, DeliveryPlan.Empty(), isLoaded: true);
                var item = new InventoryPayloadItem
                {
                    PartName = fillPartName,
                    Quantity = 1,
                    SlotsTaken = 1,
                    StoredPartSnapshot = payloadNode,
                };
                writers.WriteInventory(item, address, 1);

                deliveredAddress = address;
                deliveredModule = expectedModule;
                InGameAssert.AreEqual(1, writers.ReadInventoryActualCount(),
                    "LiveDeliveryWriters did not report a successful store into the second container");
                InGameAssert.IsTrue(
                    expectedModule.storedParts != null && expectedModule.storedParts.ContainsKey(address.SlotIndex),
                    $"Delivered payload is not present at the assigned address {address}");

                ParsekLog.Info("TestRunner",
                    $"Delivery_MultiModule_FirstContainerFullSecondReceives: PASS " +
                    $"vessel={vessel.vesselName} fillPart={fillPartName} " +
                    $"firstModule=part{first.PartIndex.ToString(IC)}/mod{first.ModuleIndex.ToString(IC)} " +
                    $"filledSlots={filledFirstModuleSlots.Count.ToString(IC)} delivered={address}");
            }
            finally
            {
                // TEARDOWN: clear the delivered payload and every slot this test
                // filled, so the vessel's inventories return to their pre-test
                // occupancy.
                try
                {
                    int cleared = 0;
                    if (pendingDonorModule != null && pendingDonorSlot >= 0
                        && pendingDonorModule.storedParts != null
                        && pendingDonorModule.storedParts.ContainsKey(pendingDonorSlot))
                    {
                        if (pendingDonorModule.ClearPartAtSlot(pendingDonorSlot)) cleared++;
                    }
                    if (deliveredModule != null && deliveredAddress.IsValid
                        && deliveredModule.storedParts != null
                        && deliveredModule.storedParts.ContainsKey(deliveredAddress.SlotIndex))
                    {
                        if (deliveredModule.ClearPartAtSlot(deliveredAddress.SlotIndex)) cleared++;
                    }
                    for (int i = 0; i < filledFirstModuleSlots.Count; i++)
                    {
                        if (first.Module.ClearPartAtSlot(filledFirstModuleSlots[i])) cleared++;
                    }
                    ParsekLog.Verbose("TestRunner",
                        $"Delivery_MultiModule cleanup: clearedSlots={cleared.ToString(IC)} " +
                        $"(filled={filledFirstModuleSlots.Count.ToString(IC)} " +
                        $"delivered={(deliveredAddress.IsValid ? 1 : 0).ToString(IC)} " +
                        $"pendingDonor={(pendingDonorSlot >= 0 ? 1 : 0).ToString(IC)})");
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"Delivery_MultiModule cleanup threw {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// First empty slot of one live inventory module using the SAME
        /// occupancy rule the delivery capacity probe applies on the loaded
        /// branch (ascending slot index, occupied = storedParts key present),
        /// or -1 when full. Local mirror so the test's expectation is computed
        /// independently of the probe under test.
        /// </summary>
        private static int FirstProbeEmptySlot(ModuleInventoryPart module)
        {
            for (int s = 0; s < module.InventorySlots; s++)
            {
                if (module.storedParts != null && module.storedParts.ContainsKey(s)) continue;
                return s;
            }
            return -1;
        }

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates a live ModuleInventoryPart slot under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "LiveDeliveryWriters.WriteInventory with units>1 against the LOADED active vessel stores the payload via stock StoreCargoPartAtSlot AND raises the slot's stack to the planned unit count via UpdateStackAmountAtSlot; the actual-count reader reports the unit-accurate total. PENDING-OPERATOR precondition: the active vessel needs an inventory container holding at least one STACKABLE cargo part (e.g. an EVA Repair Kit) plus one empty slot")]
        public IEnumerator Delivery_LoadedVessel_StacksInventoryQuantityIntoSlot()
        {
            // Post-restore unpack wait (see Delivery_LoadedVessel_AppliesResourceTransfer).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked; this test exercises the loaded StoreCargoPartAtSlot + UpdateStackAmountAtSlot path");

            // The production writer stores into the vessel's FIRST inventory
            // module — target the same one so the assertion reads the slot the
            // writer wrote.
            ModuleInventoryPart module = LiveDeliveryWriters.FindFirstInventoryModule(activeVessel);
            if (module == null)
                InGameAssert.Skip(
                    "PRECONDITION: active vessel has no ModuleInventoryPart. Fly a craft with a cargo " +
                    "container (e.g. an SEQ-3 storage unit) to run this test");

            // Payload: clone an existing STACKABLE stored cargo part from the
            // vessel (same source pattern as the pickup runtime tests — the
            // recorded payload shape is exactly StoredPart.Save's output).
            if (!TryFindStackableStoredCargo(activeVessel, out ConfigNode payloadNode, out int stackCapacity))
                InGameAssert.Skip(
                    "PRECONDITION: no STACKABLE stored cargo part (stackCapacity > 1) on the active vessel. " +
                    "Store a stackable cargo part (e.g. an EVA Repair Kit) in any inventory container to run this test");

            int targetSlot = FirstEmptySlot(module);
            if (targetSlot < 0)
                InGameAssert.Skip(
                    "PRECONDITION: the vessel's first inventory module has no empty slot. Free a slot to run this test");

            int units = Math.Min(stackCapacity, 3);
            InGameAssert.IsTrue(units > 1, "Stackable payload must allow units > 1 for a meaningful stack assertion");

            var item = new InventoryPayloadItem
            {
                IdentityHash = VesselSpawner.ComputeInventoryPayloadIdentityHash(payloadNode),
                PartName = payloadNode.GetValue("partName"),
                VariantName = payloadNode.GetValue("variantName"),
                Quantity = units,
                SlotsTaken = 1,
                StoredPartSnapshot = payloadNode,
            };

            bool stored = false;
            try
            {
                var writers = new LiveDeliveryWriters(null, activeVessel, DeliveryPlan.Empty(), isLoaded: true);
                // Address the SAME first module (part/module index in the
                // probe's coordinate space) at the chosen empty slot.
                InventorySlotAddress targetAddress = ResolveModuleAddress(activeVessel, module, targetSlot);
                writers.WriteInventory(item, targetAddress, units);
                stored = module.storedParts != null && module.storedParts.ContainsKey(targetSlot);

                InGameAssert.IsTrue(stored,
                    $"WriteInventory(units={units.ToString(IC)}) should store the payload at slot {targetSlot.ToString(IC)}");
                StoredPart storedPart = module.storedParts[targetSlot];
                InGameAssert.IsNotNull(storedPart, "Stored slot must hold a StoredPart");
                InGameAssert.AreEqual(units, storedPart.quantity,
                    $"Loaded path must stack the planned unit count into the slot (Gap A: stock StoreCargoPartAtSlot alone stores quantity=1); got quantity={storedPart.quantity.ToString(IC)} expected {units.ToString(IC)}");
                InGameAssert.AreEqual(units, writers.ReadInventoryActualCount(),
                    $"Actual-count reader must be unit-accurate; got {writers.ReadInventoryActualCount().ToString(IC)} expected {units.ToString(IC)}");

                ParsekLog.Info("TestRunner",
                    $"Delivery_StacksInventory: PASS vessel={activeVessel.vesselName} " +
                    $"part={item.PartName} slot={targetSlot.ToString(IC)} " +
                    $"units={units.ToString(IC)} stackCapacity={stackCapacity.ToString(IC)}");
            }
            finally
            {
                // TEARDOWN: clear the delivered slot so the player's inventory
                // is unchanged.
                if (stored)
                {
                    try
                    {
                        bool cleared = module.ClearPartAtSlot(targetSlot);
                        ParsekLog.Verbose("TestRunner",
                            $"Delivery_StacksInventory cleanup: ClearPartAtSlot({targetSlot.ToString(IC)})={cleared}");
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"Delivery_StacksInventory cleanup: ClearPartAtSlot threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            yield break;
        }

        // ==================================================================
        // Live probe volume/mass admission wiring (loaded/unloaded parity fix)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            Description = "LiveDeliveryCapacityProbe admission wiring against the live container: admitted units match an independently computed prefab-limit budget, admission never overshoots the volume limit, ConsumeInventoryCapacity decrements follow-up admissions, an unresolvable item prefab admits 0 (fail closed), and the stackable-quantity probe reads the prefab. Read-only (probe internals only), batch-safe")]
        public IEnumerator Delivery_Probe_AdmissionMatchesIndependentBudget()
        {
            // Post-restore unpack wait (see Delivery_LoadedVessel_AppliesResourceTransfer):
            // without it, running right after an isolated restore would skip on
            // the packed gate below.
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to probe");
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked; this test exercises the loaded budget walk");

            // Same module the probe's budget targets (and the writer stores into).
            ModuleInventoryPart module = LiveDeliveryWriters.FindFirstInventoryModule(activeVessel);
            if (module == null)
                InGameAssert.Skip(
                    "PRECONDITION: active vessel has no ModuleInventoryPart. Fly a craft with a cargo " +
                    "container (e.g. an SEQ-3 storage unit) to run this test");

            // INDEPENDENT budget mirroring the probe's multi-module
            // aggregation: prefab container limits SUMMED across ALL inventory
            // modules (any unlimited axis, limit <= 0, makes that axis
            // unlimited vessel-wide, represented as limit 0) + occupancy
            // recomputed stock-style across every module. Same expressions as
            // the probe so the comparison is float-exact.
            bool volumeUnlimited = false;
            bool massUnlimited = false;
            double volumeLimitSum = 0.0;
            double massLimitSum = 0.0;
            double volumeOccupied = 0.0;
            double massOccupied = 0.0;
            int storedPartCount = 0;
            for (int pi = 0; pi < activeVessel.parts.Count; pi++)
            {
                Part invPart = activeVessel.parts[pi];
                if (invPart == null || invPart.Modules == null) continue;
                for (int mi = 0; mi < invPart.Modules.Count; mi++)
                {
                    if (!(invPart.Modules[mi] is ModuleInventoryPart mod)) continue;
                    ModuleInventoryPart prefabModule =
                        invPart.partInfo != null && invPart.partInfo.partPrefab != null
                            ? invPart.partInfo.partPrefab.FindModuleImplementing<ModuleInventoryPart>()
                            : null;
                    InGameAssert.IsNotNull(prefabModule,
                        "A live loaded inventory part must resolve its own prefab inventory module");
                    if (prefabModule.packedVolumeLimit > 0.0) volumeLimitSum += prefabModule.packedVolumeLimit;
                    else volumeUnlimited = true;
                    if (prefabModule.massLimit > 0.0) massLimitSum += prefabModule.massLimit;
                    else massUnlimited = true;
                    for (int s = 0; s < mod.InventorySlots; s++)
                    {
                        if (mod.storedParts == null || !mod.storedParts.ContainsKey(s)) continue;
                        StoredPart sp = mod.storedParts[s];
                        if (sp == null || sp.quantity <= 0) continue;
                        if (!TryReadPrefabFootprint(sp.partName, out double storedVol, out double storedMass, out _))
                            continue;
                        storedPartCount++;
                        volumeOccupied += storedVol * sp.quantity;
                        massOccupied += storedMass * sp.quantity;
                    }
                }
            }
            double volumeLimit = volumeUnlimited ? 0.0 : volumeLimitSum;
            double massLimit = massUnlimited ? 0.0 : massLimitSum;

            // A storable cargo prefab to probe with (smallest packed volume so
            // at least 2 units usually fit and the consumption leg can run).
            if (!TryFindSmallestStorableCargoPrefab(out string cargoPartName,
                    out double perUnitVolume, out double perUnitMass, out int prefabStackable))
                InGameAssert.Skip(
                    "PRECONDITION: PartLoader has no storable cargo prefab (ModuleCargoPart with packedVolume > 0)");

            var item = new InventoryPayloadItem
            {
                IdentityHash = "probe-admission-check",
                PartName = cargoPartName,
                Quantity = 1,
                SlotsTaken = 1,
            };
            var probe = new LiveDeliveryCapacityProbe(activeVessel, true);

            // 1. Admission for a huge request matches the independent budget
            //    run through the xUnit-pinned pure core.
            const int hugeRequest = 100000;
            int expected = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                perUnitVolume, perUnitMass,
                volumeLimit - volumeOccupied,
                massLimit - massOccupied,
                volumeLimit > 0.0, massLimit > 0.0,
                hugeRequest);
            int admitted = probe.ProbeInventoryUnitsThatFit(item, hugeRequest);
            InGameAssert.AreEqual(expected, admitted,
                $"Probe admission must match the independently computed budget: part={cargoPartName} " +
                $"perUnitVol={perUnitVolume.ToString("R", IC)} volLimit={volumeLimit.ToString("R", IC)} " +
                $"volOccupied={volumeOccupied.ToString("R", IC)} massLimit={massLimit.ToString("R", IC)} " +
                $"massOccupied={massOccupied.ToString("R", IC)} (expected={expected.ToString(IC)} admitted={admitted.ToString(IC)})");

            // 2. Admission never overshoots a real volume limit (Gap B: the
            //    pre-fix writer would append past it on the unloaded branch).
            if (volumeLimit > 0.0 && perUnitVolume > 0.0)
                InGameAssert.IsTrue(
                    volumeOccupied + perUnitVolume * admitted <= volumeLimit + 1e-6,
                    $"Admitted units must fit the container: {volumeOccupied.ToString("R", IC)} + " +
                    $"{perUnitVolume.ToString("R", IC)} * {admitted.ToString(IC)} > {volumeLimit.ToString("R", IC)}");

            // 3. Consuming capacity reduces the follow-up admission, matching
            //    the pure core over the same expression the probe uses.
            if (admitted >= 2)
            {
                probe.ConsumeInventoryCapacity(item, 1);
                int expectedAfterConsume = LiveDeliveryCapacityProbe.ComputeUnitsThatFit(
                    perUnitVolume, perUnitMass,
                    volumeLimit - volumeOccupied - perUnitVolume * 1,
                    massLimit - massOccupied - perUnitMass * 1,
                    volumeLimit > 0.0, massLimit > 0.0,
                    hugeRequest);
                int admittedAfterConsume = probe.ProbeInventoryUnitsThatFit(item, hugeRequest);
                InGameAssert.AreEqual(expectedAfterConsume, admittedAfterConsume,
                    $"ConsumeInventoryCapacity(1) must shrink the next admission " +
                    $"(before={admitted.ToString(IC)} after={admittedAfterConsume.ToString(IC)} expected={expectedAfterConsume.ToString(IC)})");
            }

            // 4. Unresolvable ITEM prefab fails closed.
            var unknownItem = new InventoryPayloadItem
            {
                IdentityHash = "probe-unknown-part",
                PartName = "parsekNonexistentCargoPart",
                Quantity = 1,
                SlotsTaken = 1,
            };
            InGameAssert.AreEqual(0, probe.ProbeInventoryUnitsThatFit(unknownItem, 5),
                "An unresolvable item prefab must admit 0 units (fail closed)");

            // 5. Stackable-quantity probe reads the prefab value.
            int probedStackable = probe.ProbeInventoryStackableQuantity(item);
            InGameAssert.AreEqual(prefabStackable > 0 ? prefabStackable : 1, probedStackable,
                $"ProbeInventoryStackableQuantity must read the prefab's ModuleCargoPart.stackableQuantity " +
                $"(prefab={prefabStackable.ToString(IC)} probed={probedStackable.ToString(IC)})");

            ParsekLog.Info("TestRunner",
                $"Delivery_ProbeAdmission: PASS vessel={activeVessel.vesselName} " +
                $"container={module.part.partInfo.name} storedParts={storedPartCount.ToString(IC)} " +
                $"probePart={cargoPartName} admitted={admitted.ToString(IC)}/{hugeRequest.ToString(IC)} " +
                $"volLimit={volumeLimit.ToString("R", IC)} volOccupied={volumeOccupied.ToString("R", IC)}");
            yield break;
        }

        /// <summary>
        /// Prefab footprint read used by the independent-budget side of the
        /// probe-admission test: per-unit packed volume + prefab mass
        /// (stock's capacity accounting) + stackableQuantity. False when the
        /// prefab is unresolvable or not storable cargo.
        /// </summary>
        private static bool TryReadPrefabFootprint(
            string partName, out double perUnitVolume, out double perUnitMass, out int stackableQuantity)
        {
            perUnitVolume = 0.0;
            perUnitMass = 0.0;
            stackableQuantity = 1;
            if (string.IsNullOrEmpty(partName)) return false;
            AvailablePart info = PartLoader.getPartInfoByName(partName);
            Part prefab = info != null ? info.partPrefab : null;
            ModuleCargoPart cargo = prefab != null ? prefab.FindModuleImplementing<ModuleCargoPart>() : null;
            if (cargo == null || cargo.packedVolume < 0f) return false;
            perUnitVolume = cargo.packedVolume;
            perUnitMass = prefab.mass + prefab.GetResourceMass();
            stackableQuantity = cargo.stackableQuantity;
            return true;
        }

        /// <summary>
        /// Finds the storable cargo prefab (ModuleCargoPart, packedVolume &gt; 0)
        /// with the SMALLEST packed volume in PartLoader, so the admission test
        /// usually fits at least 2 units and can exercise the consumption leg.
        /// </summary>
        private static bool TryFindSmallestStorableCargoPrefab(
            out string partName, out double perUnitVolume, out double perUnitMass, out int stackableQuantity)
        {
            partName = null;
            perUnitVolume = 0.0;
            perUnitMass = 0.0;
            stackableQuantity = 1;
            if (PartLoader.LoadedPartsList == null) return false;
            for (int i = 0; i < PartLoader.LoadedPartsList.Count; i++)
            {
                AvailablePart info = PartLoader.LoadedPartsList[i];
                Part prefab = info != null ? info.partPrefab : null;
                ModuleCargoPart cargo = prefab != null ? prefab.FindModuleImplementing<ModuleCargoPart>() : null;
                if (cargo == null || cargo.packedVolume <= 0f) continue;
                if (partName != null && cargo.packedVolume >= perUnitVolume) continue;
                partName = info.name;
                perUnitVolume = cargo.packedVolume;
                perUnitMass = prefab.mass + prefab.GetResourceMass();
                stackableQuantity = cargo.stackableQuantity;
            }
            return partName != null;
        }

        /// <summary>
        /// Finds any STACKABLE (stackCapacity &gt; 1) stored cargo part across
        /// the vessel's inventory modules and returns its canonical STOREDPART
        /// payload (<see cref="StoredPart.Save"/> shape — exactly what the
        /// recorder captures) plus the stack capacity.
        /// </summary>
        private static bool TryFindStackableStoredCargo(
            Vessel vessel, out ConfigNode payloadNode, out int stackCapacity)
        {
            payloadNode = null;
            stackCapacity = 0;
            if (vessel == null || vessel.parts == null) return false;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module) || module.storedParts == null)
                        continue;
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        if (!module.storedParts.ContainsKey(s)) continue;
                        StoredPart sp = module.storedParts[s];
                        if (sp == null || sp.snapshot == null || sp.stackCapacity <= 1) continue;
                        var node = new ConfigNode("STOREDPART");
                        sp.Save(node);
                        payloadNode = node;
                        stackCapacity = sp.stackCapacity;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>First empty slot of <paramref name="module"/>, or -1 (mirrors the probe's walk).</summary>
        private static int FirstEmptySlot(ModuleInventoryPart module)
        {
            for (int s = 0; s < module.InventorySlots; s++)
            {
                if (module.storedParts == null || !module.storedParts.ContainsKey(s))
                    return s;
            }
            return -1;
        }

        /// <summary>
        /// Module-qualified <see cref="InventorySlotAddress"/> for
        /// (<paramref name="module"/>, <paramref name="slot"/>) in the probe's
        /// coordinate space (vessel part index, then module index within the
        /// part), so a test that located a live module by reference can address
        /// the unified WriteInventory(item, address, units) contract.
        /// </summary>
        private static InventorySlotAddress ResolveModuleAddress(Vessel vessel, ModuleInventoryPart module, int slot)
        {
            int partIndex = vessel != null && vessel.parts != null ? vessel.parts.IndexOf(module.part) : -1;
            int moduleIndex = -1;
            if (module.part != null && module.part.Modules != null)
            {
                for (int m = 0; m < module.part.Modules.Count; m++)
                {
                    if (ReferenceEquals(module.part.Modules[m], module)) { moduleIndex = m; break; }
                }
            }
            return new InventorySlotAddress(partIndex, moduleIndex, slot);
        }

        /// <summary>
        /// Finds the first <see cref="Part"/> on <paramref name="vessel"/> that
        /// carries a <c>LiquidFuel</c> resource entry. Mirrors the order the
        /// delivery applier walks (vessel.parts ascending), so the test's
        /// pre-drain / restore + the applier's fill all land on the same tank.
        /// </summary>

        /// <summary>
        /// Finds the first <see cref="Part"/> on <paramref name="vessel"/> that
        /// carries a <c>LiquidFuel</c> resource entry. Mirrors the order the
        /// delivery applier walks (vessel.parts ascending), so the test's
        /// pre-drain / restore + the applier's fill all land on the same tank.
        /// </summary>
        private static bool TryFindLiquidFuelPart(Vessel vessel, out Part part, out PartResource resource)
        {
            part = null;
            resource = null;
            if (vessel == null || vessel.parts == null) return false;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                part = p;
                resource = pr;
                return true;
            }
            return false;
        }
    }
}
