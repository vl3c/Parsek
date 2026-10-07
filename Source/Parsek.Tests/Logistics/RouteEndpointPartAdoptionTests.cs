using System;
using System.Collections.Generic;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Owner ruling 2026-10-07: a route's endpoint part scope (ROUTE-DELIVERY-INTO-DOCKED-VISITOR)
    /// admits only the parts the route RECORDED as the endpoint's, so a module the player docks to
    /// a station after setting up its route gets no cargo. <see cref="RouteEndpointPartAdoption"/>
    /// re-captures each resolvable endpoint's current parts (by per-launch flightID) into a
    /// per-route override the scope consults before the recorded sets. Pinned here: which
    /// endpoint an adoption belongs to, what the action writes for each endpoint and logs, and
    /// that the override survives the ROUTE codec and the route store's save / load. The scope
    /// side is in <c>RouteEndpointPartScopeTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointPartAdoptionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteEndpointPartAdoptionTests()
        {
            ParsekLog.ResetTestOverrides();
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

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private const string GuidA = "11111111-2222-3333-4444-555555555555";
        private const string GuidB = "99999999-8888-7777-6666-555555555555";

        private static RouteEndpoint Endpoint(uint root, uint pid, string guid = null)
        {
            return new RouteEndpoint
            {
                RootPartUId = root,
                VesselPersistentId = pid,
                LaunchGuid = guid,
                BodyName = "Kerbin",
                IsSurface = false,
            };
        }

        private static RouteStop Stop(RouteEndpoint endpoint)
        {
            return new RouteStop
            {
                Endpoint = endpoint,
                ConnectionKind = RouteConnectionKind.DockingPort,
                DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
            };
        }

        private static RouteEndpointAdoptedParts Entry(uint root, uint pid, params uint[] flightIds)
        {
            return new RouteEndpointAdoptedParts
            {
                EndpointRootPartUId = root,
                EndpointVesselPersistentId = pid,
                AdoptedUT = 10.0,
                PartFlightIds = new HashSet<uint>(flightIds),
            };
        }

        // Depot origin (root 500) and two stops (station root 100, outpost root 300).
        private static Route ThreeEndpointRoute()
        {
            return new RouteFixtureBuilder()
                .WithId("route-adopt-0001")
                .WithName("Station run")
                .WithOrigin(Endpoint(500u, 50u))
                .WithStop(Stop(Endpoint(100u, 10u)))
                .WithStop(Stop(Endpoint(300u, 30u)))
                .Build();
        }

        /// <summary>A capture seam answering per endpoint root flightID: an undocked craft
        /// (its part flightIDs, chained) or a docked composite.</summary>
        private sealed class FakeCapture
        {
            internal readonly Dictionary<uint, (string Name, List<uint> Ids)> ByRoot =
                new Dictionary<uint, (string, List<uint>)>();
            internal readonly Dictionary<uint, RouteEndpointPartAdoption.CapturedVessel> Composite =
                new Dictionary<uint, RouteEndpointPartAdoption.CapturedVessel>();
            internal readonly List<uint> Calls = new List<uint>();
            internal Action<RouteEndpoint> OnCall;

            internal bool Capture(RouteEndpoint endpoint,
                out RouteEndpointPartAdoption.CapturedVessel vessel, out string reason)
            {
                Calls.Add(endpoint.RootPartUId);
                OnCall?.Invoke(endpoint);
                if (Composite.TryGetValue(endpoint.RootPartUId, out vessel))
                {
                    reason = null;
                    return true;
                }
                if (ByRoot.TryGetValue(endpoint.RootPartUId, out var hit))
                {
                    var parts = new List<RouteEndpointPartScope.PartRecord>();
                    for (int i = 0; i < hit.Ids.Count; i++)
                        parts.Add(new RouteEndpointPartScope.PartRecord(hit.Ids[i], 0u, i - 1));
                    vessel = new RouteEndpointPartAdoption.CapturedVessel
                    {
                        Name = hit.Name,
                        Parts = parts,
                        Nodes = new List<RouteEndpointPartScope.DockNodeRecord>(),
                        RootFlightId = hit.Ids.Count > 0 ? hit.Ids[0] : 0u,
                    };
                    reason = null;
                    return true;
                }
                vessel = null;
                reason = "no-vessel-near";
                return false;
            }
        }

        private static RouteEndpointPartAdoption.CapturedVessel FromCraft(
            string name, RouteEndpointPartScopeTests.Craft craft, uint root)
        {
            return new RouteEndpointPartAdoption.CapturedVessel
            {
                Name = name,
                Parts = craft.PartRecords(),
                Nodes = craft.NodeRecords(),
                RootFlightId = root,
            };
        }

        private static RouteEndpointAdoptedParts FindByRoot(Route route, uint root)
        {
            return route.AdoptedEndpointParts?.FirstOrDefault(e => e.EndpointRootPartUId == root);
        }

        // ==================================================================
        // Which endpoint an adoption belongs to
        // ==================================================================

        // catches: an adoption following the endpoint onto a different vessel after a rebind
        // (RouteEndpointTransfer rewrites the root), or not matching its own endpoint.
        [Fact]
        public void Binding_ByRootFlightId_WhenTheEndpointHasOne()
        {
            RouteEndpointAdoptedParts entry = Entry(100u, 10u, 100u);

            Assert.True(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(100u, 10u)));
            // The root is launch-unique: a craft-baked pid that moved does not matter.
            Assert.True(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(100u, 77u)));
            // A rebound endpoint (new root) no longer finds the old vessel's adoption.
            Assert.False(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(400u, 10u)));
            // An endpoint that lost its root cannot be matched to a root-keyed adoption.
            Assert.False(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(0u, 10u)));
            Assert.False(RouteEndpointPartAdoption.BindingMatches(null, Endpoint(100u, 10u)));
        }

        // catches: a bare craft-baked pid match naming a DIFFERENT launch of the same craft
        // (CLAUDE.md persistentId rule): with no root on either side the pid matches only
        // while the launch guids do not conclusively differ.
        [Fact]
        public void Binding_ByPidAndGuid_WhenTheEndpointHasNoRoot()
        {
            var entry = new RouteEndpointAdoptedParts
            {
                EndpointVesselPersistentId = 10u,
                EndpointLaunchGuid = GuidA,
                PartFlightIds = new HashSet<uint> { 1u },
            };

            Assert.True(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(0u, 10u, GuidA)));
            Assert.True(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(0u, 10u, null)));
            Assert.False(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(0u, 10u, GuidB)));
            Assert.False(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(0u, 11u, GuidA)));
            // A root that appeared later is a rebind: the pid-keyed adoption does not follow it.
            Assert.False(RouteEndpointPartAdoption.BindingMatches(entry, Endpoint(100u, 10u, GuidA)));

            var noIdentity = new RouteEndpointAdoptedParts { PartFlightIds = new HashSet<uint> { 1u } };
            Assert.False(RouteEndpointPartAdoption.BindingMatches(noIdentity, Endpoint(0u, 0u)));
        }

        [Fact]
        public void FindAdoptedPartFlightIds_ReturnsTheMatchingEntryOrNull()
        {
            Route route = ThreeEndpointRoute();
            Assert.Null(RouteEndpointPartAdoption.FindAdoptedPartFlightIds(route, Endpoint(100u, 10u)));

            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                Entry(300u, 30u, 300u, 301u),
                Entry(100u, 10u, 100u, 101u, 110u),
            };

            Assert.Equal(new HashSet<uint> { 100u, 101u, 110u },
                RouteEndpointPartAdoption.FindAdoptedPartFlightIds(route, Endpoint(100u, 10u)));
            Assert.Null(RouteEndpointPartAdoption.FindAdoptedPartFlightIds(route, Endpoint(500u, 50u)));
            Assert.Null(RouteEndpointPartAdoption.FindAdoptedPartFlightIds(null, Endpoint(100u, 10u)));

            // An entry with no parts is no override.
            route.AdoptedEndpointParts.Add(Entry(500u, 50u));
            Assert.Null(RouteEndpointPartAdoption.FindAdoptedPartFlightIds(route, Endpoint(500u, 50u)));
        }

        // ==================================================================
        // The action
        // ==================================================================

        // catches: the action not writing an override for every resolvable endpoint (origin
        // and each stop), clobbering the previous override of an endpoint it could not reach,
        // keeping a stale entry no endpoint names any more, or logging no per-endpoint counts.
        [Fact]
        public void Adopt_WritesEachResolvableEndpoint_KeepsUnreachable_PrunesStale_LogsCounts()
        {
            Route route = ThreeEndpointRoute();
            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                Entry(300u, 30u, 300u, 301u),       // stop 2's earlier adoption, unreachable now
                Entry(777u, 77u, 777u),             // a vessel no endpoint names any more
            };
            var capture = new FakeCapture();
            capture.ByRoot[500u] = ("Depot", new List<uint> { 500u, 501u });
            capture.ByRoot[100u] = ("Station", new List<uint> { 100u, 101u, 110u });

            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(route, 1234.5, capture.Capture);

            Assert.Equal(new List<uint> { 500u, 100u, 300u }, capture.Calls);
            Assert.Equal(2, result.AdoptedCount);
            Assert.Equal(1, result.UnresolvedCount);
            Assert.Equal(1, result.PrunedCount);
            Assert.Equal(3, result.Endpoints.Count);

            RouteEndpointAdoptedParts origin = FindByRoot(route, 500u);
            Assert.NotNull(origin);
            Assert.Equal(50u, origin.EndpointVesselPersistentId);
            Assert.Equal(1234.5, origin.AdoptedUT);
            Assert.Equal(new HashSet<uint> { 500u, 501u }, origin.PartFlightIds);
            Assert.Equal(new HashSet<uint> { 100u, 101u, 110u }, FindByRoot(route, 100u).PartFlightIds);
            Assert.Equal(new HashSet<uint> { 300u, 301u }, FindByRoot(route, 300u).PartFlightIds);
            Assert.Equal(10.0, FindByRoot(route, 300u).AdoptedUT);
            Assert.Null(FindByRoot(route, 777u));
            Assert.Equal(3, route.AdoptedEndpointParts.Count);

            Assert.Contains(logLines, l => l.Contains("[INFO]")
                && l.Contains("[Route]")
                && l.Contains("Endpoint part adoption:")
                && l.Contains("route=route-ad")
                && l.Contains("ut=1234.5")
                && l.Contains("endpoints=3")
                && l.Contains("adopted=2")
                && l.Contains("unresolved=1")
                && l.Contains("pruned=1")
                && l.Contains("origin:'Depot' parts=2 prev=none")
                && l.Contains("stop1:'Station' parts=3 prev=none")
                && l.Contains("stop2:unresolved('no-vessel-near') kept=2"));
            Assert.Single(logLines, l => l.Contains("Endpoint part adoption:"));
        }

        // catches: a second Update parts unioning into (instead of replacing) the endpoint's set,
        // so a module the player undocked for good stayed the endpoint's forever.
        [Fact]
        public void Adopt_AgainReplacesTheEndpointsSet()
        {
            Route route = ThreeEndpointRoute();
            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                Entry(100u, 10u, 100u, 101u, 110u, 111u),
            };
            var capture = new FakeCapture();
            capture.ByRoot[100u] = ("Station", new List<uint> { 100u, 101u });

            RouteEndpointPartAdoption.AdoptCurrentParts(route, 50.0, capture.Capture);

            Assert.Equal(new HashSet<uint> { 100u, 101u }, FindByRoot(route, 100u).PartFlightIds);
            Assert.Single(route.AdoptedEndpointParts, e => e.EndpointRootPartUId == 100u);
            Assert.Contains(logLines, l => l.Contains("stop1:'Station' parts=2 prev=4"));
        }

        // catches: a KSC or harvest route resolving its display-only origin (pid 0, no vessel).
        [Fact]
        public void Adopt_SkipsAKscOrHarvestOrigin()
        {
            foreach (bool harvest in new[] { false, true })
            {
                Route route = ThreeEndpointRoute();
                if (harvest) route.IsHarvestOrigin = true;
                else route.IsKscOrigin = true;
                var capture = new FakeCapture();
                capture.ByRoot[500u] = ("Depot", new List<uint> { 500u });
                capture.ByRoot[100u] = ("Station", new List<uint> { 100u });

                RouteEndpointPartAdoption.AdoptCurrentParts(route, 50.0, capture.Capture);

                Assert.DoesNotContain(500u, capture.Calls);
                Assert.Null(FindByRoot(route, 500u));
                Assert.NotNull(FindByRoot(route, 100u));
            }
        }

        // catches: keying the adoption to the endpoint as it was BEFORE the resolution, when the
        // resolution itself rebound the stop to another vessel (RouteEndpointTransfer runs inside
        // the resolver): the entry would name the vanished vessel and never be found.
        [Fact]
        public void Adopt_KeysTheEntryToTheBindingAfterTheResolution()
        {
            Route route = ThreeEndpointRoute();
            var capture = new FakeCapture();
            capture.ByRoot[100u] = ("New station", new List<uint> { 400u, 401u });
            capture.OnCall = ep =>
            {
                if (ep.RootPartUId == 100u)
                    route.Stops[0].Endpoint = Endpoint(400u, 40u, GuidA);
            };

            RouteEndpointPartAdoption.AdoptCurrentParts(route, 60.0, capture.Capture);

            Assert.Null(FindByRoot(route, 100u));
            RouteEndpointAdoptedParts entry = FindByRoot(route, 400u);
            Assert.NotNull(entry);
            Assert.Equal(40u, entry.EndpointVesselPersistentId);
            Assert.Equal(VesselLaunchIdentity.NormalizeGuid(GuidA), entry.EndpointLaunchGuid);
            Assert.Equal(new HashSet<uint> { 400u, 401u }, entry.PartFlightIds);
        }

        // catches: an override written from an empty part read (every part would then fall to
        // the root piece only), or a route left with an empty list instead of none.
        [Fact]
        public void Adopt_NothingReadable_WritesNothing()
        {
            Route route = ThreeEndpointRoute();
            var capture = new FakeCapture();
            capture.ByRoot[100u] = ("Station", new List<uint>());
            capture.ByRoot[300u] = ("Outpost", new List<uint> { 0u });

            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(route, 70.0, capture.Capture);

            Assert.Equal(0, result.AdoptedCount);
            Assert.Equal(3, result.UnresolvedCount);
            Assert.Null(route.AdoptedEndpointParts);
            Assert.Contains(logLines, l => l.Contains("Endpoint part adoption:")
                && l.Contains("adopted=0")
                && l.Contains("stop1:unresolved('no-readable-parts')"));
        }

        // catches: an endpoint with neither a root nor a pid getting an entry nothing can match.
        [Fact]
        public void Adopt_EndpointWithNoIdentity_IsNotAdopted()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-no-identity")
                .WithOrigin(Endpoint(500u, 50u))
                .WithStop(Stop(Endpoint(0u, 0u)))
                .Build();
            var capture = new FakeCapture();
            capture.ByRoot[0u] = ("Somewhere", new List<uint> { 1u, 2u });

            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(route, 80.0, capture.Capture);

            Assert.Equal(0, result.AdoptedCount);
            Assert.Null(route.AdoptedEndpointParts);
            Assert.Contains(logLines, l => l.Contains("stop1:unresolved('no-endpoint-identity')"));
        }

        // ------------------------------------------------------------------
        // Docked composites (PR #2043 review)
        // ------------------------------------------------------------------

        // catches (the reviewer's probe): one press widening a lander stop docked INTO a
        // larger station to the whole station (scope own=3 before, own=7 after), so the
        // lander's cargo filled the host. The lander is refused and keeps what it had, and
        // its scope is unchanged.
        [Fact]
        public void Adopt_StopDockedIntoLargerStation_IsRefusedAndItsScopeIsUnchanged()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-lander-0001")
                .WithKscOrigin(true)
                .WithOrigin(Endpoint(0u, 0u))
                .WithStop(Stop(Endpoint(300u, 30u)))
                .Build();
            RouteEndpointPartScopeTests.Craft craft = RouteEndpointPartScopeTests.LanderInLargerStation();
            var capture = new FakeCapture();
            capture.Composite[300u] = FromCraft("Station T", craft, 400u);
            Func<RouteEndpoint, HashSet<uint>> recorded = ep =>
                ep.RootPartUId == 300u ? new HashSet<uint>(RouteEndpointPartScopeTests.LanderPids) : null;

            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(route, 90.0, capture.Capture, recorded);

            Assert.Equal(0, result.AdoptedCount);
            Assert.Equal(1, result.RefusedCount);
            Assert.Null(route.AdoptedEndpointParts);
            Assert.Contains(logLines, l => l.Contains("Endpoint part adoption:")
                && l.Contains("refused=1")
                && l.Contains("stop1:'Station T' refused=guest-in-larger-composite"));

            RouteEndpointPartScope.ResolveOwnPartSets(route, route.Stops[0].Endpoint,
                () => new Recording[0], out HashSet<uint> adopted, out _, out _);
            bool[] mask = RouteEndpointPartScope.SelectOwnParts(craft.PartRecords(), craft.NodeRecords(),
                300u, RouteEndpointPartScopeTests.LanderPids, adopted,
                out RouteEndpointPartScope.Outcome outcome, out int own, out _);
            Assert.Equal(RouteEndpointPartScope.Outcome.Scoped, outcome);
            Assert.Equal(3, own);
            Assert.NotNull(mask);
        }

        // catches: a refused endpoint losing the set an earlier press gave it.
        [Fact]
        public void Adopt_RefusedEndpointKeepsItsEarlierSet()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-lander-0002")
                .WithKscOrigin(true)
                .WithOrigin(Endpoint(0u, 0u))
                .WithStop(Stop(Endpoint(300u, 30u)))
                .WithAdoptedEndpointParts(Entry(300u, 30u, 300u, 301u, 302u))
                .Build();
            var capture = new FakeCapture();
            capture.Composite[300u] = FromCraft("Station T",
                RouteEndpointPartScopeTests.LanderInLargerStation(), 400u);

            RouteEndpointPartAdoption.AdoptCurrentParts(route, 95.0, capture.Capture);

            Assert.Equal(new HashSet<uint> { 300u, 301u, 302u }, FindByRoot(route, 300u).PartFlightIds);
            Assert.Equal(10.0, FindByRoot(route, 300u).AdoptedUT);
            Assert.Contains(logLines, l => l.Contains("stop1:'Station T' refused=guest-in-larger-composite kept=3"));
        }

        // catches: two stops on one composite both collapsing to the whole composite: the
        // station stop adopts its own pieces only, the lander stop is refused.
        [Fact]
        public void Adopt_TwoStopsOnOneComposite_StationTakesOnlyItsPieces()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-twostop-0001")
                .WithKscOrigin(true)
                .WithOrigin(Endpoint(0u, 0u))
                .WithStop(Stop(Endpoint(400u, 40u)))
                .WithStop(Stop(Endpoint(300u, 30u)))
                .Build();
            RouteEndpointPartScopeTests.Craft craft = RouteEndpointPartScopeTests.LanderInLargerStation();
            var capture = new FakeCapture();
            capture.Composite[400u] = FromCraft("Station T", craft, 400u);
            capture.Composite[300u] = FromCraft("Station T", craft, 400u);

            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(route, 100.0, capture.Capture);

            Assert.Equal(1, result.AdoptedCount);
            Assert.Equal(1, result.RefusedCount);
            Assert.Equal(new HashSet<uint> { 400u, 401u, 402u, 403u }, FindByRoot(route, 400u).PartFlightIds);
            Assert.Null(FindByRoot(route, 300u));
            Assert.Contains(logLines, l => l.Contains("stop1:'Station T' parts=4 prev=none excluded=1")
                && l.Contains("stop2:'Station T' refused=guest-in-larger-composite"));
        }

        // catches: the route's own transport, docked at the press, adopted as the station's.
        [Fact]
        public void Adopt_ExcludesTheRoutesOwnTransport()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-transport-0001")
                .WithKscOrigin(true)
                .WithOrigin(Endpoint(0u, 0u))
                .WithStop(Stop(Endpoint(100u, 10u)))
                .Build();
            // Station 100/101/102 with the transport (root 202) docked below its port.
            var craft = new RouteEndpointPartScopeTests.Craft()
                .Part(100u, 1001u)
                .Part(101u, 1002u, 100u)
                .Part(102u, 1003u, 101u)
                .Part(200u, 2001u, 102u)
                .Part(202u, 2003u, 200u)
                .Part(201u, 2002u, 202u)
                .Node(102u, 200u, ownRoot: 100u)
                .Node(200u, 102u, ownRoot: 202u);
            var capture = new FakeCapture();
            capture.Composite[100u] = FromCraft("Station", craft, 100u);

            RouteEndpointPartAdoption.AdoptCurrentParts(route, 110.0, capture.Capture,
                transportRootFlightIds: new HashSet<uint> { 202u });

            Assert.Equal(new HashSet<uint> { 100u, 101u, 102u }, FindByRoot(route, 100u).PartFlightIds);
        }

        // catches: the transport roots read off the route's recordings including an
        // endpoint's own root (a snapshot taken while docked can be rooted at the station),
        // or missing the start-docked proof's transport root.
        [Fact]
        public void TransportRoots_FromSnapshotsAndProof_MinusEndpointRoots()
        {
            var transport = new Recording
            {
                GhostVisualSnapshot = VesselNode(root: 0, 202u, 200u),
                VesselSnapshot = VesselNode(root: 1, 205u, 206u),
            };
            var dockChild = new Recording
            {
                GhostVisualSnapshot = VesselNode(root: 0, 100u, 202u),
                RouteOriginProof = new RouteOriginProof { StartDockedTransportRootPartUId = 601u },
            };

            HashSet<uint> roots = RouteEndpointPartAdoption.CollectTransportRootFlightIds(
                new[] { transport, dockChild, null },
                new[] { Endpoint(100u, 10u), Endpoint(0u, 0u) });

            Assert.Equal(new HashSet<uint> { 202u, 206u, 601u }, roots);
        }

        private static ConfigNode VesselNode(int root, params uint[] flightIds)
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddValue("root", root.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < flightIds.Length; i++)
            {
                ConfigNode part = vessel.AddNode("PART");
                part.AddValue("uid", flightIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return vessel;
        }

        [Fact]
        public void Adopt_NullRouteOrCapture_DoesNothing()
        {
            RouteEndpointPartAdoption.AdoptionResult result =
                RouteEndpointPartAdoption.AdoptCurrentParts(null, 1.0, new FakeCapture().Capture);
            Assert.Equal(0, result.AdoptedCount);
            Assert.Empty(result.Endpoints);

            Route route = ThreeEndpointRoute();
            result = RouteEndpointPartAdoption.AdoptCurrentParts(route, 1.0, null);
            Assert.Equal(0, result.AdoptedCount);
            Assert.Null(route.AdoptedEndpointParts);
        }

        // ==================================================================
        // Persistence
        // ==================================================================

        // catches: the override not surviving a save / load, or the codec writing it in an
        // order that changes the save bytes from one session to the next.
        [Fact]
        public void Codec_RoundTripsTheOverride_SortedAndSparse()
        {
            Route route = ThreeEndpointRoute();
            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                Entry(100u, 10u, 110u, 100u, 0u, 101u),
                new RouteEndpointAdoptedParts
                {
                    EndpointVesselPersistentId = 30u,
                    EndpointLaunchGuid = VesselLaunchIdentity.NormalizeGuid(GuidA),
                    AdoptedUT = 22.25,
                    PartFlightIds = new HashSet<uint> { 302u, 301u },
                },
            };

            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);

            ConfigNode adopted = node.GetNode(RouteCodec.AdoptedEndpointPartsNode);
            Assert.NotNull(adopted);
            ConfigNode[] entries = adopted.GetNodes(RouteCodec.AdoptedEndpointPartsEntryNode);
            Assert.Equal(2, entries.Length);
            Assert.Equal(new[] { "100", "101", "110" }, entries[0].GetValues(RouteCodec.AdoptedPartFlightIdValue));
            Assert.Equal("100", entries[0].GetValue("rootPartUId"));
            Assert.Equal("10", entries[0].GetValue("vesselPersistentId"));
            Assert.Null(entries[0].GetValue("launchGuid"));
            Assert.Null(entries[1].GetValue("rootPartUId"));

            Route back = Route.DeserializeFrom(node);
            Assert.NotNull(back);
            Assert.Equal(2, back.AdoptedEndpointParts.Count);
            RouteEndpointAdoptedParts a = back.AdoptedEndpointParts[0];
            Assert.Equal(100u, a.EndpointRootPartUId);
            Assert.Equal(10u, a.EndpointVesselPersistentId);
            Assert.Null(a.EndpointLaunchGuid);
            Assert.Equal(10.0, a.AdoptedUT);
            Assert.Equal(new HashSet<uint> { 100u, 101u, 110u }, a.PartFlightIds);
            RouteEndpointAdoptedParts b = back.AdoptedEndpointParts[1];
            Assert.Equal(0u, b.EndpointRootPartUId);
            Assert.Equal(30u, b.EndpointVesselPersistentId);
            Assert.Equal(VesselLaunchIdentity.NormalizeGuid(GuidA), b.EndpointLaunchGuid);
            Assert.Equal(22.25, b.AdoptedUT);
            Assert.Equal(new HashSet<uint> { 301u, 302u }, b.PartFlightIds);
        }

        // catches: a route with no override writing anything new (a save without Update parts must
        // stay byte-identical), or reading back an empty list instead of none.
        [Fact]
        public void Codec_NoOverride_WritesNoNode_ReadsNull()
        {
            Route route = ThreeEndpointRoute();
            var plain = new ConfigNode("ROUTE");
            route.SerializeInto(plain);

            route.AdoptedEndpointParts = new List<RouteEndpointAdoptedParts>
            {
                Entry(100u, 10u),           // no parts: dropped
                Entry(100u, 10u, 0u),       // only an unreadable id: dropped
            };
            var emptied = new ConfigNode("ROUTE");
            route.SerializeInto(emptied);

            Assert.False(plain.HasNode(RouteCodec.AdoptedEndpointPartsNode));
            Assert.Equal(plain.ToString(), emptied.ToString());
            Assert.Null(Route.DeserializeFrom(plain).AdoptedEndpointParts);
        }

        // catches: a hand-edited or damaged entry poisoning the route load.
        [Fact]
        public void Codec_MalformedEntries_AreSkipped()
        {
            Route route = ThreeEndpointRoute();
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            ConfigNode adopted = node.AddNode(RouteCodec.AdoptedEndpointPartsNode);
            ConfigNode good = adopted.AddNode(RouteCodec.AdoptedEndpointPartsEntryNode);
            good.AddValue("rootPartUId", "100");
            good.AddValue(RouteCodec.AdoptedPartFlightIdValue, "100");
            good.AddValue(RouteCodec.AdoptedPartFlightIdValue, "not-a-number");
            good.AddValue(RouteCodec.AdoptedPartFlightIdValue, "101");
            ConfigNode noParts = adopted.AddNode(RouteCodec.AdoptedEndpointPartsEntryNode);
            noParts.AddValue("rootPartUId", "300");

            Route back = Route.DeserializeFrom(node);

            Assert.NotNull(back);
            Assert.Single(back.AdoptedEndpointParts);
            Assert.Equal(new HashSet<uint> { 100u, 101u }, back.AdoptedEndpointParts[0].PartFlightIds);
            Assert.Equal(-1.0, back.AdoptedEndpointParts[0].AdoptedUT);
        }

        // catches: RouteStore's ROUTES save / load dropping the override (the scope would
        // forget the player's Update parts on the next cold load).
        [Fact]
        public void RouteStore_SaveLoad_PreservesTheOverride()
        {
            Route route = new RouteFixtureBuilder()
                .WithId("route-adopt-store")
                .WithOrigin(Endpoint(500u, 50u))
                .WithStop(Stop(Endpoint(100u, 10u)))
                .WithAdoptedEndpointParts(Entry(100u, 10u, 100u, 101u, 110u))
                .Build();
            RouteStore.AddRoute(route);

            var parent = new ConfigNode("SCENARIO");
            RouteStore.SaveRoutesTo(parent);
            RouteStore.ResetForTesting();
            RouteStore.LoadRoutesFrom(parent);

            Assert.Single(RouteStore.CommittedRoutes);
            Route loaded = RouteStore.CommittedRoutes[0];
            Assert.NotSame(route, loaded);
            Assert.Equal(new HashSet<uint> { 100u, 101u, 110u },
                RouteEndpointPartAdoption.FindAdoptedPartFlightIds(loaded, Endpoint(100u, 10u)));

            // The read-only view the in-session reconcile and the rewind exits read carries it too.
            List<Route> saved = RouteStore.ReadSavedCommittedRoutes(parent);
            Assert.Single(saved);
            Assert.NotNull(saved[0].AdoptedEndpointParts);
        }
    }
}
