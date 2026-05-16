using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    [Collection("Sequential")]
    public class RouteStoreTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteStoreTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            // ResetForTesting itself emits a Verbose line; clear it so each
            // test starts with an empty capture and can assert on its own
            // log output without false positives from the test fixture.
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

        private static RouteEndpoint BuildMunStopEndpoint()
        {
            return new RouteEndpoint
            {
                BodyName = "Mun",
                Latitude = 3.2001,
                Longitude = -45.1234,
                Altitude = 612.5,
                VesselPersistentId = 67890,
                IsSurface = true
            };
        }

        private static RouteStop BuildSimpleStop()
        {
            return new RouteStop
            {
                Endpoint = BuildMunStopEndpoint(),
                ConnectionKind = RouteConnectionKind.DockingPort,
                SegmentIndexBefore = 0,
                DeliveryOffsetSeconds = 0.0,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } }
            };
        }

        private static Route BuildRoute(string id, string name = null)
        {
            return new RouteFixtureBuilder()
                .WithId(id)
                .WithName(name ?? id)
                .WithOrigin(BuildKscOrigin())
                .WithStop(BuildSimpleStop())
                .Build();
        }

        // -----------------------------------------------------------------
        // AddRoute
        // -----------------------------------------------------------------

        // catches: silent add that bypasses transition logging.
        [Fact]
        public void AddRoute_HappyPath_StoresAndLogsCreation()
        {
            Route route = BuildRoute("route-A");

            RouteStore.AddRoute(route);

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Same(route, RouteStore.CommittedRoutes[0]);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[RouteStore]")
                && l.Contains("Route route-A added")
                && l.Contains("status=Active")
                && l.Contains("stops=1"));
        }

        [Fact]
        public void AddRoute_Null_LogsWarnNoThrow()
        {
            RouteStore.AddRoute(null);

            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[RouteStore]")
                && l.Contains("null route"));
        }

        [Fact]
        public void AddRoute_EmptyId_LogsWarnNoThrow()
        {
            Route route = BuildRoute("placeholder");
            route.Id = "";

            RouteStore.AddRoute(route);

            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[RouteStore]")
                && l.Contains("empty Id"));
        }

        // catches: a future refactor that swaps add semantics from "ignore"
        // to "replace" without audit.
        [Fact]
        public void AddRoute_DuplicateId_LogsWarnAndKeepsOriginal()
        {
            Route first = BuildRoute("dup-id", "First");
            Route second = BuildRoute("dup-id", "Second");

            RouteStore.AddRoute(first);
            RouteStore.AddRoute(second);

            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Same(first, RouteStore.CommittedRoutes[0]);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[RouteStore]")
                && l.Contains("duplicate id")
                && l.Contains("dup-id"));
        }

        // -----------------------------------------------------------------
        // RemoveRoute
        // -----------------------------------------------------------------

        [Fact]
        public void RemoveRoute_Unknown_LogsWarnReturnsFalse()
        {
            bool removed = RouteStore.RemoveRoute("does-not-exist");

            Assert.False(removed);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("[RouteStore]")
                && l.Contains("not found")
                && l.Contains("does-not"));
        }

        [Fact]
        public void RemoveRoute_NullOrEmpty_LogsWarnReturnsFalse()
        {
            Assert.False(RouteStore.RemoveRoute(null));
            Assert.False(RouteStore.RemoveRoute(""));

            int warnCount = 0;
            for (int i = 0; i < logLines.Count; i++)
            {
                if (logLines[i].Contains("[WARN]")
                    && logLines[i].Contains("[RouteStore]")
                    && logLines[i].Contains("null or empty id"))
                {
                    warnCount++;
                }
            }
            Assert.Equal(2, warnCount);
        }

        [Fact]
        public void RemoveRoute_Known_RemovesAndLogsAndReturnsTrue()
        {
            Route route = BuildRoute("route-removable");
            RouteStore.AddRoute(route);
            logLines.Clear();

            bool removed = RouteStore.RemoveRoute("route-removable");

            Assert.True(removed);
            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[RouteStore]")
                && l.Contains("Route route-re")
                && l.Contains("removed"));
        }

        // -----------------------------------------------------------------
        // TryGetRoute
        // -----------------------------------------------------------------

        [Fact]
        public void TryGetRoute_Hit_ReturnsTrueAndRoute()
        {
            Route route = BuildRoute("route-lookup");
            RouteStore.AddRoute(route);

            bool found = RouteStore.TryGetRoute("route-lookup", out Route resolved);

            Assert.True(found);
            Assert.Same(route, resolved);
        }

        [Fact]
        public void TryGetRoute_Miss_ReturnsFalseAndNull()
        {
            bool found = RouteStore.TryGetRoute("missing", out Route resolved);

            Assert.False(found);
            Assert.Null(resolved);
        }

        // -----------------------------------------------------------------
        // SaveRoutesTo / LoadRoutesFrom
        // -----------------------------------------------------------------

        // catches: empty ROUTES nodes bloating saves and breaking the
        // additive-load contract.
        [Fact]
        public void SaveRoutesTo_NoRoutes_OmitsRoutesNode()
        {
            var parent = new ConfigNode("SCENARIO");

            RouteStore.SaveRoutesTo(parent);

            Assert.False(parent.HasNode("ROUTES"),
                "Empty store must not write a ROUTES wrapper node");
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[RouteStore]")
                && l.Contains("no routes to save"));
        }

        // catches: stale-entry leak between saves.
        [Fact]
        public void SaveRoutesTo_RemovesStaleRoutesNode()
        {
            var parent = new ConfigNode("SCENARIO");
            ConfigNode stale = parent.AddNode("ROUTES");
            ConfigNode staleRoute = stale.AddNode("ROUTE");
            staleRoute.AddValue("id", "stale-from-prior-save");

            RouteStore.SaveRoutesTo(parent);

            Assert.False(parent.HasNode("ROUTES"),
                "SaveRoutesTo must strip pre-existing ROUTES nodes before deciding what to write");
        }

        // catches: scenario-codec contract drift.
        [Fact]
        public void SaveRoutesTo_LoadRoutesFrom_RoundTrip()
        {
            Route a = BuildRoute("route-A", "Alpha");
            Route b = BuildRoute("route-B", "Beta");
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);
            RouteStore.ResetForTesting();
            Assert.Empty(RouteStore.CommittedRoutes);

            int loaded = RouteStore.LoadRoutesFrom(parent);

            Assert.Equal(2, loaded);
            Assert.Equal(2, RouteStore.CommittedRoutes.Count);
            Assert.Equal("route-A", RouteStore.CommittedRoutes[0].Id);
            Assert.Equal("Alpha", RouteStore.CommittedRoutes[0].Name);
            Assert.Equal("route-B", RouteStore.CommittedRoutes[1].Id);
            Assert.Equal("Beta", RouteStore.CommittedRoutes[1].Name);
        }

        // catches: noisy log on the common fresh-save path.
        [Fact]
        public void LoadRoutesFrom_NoRoutesNode_LoadsZeroNoWarn()
        {
            var parent = new ConfigNode("SCENARIO");

            int loaded = RouteStore.LoadRoutesFrom(parent);

            Assert.Equal(0, loaded);
            Assert.Empty(RouteStore.CommittedRoutes);

            // The Verbose breadcrumb is fine; a Warn would be noise.
            foreach (string line in logLines)
            {
                Assert.False(line.Contains("[WARN]") && line.Contains("[RouteStore]"),
                    "LoadRoutesFrom on a save without routes must not emit any Warn");
            }
        }

        [Fact]
        public void LoadRoutesFrom_NullParent_LoadsZero()
        {
            int loaded = RouteStore.LoadRoutesFrom(null);

            Assert.Equal(0, loaded);
            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[RouteStore]")
                && l.Contains("null parent"));
        }

        // catches: hash-set-driven shuffle that changes UI ordering.
        [Fact]
        public void LoadRoutesFrom_PreservesOrder()
        {
            string[] originalOrder = { "route-1", "route-2", "route-3" };
            for (int i = 0; i < originalOrder.Length; i++)
                RouteStore.AddRoute(BuildRoute(originalOrder[i]));

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);
            RouteStore.ResetForTesting();

            RouteStore.LoadRoutesFrom(parent);

            Assert.Equal(originalOrder.Length, RouteStore.CommittedRoutes.Count);
            for (int i = 0; i < originalOrder.Length; i++)
                Assert.Equal(originalOrder[i], RouteStore.CommittedRoutes[i].Id);
        }

        // catches: a load path that throws on partial corruption or that
        // counts dropped routes in the success total.
        [Fact]
        public void LoadRoutesFrom_DroppedRoute_DoesNotIncrementCount()
        {
            // Build a parent with two ROUTE nodes: one valid, one with no
            // STOP children (Phase-2 rejection path).
            var parent = new ConfigNode("SCENARIO");
            ConfigNode routesNode = parent.AddNode("ROUTES");

            // Good route — serialized through the real codec via the store.
            RouteStore.AddRoute(BuildRoute("route-good"));
            var goodSerialized = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(goodSerialized);
            RouteStore.ResetForTesting();
            logLines.Clear();
            ConfigNode goodRouteNode = goodSerialized.GetNode("ROUTES").GetNodes("ROUTE")[0];
            routesNode.AddNode(goodRouteNode.CreateCopy());

            // Bad route — hand-authored, missing STOP children. The codec
            // emits its own Warn explaining the reject.
            ConfigNode badRoute = routesNode.AddNode("ROUTE");
            badRoute.AddValue("id", "route-bad");
            badRoute.AddValue("status", "Active");
            ConfigNode badOrigin = badRoute.AddNode(RouteCodec.OriginNode);
            badOrigin.AddValue("bodyName", "Kerbin");
            badOrigin.AddValue("latitude", "0");
            badOrigin.AddValue("longitude", "0");
            badOrigin.AddValue("altitude", "0");
            badOrigin.AddValue("isSurface", "True");
            // No STOP children -> codec rejects.

            int loaded = RouteStore.LoadRoutesFrom(parent);

            Assert.Equal(1, loaded);
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal("route-good", RouteStore.CommittedRoutes[0].Id);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]")
                && l.Contains("[RouteStore]")
                && l.Contains("loaded 1 route")
                && l.Contains("1 dropped"));
        }

        // -----------------------------------------------------------------
        // ResetForTesting
        // -----------------------------------------------------------------

        [Fact]
        public void ResetForTesting_ClearsAndLogsPrevCount()
        {
            RouteStore.AddRoute(BuildRoute("route-1"));
            RouteStore.AddRoute(BuildRoute("route-2"));
            logLines.Clear();

            RouteStore.ResetForTesting();

            Assert.Empty(RouteStore.CommittedRoutes);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]")
                && l.Contains("[RouteStore]")
                && l.Contains("ResetForTesting")
                && l.Contains("prevCount=2"));
        }
    }
}
