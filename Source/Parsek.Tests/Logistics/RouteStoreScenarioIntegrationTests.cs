using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Phase 4 of the Route store plan: scenario-codec integration tests.
    /// These exercise <see cref="RouteStore.SaveRoutesTo"/> and
    /// <see cref="RouteStore.LoadRoutesFrom"/> against a synthetic scenario
    /// ConfigNode — the same surface that <c>ParsekScenario.OnSave</c> /
    /// <c>OnLoad</c> drives in production. Driving the real
    /// <see cref="ParsekScenario"/> lifecycle from a unit test requires the
    /// live <see cref="ScenarioModule"/> harness (Planetarium / Unity
    /// MonoBehaviour coroutines / GameEvents), so we exercise the integration
    /// through the same public seams the scenario uses, plus one source-text
    /// gate that catches deletion of the hookup itself
    /// (<see cref="Scenario_OnSaveAndOnLoad_InvokeRouteStoreCodec"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteStoreScenarioIntegrationTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteStoreScenarioIntegrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Fixture helpers
        // -----------------------------------------------------------------

        private static RouteEndpoint BuildKscOrigin()
        {
            return new RouteEndpoint
            {
                BodyName = "Kerbin",
                Latitude = -0.0972,
                Longitude = -74.5577,
                Altitude = 75.2,
                VesselPersistentId = 0,
                IsSurface = true
            };
        }

        private static RouteEndpoint BuildMunStopEndpoint(uint pid)
        {
            return new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 3.2001,
                Longitude = -45.1234,
                Altitude = 612.5,
                VesselPersistentId = pid,
                IsSurface = true
            };
        }

        private static RouteStop BuildSimpleStop(uint pid)
        {
            return new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(pid),
                ConnectionKind = RouteConnectionKind.DockingPort,
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
            };
        }

        private static Route BuildRoute(string id, string name, uint stopPid)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(name)
                .WithOrigin(BuildKscOrigin())
                .WithStop(BuildSimpleStop(stopPid))
                .Build();
        }

        // -----------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------

        // catches: wrong parent level (e.g., nested under a recording).
        [Fact]
        public void Scenario_OnSave_WritesRoutesAtRightParent()
        {
            RouteStore.AddRoute(BuildRoute("route-A", "Alpha", 11111u));
            RouteStore.AddRoute(BuildRoute("route-B", "Beta", 22222u));

            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);

            ConfigNode[] routesChildren = scenarioNode.GetNodes("ROUTES");
            Assert.Single(routesChildren);
            ConfigNode[] routeChildren = routesChildren[0].GetNodes("ROUTE");
            Assert.Equal(2, routeChildren.Length);
            // ROUTE nodes belong under ROUTES, not loose on the scenario node.
            Assert.Empty(scenarioNode.GetNodes("ROUTE"));
        }

        // catches: load-path silent skip.
        [Fact]
        public void Scenario_OnLoad_ReadsRoutesFromCleanState()
        {
            // Author a scenario node by going through the save path so the
            // exact ConfigNode shape Phase 4 expects on disk is what we read.
            RouteStore.AddRoute(BuildRoute("route-1", "One", 111u));
            RouteStore.AddRoute(BuildRoute("route-2", "Two", 222u));
            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);
            RouteStore.ResetForTesting();
            logLines.Clear();

            int loaded = RouteStore.LoadRoutesFrom(scenarioNode);

            Assert.Equal(2, loaded);
            Assert.Equal(2, RouteStore.CommittedRoutes.Count);
            Assert.Equal("route-1", RouteStore.CommittedRoutes[0].Id);
            Assert.Equal("route-2", RouteStore.CommittedRoutes[1].Id);
        }

        // catches: UI order shuffle through a full round-trip.
        [Fact]
        public void Scenario_RoundTrip_PreservesRouteOrder()
        {
            string[] originalOrder = { "route-alpha", "route-bravo", "route-charlie", "route-delta" };
            for (int i = 0; i < originalOrder.Length; i++)
                RouteStore.AddRoute(BuildRoute(originalOrder[i], originalOrder[i], 1000u + (uint)i));

            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);
            RouteStore.ResetForTesting();
            RouteStore.LoadRoutesFrom(scenarioNode);

            Assert.Equal(originalOrder.Length, RouteStore.CommittedRoutes.Count);
            for (int i = 0; i < originalOrder.Length; i++)
                Assert.Equal(originalOrder[i], RouteStore.CommittedRoutes[i].Id);
        }

        // catches: regression in the "saves without routes" additive contract.
        [Fact]
        public void Scenario_RoundTrip_EmptyStore_NoRoutesNode()
        {
            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);

            Assert.False(scenarioNode.HasNode("ROUTES"),
                "Empty store must not emit a ROUTES node — keeps saves lean");

            // Round-trip the empty save: should load zero routes, no Warn.
            int loaded = RouteStore.LoadRoutesFrom(scenarioNode);
            Assert.Equal(0, loaded);
            foreach (string line in logLines)
            {
                Assert.False(line.Contains("[WARN]") && line.Contains("[Route]"),
                    "Empty round-trip must not emit any Warn");
            }
        }

        // catches: stale-entry leak between saves.
        [Fact]
        public void Scenario_OnSave_RemovesStaleRoutesNode()
        {
            var scenarioNode = new ConfigNode("SCENARIO");

            // Simulate a save that previously held routes by inserting a
            // stale ROUTES child with one ROUTE entry. After we save with an
            // empty store, the stale wrapper must be gone.
            ConfigNode staleRoutes = scenarioNode.AddNode("ROUTES");
            ConfigNode staleRoute = staleRoutes.AddNode("ROUTE");
            staleRoute.AddValue("id", "stale-route-from-prior-save");

            // Empty store.
            Assert.Empty(RouteStore.CommittedRoutes);

            RouteStore.SaveRoutesTo(scenarioNode);

            Assert.False(scenarioNode.HasNode("ROUTES"),
                "Stale ROUTES wrapper must be stripped on empty-store save");
        }

        // catches: codec drift on the M1 fields through the same SCENARIO-node
        // shape ParsekScenario drives - a non-KSC origin endpoint carrying the
        // descriptor coordinates (populated by RouteBuilder from the origin
        // proof descriptor, Phase 3) plus the sparse dispatch priority must
        // survive a full SaveRoutesTo -> LoadRoutesFrom round-trip.
        [Fact]
        public void Scenario_RoundTrip_PreservesPriorityAndNonKscOriginDescriptor()
        {
            var origin = new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 12.3456789,
                Longitude = -98.7654321,
                Altitude = 1234.5,
                VesselPersistentId = 987654321u,
                IsSurface = true
            };
            Route original = new RouteFixtureBuilder()
                .WithId("route-m1-origin")
                .WithName("Mun Depot Run")
                .WithKscOrigin(false)
                .WithDispatchPriority(2)
                .WithOrigin(origin)
                .WithStop(BuildSimpleStop(33333u))
                .Build();
            RouteStore.AddRoute(original);

            var scenarioNode = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(scenarioNode);
            RouteStore.ResetForTesting();
            int loaded = RouteStore.LoadRoutesFrom(scenarioNode);

            Assert.Equal(1, loaded);
            Assert.True(RouteStore.TryGetRoute("route-m1-origin", out Route reloaded));
            Assert.Equal(2, reloaded.DispatchPriority);
            Assert.False(reloaded.IsKscOrigin);
            Assert.Equal(987654321u, reloaded.Origin.VesselPersistentId);
            Assert.Equal("Mun", reloaded.Origin.BodyName);
            Assert.Equal(12.3456789, reloaded.Origin.Latitude);
            Assert.Equal(-98.7654321, reloaded.Origin.Longitude);
            Assert.Equal(1234.5, reloaded.Origin.Altitude);
            Assert.True(reloaded.Origin.IsSurface,
                "Surface-typed origin must survive the round-trip - it gates the proximity rebuild fallback");
        }

        // catches: a future edit to ParsekScenario.OnSave or OnLoad that drops
        // the RouteStore.SaveRoutesTo / LoadRoutesFrom hookup. The granular
        // tests in this class drive the codec directly and would NOT catch a
        // missing hookup — only this test does.
        //
        // The intended shape was to instantiate ParsekScenario, call
        // .OnSave(node), reset stores, call .OnLoad(node), and round-trip
        // through the real lifecycle. That cannot run from xUnit: OnSave's
        // game-state phase calls Planetarium.GetUniversalTime() unguarded
        // (ParsekScenario.cs:1156) BEFORE the routes phase runs, and OnLoad
        // calls it unguarded for reconcileUT (ParsekScenario.cs:2229) plus
        // depends on stateRecorder.Subscribe / SubscribeVesselLifecycleEvents
        // (Unity GameEvents) and StartCoroutine (Unity MonoBehaviour
        // lifecycle). None of those have test stubs in this project, and no
        // test in Parsek.Tests currently drives the real OnSave/OnLoad. A
        // source-text gate is the only thing that reliably catches a literal
        // deletion of the four hookup lines without requiring scaffolding
        // that does not yet exist; the codebase already uses this pattern
        // (see ChainSaveLoadTests.ChainStateNotPersistedInScenario and
        // GrepAuditTests for ERS / ELS).
        //
        // M1 tightening (plan-logistics-m1-origin-debit Phase 6, design D10
        // layer b): the load-side gates are ORDERED, not just present -
        // LoadRoutesFrom must precede RevalidateSources("OnLoad") inside
        // ParsekScenario.cs, because revalidation reads the freshly loaded
        // routes' SourceRefs against ERS; reversing them would validate the
        // PREVIOUS scene's routes and silently skip the loaded ones.
        [Fact]
        public void Scenario_OnSaveAndOnLoad_InvokeRouteStoreCodec()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");

            // Fallback path if the test runs from an unusual working dir.
            if (!File.Exists(scenarioPath))
            {
                scenarioPath = Path.Combine(projectRoot,
                    "Parsek", "ParsekScenario.cs");
            }

            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);

            // Save-side hookup: presence gate (no ordering constraint exists
            // on the save side).
            Assert.Contains("RouteStore.SaveRoutesTo(node)", source);

            // Load-side hookups: ORDERED gate. Load must come first, then the
            // ERS revalidation of the freshly loaded routes (Phase 5 hook;
            // today ParsekScenario.cs calls them on consecutive lines).
            int loadIndex = source.IndexOf(
                "RouteStore.LoadRoutesFrom(node)", StringComparison.Ordinal);
            int revalidateIndex = source.IndexOf(
                "RouteStore.RevalidateSources(\"OnLoad\")", StringComparison.Ordinal);
            Assert.True(loadIndex >= 0,
                "ParsekScenario.cs must call RouteStore.LoadRoutesFrom(node) in OnLoad");
            Assert.True(revalidateIndex >= 0,
                "ParsekScenario.cs must call RouteStore.RevalidateSources(\"OnLoad\") after loading routes");
            Assert.True(loadIndex < revalidateIndex,
                "RouteStore.LoadRoutesFrom(node) must PRECEDE RouteStore.RevalidateSources(\"OnLoad\") " +
                $"inside ParsekScenario.cs (loadIndex={loadIndex}, revalidateIndex={revalidateIndex}); " +
                "revalidation reads the loaded routes' SourceRefs against ERS");
        }

        // catches: a future edit to ParsekScenario that drops the
        // RouteOrchestrator.Tick(currentUT) Update() hook. The orchestrator
        // tests in RouteOrchestratorTests drive Tick directly with a fake env,
        // so they would NOT catch a missing hookup — only this test does.
        //
        // We cannot drive ParsekScenario.Update() from xUnit: it calls
        // Planetarium.GetUniversalTime() unguarded and depends on the live
        // Unity MonoBehaviour lifecycle. Same source-text gate pattern as
        // Scenario_OnSaveAndOnLoad_InvokeRouteStoreCodec above.
        [Fact]
        public void Scenario_Update_InvokesRouteOrchestratorTick()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");

            // Fallback path if the test runs from an unusual working dir.
            if (!File.Exists(scenarioPath))
            {
                scenarioPath = Path.Combine(projectRoot,
                    "Parsek", "ParsekScenario.cs");
            }

            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);

            // The literal call site the orchestrator depends on. Editing the
            // signature requires updating this gate string in lockstep.
            Assert.Contains("RouteOrchestrator.Tick(currentUT)", source);
        }
    }
}
