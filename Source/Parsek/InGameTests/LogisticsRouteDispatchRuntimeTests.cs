using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Stock-runtime smoke check for the dispatch-scheduler tick body
    /// (item 5 of the logistics plan). Injects a synthetic KSC-origin
    /// <see cref="Route"/> into <see cref="RouteStore"/>, drives one
    /// <see cref="RouteOrchestrator.Tick(double)"/> against live KSP
    /// statics (<see cref="HighLogic"/>, <see cref="Funding"/>,
    /// <see cref="EffectiveState.ComputeERS"/>), and asserts that the
    /// orchestrator processed the route without throwing.
    ///
    /// xUnit covers the orchestrator with an injected
    /// <c>IRouteRuntimeEnvironment</c> fake; this test exercises the
    /// production no-env overload that constructs the live
    /// <c>LiveRouteRuntimeEnvironment</c>, which xUnit cannot reach
    /// because the env probes <see cref="HighLogic.CurrentGame"/> and
    /// <see cref="Funding.Instance"/>. A regression that NREs inside the
    /// env builder under real KSP statics (e.g. ERS query during early
    /// load, Funding null in Sandbox) would surface here.
    ///
    /// The synthetic route is removed in <c>finally</c> so the test
    /// leaves no residue in <see cref="RouteStore.CommittedRoutes"/> for
    /// the next batch test or the player's save. v0 limitations
    /// (non-KSC origin stub, capacity stub) make the route's terminal
    /// state mode-dependent — Sandbox + KSC origin should dispatch to
    /// <c>InTransit</c>; Career without funds will land in
    /// <c>WaitingForFunds</c>; missing source recording will land in
    /// <c>MissingSourceRecording</c>. Any of those terminal states is
    /// proof the tick ran end-to-end; the assertion accepts the union.
    /// </summary>
    public sealed class LogisticsRouteDispatchRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + emits ledger rows under live KSP statics; runs out of band so a parallel batch test cannot read a partially-mutated route list or stray dispatched/endpoint-lost ledger rows.",
            Description = "RouteOrchestrator.Tick processes a synthetic KSC-origin route under live KSP statics without throwing, and the route's state advances away from the pre-tick Active baseline")]
        public IEnumerator RouteOrchestrator_Tick_ProcessesSyntheticRoute()
        {
            // PRECONDITION CHECKS -------------------------------------------------
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; cannot build a LiveRouteRuntimeEnvironment");
            if (Planetarium.fetch == null)
                InGameAssert.Skip("Planetarium.fetch is null; cannot resolve current UT");

            string syntheticRouteId = "ingame-dispatch-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot ledger size BEFORE we touch anything so the assertion can
            // detect a brand-new row authored by this tick rather than reading a
            // pre-existing RouteDispatched/RouteEndpointLost left by gameplay.
            int beforeLedgerCount = Ledger.Actions?.Count ?? 0;

            // Build the synthetic route inline. RouteFixtureBuilder lives in
            // Source/Parsek.Tests/Generators/ and is not visible from the
            // production assembly, so we author the route here directly with
            // the same fields the builder would set.
            //
            // NextDispatchUT = 0.0 is always due (any positive currentUT >= 0).
            // KSC origin avoids the v0 non-KSC stub. Status = Active is the
            // pre-dispatch baseline. One Stop with a non-zero endpoint
            // VesselPersistentId so the destination resolver does not reject
            // the route outright.
            uint destinationPid = FlightGlobals.ActiveVessel != null
                ? FlightGlobals.ActiveVessel.persistentId
                : 1u;

            var synthetic = new Route
            {
                Id = syntheticRouteId,
                Name = "Parsek Dispatch Smoke Test",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                TransitDuration = 60.0,
                DispatchInterval = 300.0,
                NextDispatchUT = 0.0,
                CurrentSegmentIndex = -1,
                PendingStopIndex = -1,
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = destinationPid,
                            BodyName = "Kerbin",
                            IsSurface = false,
                        },
                        ConnectionKind = RouteConnectionKind.DockingPort,
                    },
                },
            };

            RouteStore.AddRoute(synthetic);
            bool addedToStore = true;

            try
            {
                // Confirm AddRoute landed before driving the tick.
                Route fromStore;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(syntheticRouteId, out fromStore),
                    $"AddRoute did not surface synthetic route id={syntheticRouteId} in CommittedRoutes");
                InGameAssert.AreEqual(RouteStatus.Active, fromStore.Status,
                    "Pre-tick: synthetic route should be Active");

                ParsekLog.Verbose("TestRunner",
                    $"RouteOrchestrator_Tick: pre-tick routeId={syntheticRouteId} " +
                    $"status={fromStore.Status} nextDispatchUT={fromStore.NextDispatchUT.ToString("R", IC)} " +
                    $"beforeLedgerCount={beforeLedgerCount.ToString(IC)}");

                // ACT — production no-env overload. Builds a fresh
                // LiveRouteRuntimeEnvironment internally; the env probes
                // HighLogic / Funding / EffectiveState.ComputeERS for real.
                double currentUT = Planetarium.GetUniversalTime();
                RouteOrchestrator.Tick(currentUT);

                // Yield one frame in case any deferred wiring (ledger flush,
                // VerboseRateLimited buffer) settles on the next FixedUpdate.
                yield return null;

                // ASSERT — the tick ran end-to-end. Acceptance criteria are
                // intentionally broad because the v0 evaluator's exact outcome
                // depends on game mode + ERS state at runtime:
                //
                //   * Sandbox / Science + KSC origin + source recordings absent
                //     → RouteHasValidSourcesInErs() returns false → the
                //       evaluator transitions the route to
                //       MissingSourceRecording (no ledger row written).
                //   * Career + no funds → WaitingForFunds (no ledger row).
                //   * Career + funds + source ok → InTransit (RouteDispatched
                //       + RouteCargoDebited rows written).
                //   * Endpoint vessel resolver fails → EndpointLost
                //       (RouteEndpointLost row written).
                //
                // All of these are valid evidence that the orchestrator
                // walked the dispatch chain on the synthetic route. The
                // failure mode this test is designed to catch is the env
                // builder NRE-ing under real KSP statics, leaving the
                // route's Status untouched at Active.
                Route postTick;
                InGameAssert.IsTrue(
                    RouteStore.TryGetRoute(syntheticRouteId, out postTick),
                    "Synthetic route disappeared from store during Tick");
                InGameAssert.IsNotNull(postTick,
                    "TryGetRoute returned true but post-tick route was null");

                int afterLedgerCount = Ledger.Actions?.Count ?? 0;
                int newRows = afterLedgerCount - beforeLedgerCount;

                ParsekLog.Info("TestRunner",
                    $"RouteOrchestrator_Tick: post-tick routeId={syntheticRouteId} " +
                    $"status={postTick.Status} " +
                    $"nextDispatchUT={postTick.NextDispatchUT.ToString("R", IC)} " +
                    $"pendingDeliveryUT={(postTick.PendingDeliveryUT.HasValue ? postTick.PendingDeliveryUT.Value.ToString("R", IC) : "<null>")} " +
                    $"newLedgerRows={newRows.ToString(IC)}");

                // Either the route transitioned status OR the ledger gained a
                // new row attributed to this route id. Both are sufficient
                // evidence the tick processed the synthetic route.
                bool statusChanged = postTick.Status != RouteStatus.Active;
                bool ledgerRowForRoute = false;
                if (afterLedgerCount > beforeLedgerCount && Ledger.Actions != null)
                {
                    for (int i = beforeLedgerCount; i < afterLedgerCount; i++)
                    {
                        GameAction action = Ledger.Actions[i];
                        if (action != null
                            && string.Equals(action.RouteId, syntheticRouteId, StringComparison.Ordinal))
                        {
                            ledgerRowForRoute = true;
                            break;
                        }
                    }
                }

                InGameAssert.IsTrue(
                    statusChanged || ledgerRowForRoute,
                    "RouteOrchestrator.Tick neither transitioned the synthetic route's Status " +
                    "nor emitted a ledger row attributed to its id — the tick body did not " +
                    "process the route. Check LiveRouteRuntimeEnvironment build path under live KSP statics.");
            }
            finally
            {
                // TEARDOWN: remove the synthetic route so the player's save and
                // the next batch test do not see it. Wrapping in finally
                // guarantees cleanup even if an assert above threw.
                if (addedToStore)
                {
                    bool removed = RouteStore.RemoveRoute(syntheticRouteId);
                    ParsekLog.Verbose("TestRunner",
                        $"RouteOrchestrator_Tick cleanup: RemoveRoute(synthetic)={removed}");
                }
            }
        }
    }
}
