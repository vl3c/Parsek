using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
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
        public void PickupDebit_LoadedEndpointVessel_RemovesManifestAmount()
        {
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
        }

        // ==================================================================
        // 2. Unloaded-endpoint pickup-debit writes the proto snapshot
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyPickupDebit re-aimed at an UNLOADED on-rails vessel as the resolved pickup endpoint drains ProtoPartResourceSnapshot.amount through the production unloaded writer; the outcome carries the actuals + endpoint pid. Sources an existing unloaded vessel from the save (no spawn) and skips with a named reason when none exists")]
        public void PickupDebit_UnloadedEndpointVessel_WritesProtoSnapshot()
        {
            if (!TryFindUnloadedLiquidFuelVessel(out Vessel endpointVessel, out double storedBefore))
                InGameAssert.Skip(
                    "PRECONDITION: no unloaded non-ghost vessel with >= " +
                    $"{MinMeaningfulPickup.ToString("R", IC)} debitable LiquidFuel in this save. " +
                    "The unloaded-endpoint fixture sources an EXISTING on-rails vessel (plan finding 6: " +
                    "spawn-based fixtures are unproven ground); load a save with a distant fuel-carrying " +
                    "vessel (e.g. an orbiting depot) to run this test");
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

        // ==================================================================
        // 3. Short endpoint clamps to stored + records requested-on-shortfall
        // ==================================================================

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "ApplyPickupDebit against a resolved endpoint whose stored LiquidFuel is below the pickup manifest clamps the removal to what is stored, marks the outcome short, and records the full requested amount on RequestedOnShortfall (clamp-and-warn, design D3)")]
        public void PickupDebit_ShortEndpoint_ClampsAndRecordsRequested()
        {
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
        }

        // ==================================================================
        // Helpers
        // ==================================================================

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

        /// <summary>
        /// Finds an existing UNLOADED, non-ghost, non-active vessel holding at
        /// least <see cref="MinMeaningfulPickup"/> debitable LiquidFuel per the
        /// production <see cref="LiveOriginCargoProbe"/> (unloaded branch).
        /// Mirror of the M1 origin-debit suite's fixture (plan finding 6:
        /// spawn-based fixtures are unproven ground).
        /// </summary>
        private static bool TryFindUnloadedLiquidFuelVessel(out Vessel candidate, out double stored)
        {
            candidate = null;
            stored = 0.0;
            List<Vessel> vessels = FlightGlobals.Vessels;
            if (vessels == null) return false;
            HashSet<uint> ghostPids = GhostMapPresence.ghostMapVesselPids;
            Vessel active = FlightGlobals.ActiveVessel;

            for (int i = 0; i < vessels.Count; i++)
            {
                Vessel v = vessels[i];
                if (v == null || v.loaded) continue;
                if (active != null && ReferenceEquals(v, active)) continue;
                if (ghostPids != null && ghostPids.Contains(v.persistentId)) continue;
                if (v.protoVessel == null || v.protoVessel.protoPartSnapshots == null) continue;

                double s = new LiveOriginCargoProbe(v, false).ProbeResourceStored(LiquidFuelName);
                if (s < MinMeaningfulPickup) continue;

                candidate = v;
                stored = s;
                return true;
            }
            return false;
        }
    }
}
