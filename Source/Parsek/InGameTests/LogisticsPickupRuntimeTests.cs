using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Parsek.InGameTests.Helpers;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// M3 Phase 3 in-game coverage for the REVERSE-direction resource removal
    /// re-aimed at a per-window pickup ENDPOINT vessel
    /// (<see cref="RouteOrchestrator.ApplyPickupDebit"/>, design D5). xUnit
    /// (<c>RoutePickupDebitTests</c>, <c>RouteOriginDebitPlannerTests</c>)
    /// covers the manifest-agnostic planner, the test seam, the empty-manifest
    /// no-op, and the unresolved branch; these tests pin the part only live KSP
    /// can exercise: the LIVE endpoint-vessel mutation through the production
    /// probe + writer bundle (loaded <c>PartResource.amount</c> AND unloaded
    /// <c>ProtoPartResourceSnapshot.amount</c> removal), and the
    /// <see cref="RouteOrchestrator.OriginDebitOutcome"/> the path returns for a
    /// RESOLVED endpoint vessel (actual debited == removed, endpoint pid, not
    /// short).
    ///
    /// <para><b>Loop-path-only in spirit (M1 D11 parity).</b> Phase 3 does NOT
    /// wire the path into <c>EmitLoopCycle</c> (Phase 4) and emits no ledger
    /// row, so these tests call <see cref="RouteOrchestrator.ApplyPickupDebit"/>
    /// DIRECTLY against a live <see cref="LiveRouteRuntimeEnvironment"/> (same
    /// resolver the orchestrator uses) - no route enters <see cref="RouteStore"/>
    /// and no seam is armed, so there is zero background-tick exposure and no
    /// store mutation. Every mutated tank amount is restored in
    /// <c>finally</c>.</para>
    ///
    /// <para><b>Unloaded-endpoint fixture (mirrors the M1 origin-debit suite,
    /// plan finding 6).</b> The unloaded test sources an EXISTING on-rails
    /// vessel from the current save (non-ghost, unloaded, holding debitable
    /// LiquidFuel per the production <see cref="LiveOriginCargoProbe"/>) rather
    /// than spawning a distant fixture, and precondition-skips with a named
    /// reason when the save has none.</para>
    /// </summary>
    public sealed class LogisticsPickupRuntimeTests
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DefaultPickupAmount = 5.0;
        private const double MinMeaningfulPickup = 0.1;
        private const double ResourceTolerance = 0.01;

        // Auto-spawn fixture floors (shared shape with the M1 origin-debit suite):
        // enough stored LiquidFuel to pick up the default amount with margin.
        private const double FixtureMinStoredLf = DefaultPickupAmount + 5.0;
        private const double FixtureMinFreeCapacity = 1.0;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - mutates live vessel resource state under live KSP statics; " +
            "excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play " +
            "button in a disposable FLIGHT session.";

        // ==================================================================
        // 1. Loaded-endpoint pickup-debit removes the witnessed amount
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyPickupDebit re-aimed at the LOADED active vessel as the resolved pickup endpoint physically removes the pickup-manifest LiquidFuel through the production probe + writer, returns an outcome carrying the actual-debited manifest + endpoint pid (not short), and logs the PickupDebit plan + Origin debit lines on path=loaded")]
        public IEnumerator PickupDebit_LoadedEndpointVessel_RemovesManifestAmount()
        {
            // Post-restore unpack wait (yields BEFORE any state read/mutation): the
            // isolated-batch baseline restore reloads FLIGHT and leaves the active
            // vessel packed for a few frames; without this the synchronous
            // loaded+unpacked precondition skipped every run (3x observed 2026-06-15).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel to debit");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={endpointVessel.loaded}, packed={endpointVessel.packed}); the pickup debit " +
                    "would take the unloaded proto path which does not mutate the live PartResource this test reads");

            double storedBefore = new LiveOriginCargoProbe(endpointVessel, true)
                .ProbeResourceStored(LiquidFuelName);
            if (storedBefore < MinMeaningfulPickup)
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' stores only " +
                    $"{storedBefore.ToString("R", IC)} debitable LiquidFuel " +
                    $"(< {MinMeaningfulPickup.ToString("R", IC)}); pick a vessel with fuel aboard to run this test");
            double pickupAmount = Math.Min(DefaultPickupAmount, storedBefore);

            List<KeyValuePair<PartResource, double>> tankSnapshot = SnapshotLoadedLiquidFuel(endpointVessel);

            try
            {
                RouteEndpoint endpoint = EndpointForVessel(endpointVessel);
                var pickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, pickupAmount },
                };

                RouteOrchestrator.OriginDebitOutcome outcome = RouteOrchestrator.ApplyPickupDebit(
                    endpoint, pickupManifest, new LiveRouteRuntimeEnvironment(), "ingame-pickup-loaded");

                // 1. Outcome shape: resolved, not short, actual == requested,
                //    endpoint pid carried.
                InGameAssert.IsFalse(outcome.Unresolved, "Endpoint should resolve to the active vessel");
                InGameAssert.IsFalse(outcome.Short, "Full pickup must not be short");
                InGameAssert.IsNull(outcome.RequestedOnShortfall, "Full pickup records no requested-on-shortfall manifest");
                InGameAssert.AreEqual(endpointVessel.persistentId, outcome.OriginVesselPid,
                    "Outcome must carry the resolved endpoint pid");
                InGameAssert.IsNotNull(outcome.ActualDebited, "Outcome must carry the actual-debited manifest");
                InGameAssert.IsTrue(outcome.ActualDebited.ContainsKey(LiquidFuelName),
                    "Actual-debited manifest must contain LiquidFuel");
                InGameAssert.ApproxEqual(pickupAmount, outcome.ActualDebited[LiquidFuelName], ResourceTolerance,
                    "Actual-debited must equal the witnessed pickup amount");

                // 2. Live resource pool dropped by exactly the pickup amount.
                double storedAfter = new LiveOriginCargoProbe(endpointVessel, true)
                    .ProbeResourceStored(LiquidFuelName);
                InGameAssert.ApproxEqual(storedBefore - pickupAmount, storedAfter, ResourceTolerance,
                    $"Endpoint LiquidFuel pool should drop by {pickupAmount.ToString("R", IC)} " +
                    $"(before={storedBefore.ToString("R", IC)} after={storedAfter.ToString("R", IC)})");

                ParsekLog.Info("TestRunner",
                    $"PickupDebit_Loaded: PASS endpoint={endpointVessel.vesselName} " +
                    $"pid={endpointVessel.persistentId.ToString(IC)} " +
                    $"storedBefore={storedBefore.ToString("R", IC)} storedAfter={storedAfter.ToString("R", IC)} " +
                    $"pickup={pickupAmount.ToString("R", IC)}");
            }
            finally
            {
                RestoreLoadedLiquidFuel(tankSnapshot);
            }
            yield break;
        }

        // ==================================================================
        // 2. Unloaded-endpoint pickup-debit writes the proto snapshot
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyPickupDebit re-aimed at an UNLOADED on-rails vessel as the resolved pickup endpoint drains ProtoPartResourceSnapshot.amount through the production unloaded writer; the outcome carries the actuals + endpoint pid. Auto-spawns an unloaded LiquidFuel vessel (fresh-identity pad-rocket copy in a parking orbit) when the save lacks one, reuses an existing one when present, and skips only when neither can be provided")]
        public IEnumerator PickupDebit_UnloadedEndpointVessel_WritesProtoSnapshot()
        {
            // Post-restore unpack wait (the fixture snapshots the active vessel,
            // so it must be loaded+unpacked first).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // Auto-spawn (or reuse) an unloaded LiquidFuel vessel so the player
            // only needs a fueled pad rocket; null fixture => fall back to Skip.
            var fixture = new UnloadedFuelVesselFixture.EnsureResult();
            IEnumerator ensure = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                FixtureMinStoredLf, FixtureMinFreeCapacity, fixture);
            while (ensure.MoveNext())
                yield return ensure.Current;

            try
            {
                if (fixture.Vessel == null)
                    InGameAssert.Skip(
                        "PRECONDITION: could not provide an unloaded non-ghost vessel with >= " +
                        $"{FixtureMinStoredLf.ToString("R", IC)} debitable LiquidFuel - no suitable pre-existing " +
                        "on-rails vessel AND the auto-spawn from the active pad rocket did not settle unloaded " +
                        "(see the TestHelper log lines). Provide a fueled PRELAUNCH pad rocket to run this test");
                Vessel endpointVessel = fixture.Vessel;
                double storedBefore = fixture.StoredLiquidFuel;
                double pickupAmount = Math.Min(DefaultPickupAmount, storedBefore);

                List<KeyValuePair<ProtoPartResourceSnapshot, double>> protoSnapshot =
                    SnapshotProtoLiquidFuel(endpointVessel);

                try
                {
                    RouteEndpoint endpoint = EndpointForVessel(endpointVessel);
                    var pickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                    {
                        { LiquidFuelName, pickupAmount },
                    };

                    RouteOrchestrator.OriginDebitOutcome outcome = RouteOrchestrator.ApplyPickupDebit(
                        endpoint, pickupManifest, new LiveRouteRuntimeEnvironment(), "ingame-pickup-unloaded");

                    InGameAssert.IsFalse(outcome.Unresolved, "Unloaded endpoint should resolve via the pid lookup");
                    InGameAssert.IsFalse(outcome.Short, "Full unloaded pickup must not be short");
                    InGameAssert.AreEqual(endpointVessel.persistentId, outcome.OriginVesselPid,
                        "Outcome must carry the unloaded endpoint pid");
                    InGameAssert.IsNotNull(outcome.ActualDebited, "Outcome must carry the actual-debited manifest");
                    InGameAssert.ApproxEqual(pickupAmount, outcome.ActualDebited[LiquidFuelName], ResourceTolerance,
                        "Actual-debited must equal the witnessed pickup amount");

                    double storedAfter = new LiveOriginCargoProbe(endpointVessel, false)
                        .ProbeResourceStored(LiquidFuelName);
                    InGameAssert.ApproxEqual(storedBefore - pickupAmount, storedAfter, ResourceTolerance,
                        $"Unloaded endpoint LiquidFuel pool should drop by {pickupAmount.ToString("R", IC)} " +
                        $"(before={storedBefore.ToString("R", IC)} after={storedAfter.ToString("R", IC)})");

                    ParsekLog.Info("TestRunner",
                        $"PickupDebit_Unloaded: PASS endpoint={endpointVessel.vesselName} " +
                        $"pid={endpointVessel.persistentId.ToString(IC)} " +
                        $"storedBefore={storedBefore.ToString("R", IC)} storedAfter={storedAfter.ToString("R", IC)} " +
                        $"pickup={pickupAmount.ToString("R", IC)}");
                }
                finally
                {
                    RestoreProtoLiquidFuel(protoSnapshot);
                }
            }
            finally
            {
                // Remove the auto-spawned fixture vessel (no-op for a reused one).
                UnloadedFuelVesselFixture.Cleanup(fixture);
            }
        }

        // ==================================================================
        // 3. Short endpoint clamps to stored + records requested-on-shortfall
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyPickupDebit against a resolved endpoint whose stored LiquidFuel is below the pickup manifest clamps the removal to what is stored, marks the outcome short, and records the full requested amount on RequestedOnShortfall (clamp-and-warn, design D3)")]
        public IEnumerator PickupDebit_ShortEndpoint_ClampsAndRecordsRequested()
        {
            // Post-restore unpack wait (see PickupDebit_LoadedEndpointVessel).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel to debit");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked; this test reads live PartResource amounts");

            double storedBefore = new LiveOriginCargoProbe(endpointVessel, true)
                .ProbeResourceStored(LiquidFuelName);
            if (storedBefore < MinMeaningfulPickup)
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' stores only {storedBefore.ToString("R", IC)} debitable LiquidFuel; " +
                    "pick a vessel with fuel aboard to run this test");

            // Request MORE than stored so the clamp + short path fires.
            double requestedAmount = storedBefore + 1000.0;
            List<KeyValuePair<PartResource, double>> tankSnapshot = SnapshotLoadedLiquidFuel(endpointVessel);

            try
            {
                RouteEndpoint endpoint = EndpointForVessel(endpointVessel);
                var pickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, requestedAmount },
                };

                RouteOrchestrator.OriginDebitOutcome outcome = RouteOrchestrator.ApplyPickupDebit(
                    endpoint, pickupManifest, new LiveRouteRuntimeEnvironment(), "ingame-pickup-short");

                InGameAssert.IsFalse(outcome.Unresolved, "Short endpoint still resolves (it has SOME fuel)");
                InGameAssert.IsTrue(outcome.Short, "Requesting more than stored must mark the outcome short");
                InGameAssert.IsNotNull(outcome.RequestedOnShortfall, "Short outcome must record the requested manifest");
                InGameAssert.ApproxEqual(requestedAmount, outcome.RequestedOnShortfall[LiquidFuelName], ResourceTolerance,
                    "Requested-on-shortfall must carry the full requested amount");
                InGameAssert.IsNotNull(outcome.ActualDebited, "Some fuel was removed so an actual manifest exists");
                InGameAssert.ApproxEqual(storedBefore, outcome.ActualDebited[LiquidFuelName], ResourceTolerance,
                    "Actual-debited clamps to what was stored");

                double storedAfter = new LiveOriginCargoProbe(endpointVessel, true)
                    .ProbeResourceStored(LiquidFuelName);
                InGameAssert.ApproxEqual(0.0, storedAfter, ResourceTolerance,
                    "A short clamp drains the endpoint pool to (near) zero, never below");

                ParsekLog.Info("TestRunner",
                    $"PickupDebit_Short: PASS endpoint={endpointVessel.vesselName} " +
                    $"storedBefore={storedBefore.ToString("R", IC)} requested={requestedAmount.ToString("R", IC)} " +
                    $"actual={outcome.ActualDebited[LiquidFuelName].ToString("R", IC)}");
            }
            finally
            {
                RestoreLoadedLiquidFuel(tankSnapshot);
            }
            yield break;
        }

        // ==================================================================
        // 4. Loaded-endpoint INVENTORY pickup removes the witnessed stored part
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyInventoryPickupDebit re-aimed at the LOADED active vessel removes a witnessed stored-part payload by IdentityHash via the production LiveInventoryPickupWriter (stock ClearPartAtSlot), returns an outcome carrying the actual-picked-up inventory + endpoint pid (not short), and restores the cargo in finally")]
        public IEnumerator InventoryPickupDebit_LoadedEndpointVessel_RemovesStoredPart()
        {
            // Post-restore unpack wait (see PickupDebit_LoadedEndpointVessel).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel to debit");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked; this test uses the loaded ClearPartAtSlot path");

            // Find a stored cargo item on the active vessel (the pickup SOURCE).
            if (!TryFindStoredCargoItem(endpointVessel, out InventoryPayloadItem witnessed,
                    out ModuleInventoryPart sourceModule, out int sourceSlot, out ProtoPartSnapshot sourceSnapshot))
                InGameAssert.Skip(
                    "PRECONDITION: no stored cargo part on the active vessel. Store a cargo part in an " +
                    "inventory container (EVA construction or VAB) to run this test");

            int storedBefore = new LiveInventoryPickupWriter(endpointVessel, true).CountStored(witnessed.IdentityHash);
            InGameAssert.IsTrue(storedBefore >= 1, "Source must hold at least one of the witnessed identity before pickup");

            bool removed = false;
            try
            {
                RouteEndpoint endpoint = EndpointForVessel(endpointVessel);
                var manifest = new List<InventoryPayloadItem> { witnessed };

                RouteOrchestrator.InventoryPickupOutcome outcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                    endpoint, manifest, new LiveRouteRuntimeEnvironment(), "ingame-inv-pickup-loaded");

                InGameAssert.IsFalse(outcome.Unresolved, "Endpoint should resolve to the active vessel");
                InGameAssert.IsFalse(outcome.Short, "A held witnessed item must not be short");
                InGameAssert.AreEqual(endpointVessel.persistentId, outcome.EndpointVesselPid,
                    "Outcome must carry the resolved endpoint pid");
                InGameAssert.IsNotNull(outcome.ActualPickedUp, "Outcome must carry the actual-picked-up inventory");
                InGameAssert.AreEqual(1, outcome.ActualPickedUp.Count, "Exactly one identity picked up");
                InGameAssert.AreEqual(witnessed.IdentityHash, outcome.ActualPickedUp[0].IdentityHash,
                    "Picked-up identity must match the witnessed one");
                removed = true;

                int storedAfter = new LiveInventoryPickupWriter(endpointVessel, true).CountStored(witnessed.IdentityHash);
                InGameAssert.AreEqual(storedBefore - 1, storedAfter,
                    $"Source stored count for the identity should drop by 1 (before={storedBefore} after={storedAfter})");

                ParsekLog.Info("TestRunner",
                    $"InventoryPickupDebit_Loaded: PASS endpoint={endpointVessel.vesselName} " +
                    $"part={witnessed.PartName} storedBefore={storedBefore} storedAfter={storedAfter}");
            }
            finally
            {
                // Restore the removed cargo into its source slot (mirror the
                // live-move test's restore).
                if (removed && sourceModule != null && sourceSnapshot != null)
                {
                    try { sourceModule.StoreCargoPartAtSlot(sourceSnapshot, sourceSlot); }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("TestRunner",
                            $"InventoryPickupDebit_Loaded: restore threw {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            yield break;
        }

        // ==================================================================
        // 5. Endpoint with no matching inventory clamps short (honest book)
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyInventoryPickupDebit for an identity the resolved endpoint does NOT hold removes nothing, marks the outcome short, and records the full witnessed quantity on RequestedOnShortfall (clamp-and-warn, design D7) - no mutation, so no restore needed")]
        public IEnumerator InventoryPickupDebit_IdentityNotHeld_ClampsShortNoMutation()
        {
            // Post-restore unpack wait (see PickupDebit_LoadedEndpointVessel).
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live endpoint vessel");
            Vessel endpointVessel = FlightGlobals.ActiveVessel;
            if (!(endpointVessel.loaded && !endpointVessel.packed))
                InGameAssert.Skip($"Active vessel '{endpointVessel.vesselName}' is not loaded+unpacked");

            // A synthetic payload with a hash the vessel cannot possibly hold.
            var phantom = new InventoryPayloadItem
            {
                IdentityHash = "phantom-identity-hash-that-no-stored-part-matches",
                PartName = "smallCargoContainer",
                Quantity = 1,
                SlotsTaken = 1,
            };

            RouteEndpoint endpoint = EndpointForVessel(endpointVessel);
            RouteOrchestrator.InventoryPickupOutcome outcome = RouteOrchestrator.ApplyInventoryPickupDebit(
                endpoint, new List<InventoryPayloadItem> { phantom }, new LiveRouteRuntimeEnvironment(),
                "ingame-inv-pickup-notheld");

            InGameAssert.IsFalse(outcome.Unresolved, "The endpoint resolves; only the identity is absent");
            InGameAssert.IsTrue(outcome.Short, "An identity the source does not hold must mark the outcome short");
            InGameAssert.IsNull(outcome.ActualPickedUp, "Nothing removed -> no actual-picked-up manifest");
            InGameAssert.IsNotNull(outcome.RequestedOnShortfall, "Short outcome records the requested manifest");
            InGameAssert.AreEqual(1, outcome.RequestedOnShortfall.Count, "One witnessed identity requested");

            ParsekLog.Info("TestRunner",
                $"InventoryPickupDebit_NotHeld: PASS endpoint={endpointVessel.vesselName} (no mutation, clamp short)");
            yield break;
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Finds the FIRST quantity-1 stored cargo item on <paramref name="vessel"/>
        /// and the witnessed payload (identity hash + canonical STOREDPART) the
        /// production pickup writer would match against, plus the source module /
        /// slot / proto snapshot so the test can restore it. Mirror of the
        /// live-move test's CollectStoredParts walk, reading the public
        /// <see cref="StoredPart"/> fields.
        /// </summary>
        private static bool TryFindStoredCargoItem(
            Vessel vessel,
            out InventoryPayloadItem witnessed,
            out ModuleInventoryPart sourceModule,
            out int sourceSlot,
            out ProtoPartSnapshot sourceSnapshot)
        {
            witnessed = null;
            sourceModule = null;
            sourceSlot = -1;
            sourceSnapshot = null;
            if (vessel?.parts == null) return false;

            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p?.Modules == null) continue;
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleInventoryPart module) || module.storedParts == null)
                        continue;
                    for (int s = 0; s < module.InventorySlots; s++)
                    {
                        if (!module.storedParts.ContainsKey(s)) continue;
                        StoredPart sp = module.storedParts[s];
                        if (sp == null || sp.snapshot == null || sp.quantity != 1) continue;

                        // Build the witnessed payload exactly as the recorder
                        // would (StoredPart.Save -> canonical hash).
                        var node = new ConfigNode("STOREDPART");
                        sp.Save(node);
                        string hash = VesselSpawner.ComputeInventoryPayloadIdentityHash(node);
                        if (string.IsNullOrEmpty(hash)) continue;

                        witnessed = new InventoryPayloadItem
                        {
                            IdentityHash = hash,
                            PartName = sp.partName,
                            VariantName = sp.variantName,
                            Quantity = 1,
                            SlotsTaken = 1,
                            StoredPartSnapshot = node,
                        };
                        sourceModule = module;
                        sourceSlot = s;
                        sourceSnapshot = sp.snapshot;
                        return true;
                    }
                }
            }
            return false;
        }

        private static RouteEndpoint EndpointForVessel(Vessel v)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = v != null ? v.persistentId : 0u,
                BodyName = v != null && v.mainBody != null ? v.mainBody.bodyName : "Kerbin",
                IsSurface = false,
            };
        }

        private static List<KeyValuePair<PartResource, double>> SnapshotLoadedLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<PartResource, double>>();
            if (vessel == null || vessel.parts == null) return snapshot;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                snapshot.Add(new KeyValuePair<PartResource, double>(pr, pr.amount));
            }
            return snapshot;
        }

        private static void RestoreLoadedLiquidFuel(List<KeyValuePair<PartResource, double>> snapshot)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }

        private static List<KeyValuePair<ProtoPartResourceSnapshot, double>> SnapshotProtoLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<ProtoPartResourceSnapshot, double>>();
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return snapshot;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    snapshot.Add(new KeyValuePair<ProtoPartResourceSnapshot, double>(prs, prs.amount));
                }
            }
            return snapshot;
        }

        private static void RestoreProtoLiquidFuel(List<KeyValuePair<ProtoPartResourceSnapshot, double>> snapshot)
        {
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }
    }
}
