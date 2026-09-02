using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// The ROOT-PART identity step in <see cref="RouteEndpointResolver"/>, and why it has to
    /// run BEFORE proximity.
    ///
    /// <para>A start-docked origin carries no vessel pid at all - the depot's own
    /// <c>Vessel</c> is destroyed by <c>Part.Couple</c> at the dock, so there is nothing to
    /// stamp. Without an identity step the origin falls straight through to the surface
    /// proximity fallback and resolves to whatever vessel is nearest the recorded
    /// coordinates, which for a shuttle route is routinely the TRANSPORT parked back at the
    /// depot: the exact "deducts from the origin depot, NOT the transport" failure the route
    /// design doc names.</para>
    ///
    /// <para>The key is a part <c>flightID</c>: assigned per launch, never written into the
    /// <c>.craft</c>, so it is launch-unique and needs no guid gate. It survives the split
    /// that gives the depot its own vessel back - <c>Part.Undock(newVesselInfo)</c> resolves
    /// <c>this.vessel[newVesselInfo.rootPartUId]</c>, calls <c>SetHierarchyRoot</c> on it and
    /// builds the new <c>Vessel</c> on that part (decompiled KSP 1.12.5) - while assigning
    /// <c>vessel.id = Guid.NewGuid()</c>, which is why a launch-guid match would be wrong.</para>
    /// </summary>
    public class RouteEndpointRootIdResolutionTests
    {
        private static RouteEndpointResolver.RootIdVesselSnapshot Snap(uint pid, uint rootFlightId)
        {
            return new RouteEndpointResolver.RootIdVesselSnapshot
            {
                PersistentId = pid,
                RootPartFlightId = rootFlightId,
                Vessel = null,
            };
        }

        [Fact]
        public void RootMatch_FindsTheVesselCarryingThatRootPart()
        {
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 111u, rootFlightId: 900u),
                Snap(pid: 222u, rootFlightId: 4242u),
                Snap(pid: 333u, rootFlightId: 901u),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, snapshots, null, out _, out uint pickedPid, out string reason));
            Assert.Equal(222u, pickedPid);
            Assert.Equal(string.Empty, reason);
        }

        [Fact]
        public void RootMatch_IsNotPositional_ADistantDepotStillWins()
        {
            // THE WHOLE POINT OF THE STEP. The snapshot list is deliberately unfiltered by
            // body or situation: identity does not depend on where the vessel is, so an
            // ORBITING depot - which can never reach the surface-proximity fallback at all -
            // resolves here, and a nearer vessel has no way to outrank it because distance
            // is not an input to this step.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 111u, rootFlightId: 555u),   // the transport, parked at the depot
                Snap(pid: 222u, rootFlightId: 4242u),  // the depot itself
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, snapshots, null, out _, out uint pickedPid, out _));
            Assert.Equal(222u, pickedPid);
        }

        [Fact]
        public void NoRootMatch_FallsThroughSoProximityCanRun()
        {
            // The step must MISS cleanly rather than fail the resolution: a recovered or
            // rebuilt depot has a new root part, and the coordinate-based fallback is what
            // catches it.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 111u, rootFlightId: 900u),
            };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, snapshots, null, out Vessel vessel, out uint pickedPid, out string reason));
            Assert.Null(vessel);
            Assert.Equal(0u, pickedPid);
            Assert.Equal("no-root-match", reason);
        }

        [Fact]
        public void ZeroRootId_SkipsTheStepEntirely()
        {
            // FAILS IF: 0 is treated as a value. It is the "unknown" sentinel on BOTH sides -
            // a KSC origin, a pre-2026-09-02 route, and a snapshot whose root id could not be
            // read all carry 0 - so matching it would pair every unknown with every other
            // unknown and hand back an arbitrary vessel.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 111u, rootFlightId: 0u),
            };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                0u, snapshots, null, out _, out _, out string reason));
            Assert.Equal("root-id-unknown", reason);
        }

        [Fact]
        public void GhostMapVessels_AreExcluded()
        {
            // Same exclusion the pid and proximity steps apply: a ghost's ProtoVessel is a
            // Parsek-authored replica and must never be debited.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 222u, rootFlightId: 4242u),
            };

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, snapshots, new HashSet<uint> { 222u },
                out _, out _, out string reason));
            Assert.Equal("no-root-match", reason);
        }

        [Fact]
        public void TwoVesselsSharingARootFlightId_TakeTheFirstAndSaySo()
        {
            // Impossible in a healthy save; recorded rather than asserted-away so a log
            // names it if it ever happens.
            var snapshots = new List<RouteEndpointResolver.RootIdVesselSnapshot>
            {
                Snap(pid: 222u, rootFlightId: 4242u),
                Snap(pid: 333u, rootFlightId: 4242u),
            };

            Assert.True(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, snapshots, null, out _, out uint pickedPid, out string reason));
            Assert.Equal(222u, pickedPid);
            Assert.Equal("root-match-ambiguous", reason);
        }

        [Fact]
        public void EmptyOrNullSnapshots_MissCleanly()
        {
            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, new List<RouteEndpointResolver.RootIdVesselSnapshot>(), null,
                out _, out _, out string emptyReason));
            Assert.Equal("no-root-candidate", emptyReason);

            Assert.False(RouteEndpointResolver.TryRootPartMatchPure(
                4242u, null, null, out _, out _, out string nullReason));
            Assert.Equal("no-root-candidate", nullReason);
        }

        [Fact]
        public void RouteBuilderStampsTheRootIdOntoTheOriginEndpoint_AndItRoundTrips()
        {
            // FAILS IF: the identity is captured but never reaches Route.Origin, or is
            // dropped by the codec - either way the resolver's first step would be dead and
            // proximity would silently own every start-docked origin again.
            var endpoint = new RouteEndpoint
            {
                RootPartUId = 4242u,
                BodyName = "Mun",
                Latitude = 1.0,
                Longitude = 2.0,
                Altitude = 3.0,
                IsSurface = true,
            };

            var node = new ConfigNode("ORIGIN");
            RouteNodeCodec.SerializeEndpoint(node, endpoint, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal("4242", node.GetValue("rootPartUId"));

            RouteEndpoint restored = RouteCodec.DeserializeEndpointForTesting(node);
            Assert.Equal(4242u, restored.RootPartUId);
        }

        [Fact]
        public void EndpointWithoutARootId_OmitsTheKey_SoOldRoutesRoundTripByteIdentically()
        {
            var endpoint = new RouteEndpoint
            {
                VesselPersistentId = 99u,
                BodyName = "Kerbin",
                IsSurface = true,
            };
            var node = new ConfigNode("ORIGIN");
            RouteNodeCodec.SerializeEndpoint(node, endpoint, System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(node.HasValue("rootPartUId"));
        }
    }
}
