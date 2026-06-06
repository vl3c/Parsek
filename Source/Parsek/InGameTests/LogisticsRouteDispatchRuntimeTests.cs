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
    /// the next batch test or the player's save.
    ///
    /// The synthetic route carries an EMPTY <c>SourceRefs</c> list (it is a
    /// throwaway with no backing recording in ERS). The legacy dispatch
    /// path therefore evaluates
    /// <c>LiveRouteRuntimeEnvironment.RouteHasValidSourcesInErs</c> to false,
    /// the evaluator returns the <c>SourcesStale</c> failure kind, and the
    /// orchestrator's
    /// <see cref="RouteOrchestrator.Tick(double)"/> resolves it to a benign
    /// <c>Skip("sources-stale")</c>, a deliberate no-op that does NOT mutate
    /// the route's <c>Status</c> and writes NO ledger row. (Only
    /// <c>RouteStore.RevalidateSources</c> ever flips a route to
    /// <c>MissingSourceRecording</c>, and Tick never calls it; that status is
    /// not reachable from this test's tick path.) A sourceless route in a
    /// live Sandbox save thus correctly stays Active after the tick, so this
    /// test does NOT assert a status transition or a ledger row; those are
    /// unsatisfiable here and asserting them would be a false negative.
    ///
    /// What this test DOES guard is that the production no-env overload
    /// constructs a live <c>LiveRouteRuntimeEnvironment</c> and runs
    /// <see cref="RouteOrchestrator.Tick(double)"/> to completion under real
    /// KSP statics without an unhandled exception escaping the tick: a throw
    /// from the env construction or the pre-dispatch route enumeration would
    /// surface from the coroutine body and the runner would record FAILED.
    /// NOTE the scope limit: the env's expensive probes
    /// (<c>EffectiveState.ComputeERS</c> via <c>EnsureErsBuilt</c>,
    /// <see cref="Funding.Instance"/>) are LAZY and run inside
    /// <c>ProcessOneRoute</c>, which <c>Tick</c> wraps in a per-route
    /// try/catch (counted as <c>errored++</c>, the route left untouched), so a
    /// probe NRE there is swallowed and leaves the route Active with no ledger
    /// row -- this test cannot distinguish that from the benign Skip. The
    /// post-tick asserts confirm the route survived the tick intact (still
    /// present, still Active, not nulled-out) and that the benign Skip left the
    /// ledger unchanged for this route id.
    /// </summary>
    public sealed class LogisticsRouteDispatchRuntimeTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            BatchSkipReason = "Mutates RouteStore + emits ledger rows under live KSP statics; runs out of band so a parallel batch test cannot read a partially-mutated route list or stray dispatched/endpoint-lost ledger rows.",
            Description = "RouteOrchestrator.Tick iterates a synthetic sourceless KSC-origin route under live KSP statics without throwing (guards the LiveRouteRuntimeEnvironment build path); the route survives the tick intact (still present, still Active) and the benign sources-stale Skip leaves the ledger unchanged for its id")]
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

                // ACT: production no-env overload. Builds a fresh
                // LiveRouteRuntimeEnvironment internally; the env probes
                // HighLogic / Funding / EffectiveState.ComputeERS for real.
                double currentUT = Planetarium.GetUniversalTime();
                RouteOrchestrator.Tick(currentUT);

                // Yield one frame in case any deferred wiring (ledger flush,
                // VerboseRateLimited buffer) settles on the next FixedUpdate.
                yield return null;

                // ASSERT: the tick ran to completion WITHOUT an unhandled throw.
                //
                // The regression this test guards is RouteOrchestrator.Tick(double)
                // failing under real KSP statics. Reaching these asserts at all
                // proves the no-env overload constructed a LiveRouteRuntimeEnvironment
                // and the tick returned without an exception escaping it. SCOPE: the
                // env's expensive probes (EffectiveState.ComputeERS via EnsureErsBuilt,
                // Funding.Instance) are LAZY -- they run inside ProcessOneRoute, which
                // Tick wraps in a per-route try/catch (counted as errored++, route left
                // untouched). So a probe NRE there is swallowed and leaves the route
                // Active with no ledger row, which this test cannot distinguish from the
                // benign skip below; the escaping-throw guarantee covers env construction
                // and the pre-dispatch route enumeration, not the lazy in-route probes.
                //
                // The synthetic route's SourceRefs list is EMPTY, so
                // RouteHasValidSourcesInErs() is false, the evaluator yields
                // the SourcesStale kind, and the orchestrator resolves it to a
                // benign Skip("sources-stale"). That Skip is a deliberate no-op:
                // it does NOT mutate Status (the route correctly stays Active)
                // and writes NO ledger row. We therefore do NOT assert a status
                // transition or a ledger row; those are unsatisfiable for a
                // sourceless route in a live Sandbox save, and asserting them
                // would be a false negative. (MissingSourceRecording is written
                // ONLY by RouteStore.RevalidateSources, which Tick never calls.)
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

                // The benign sources-stale Skip leaves the route exactly as it
                // was: present, non-null, and still Active. A regression that
                // silently mutates or drops the route on the Skip path (or any
                // future change that lets a sourceless route fall through to
                // dispatch/wait/endpoint-loss handling) trips one of these.
                InGameAssert.AreEqual(RouteStatus.Active, postTick.Status,
                    "Post-tick: sourceless synthetic route should remain Active after the " +
                    "benign sources-stale Skip. A different status means the route fell " +
                    "through to a dispatch/wait/endpoint outcome it should never reach with " +
                    "empty SourceRefs.");

                // The Skip path must emit NO ledger row attributed to this
                // route id. Walk only the rows appended during the tick.
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

                InGameAssert.IsFalse(
                    ledgerRowForRoute,
                    "RouteOrchestrator.Tick emitted a ledger row attributed to the synthetic " +
                    "route id, but a sourceless route must resolve to a benign sources-stale " +
                    "Skip that writes nothing. A row here means the route dispatched/debited " +
                    "without valid sources.");
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
