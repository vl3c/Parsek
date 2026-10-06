using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// ROUTE-ENDPOINT-CHAIN-GHOST-PROXIMITY-REBIND: after a rewind the Ghost Chain Rule
    /// despawns a base a committed dock claims, the resolver misses its root-part and pid
    /// steps, and the surface-proximity step used to REBIND the route (persisted) to a craft
    /// parked within 500 m - permanently, because the respawned base then loses to the
    /// neighbour's root part at the first resolver step. <see cref="RouteEndpointChainHold"/>
    /// holds the endpoint instead while it is chain-ghosted.
    ///
    /// <para>The mirror direction is pinned too: a genuinely lost endpoint (its chain is
    /// terminated - destroyed or recovered in the committed future - or no chain claims it)
    /// is NOT held and still transfers to a nearby craft as before.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointChainHoldTests : IDisposable
    {
        private const uint BasePid = 4242u;
        private const string BaseGuid = "aaaaaaaabbbbccccddddeeeeeeeeeeee";
        private const string OtherLaunchGuid = "11111111222233334444555555555555";

        public RouteEndpointChainHoldTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RouteEndpoint BaseEndpoint(string guid = BaseGuid, uint pid = BasePid)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = pid,
                RootPartUId = 5000u,
                LaunchGuid = guid,
                BodyName = "Mun",
                IsSurface = true,
            };
        }

        private static RouteEndpointChainHold.ChainClaim Claim(
            uint pid = BasePid, string guid = BaseGuid, double spawnUT = 2000.0,
            bool terminated = false, bool pending = false)
        {
            return new RouteEndpointChainHold.ChainClaim
            {
                VesselPid = pid,
                LaunchGuid = guid,
                SpawnUT = spawnUT,
                IsTerminated = terminated,
                SpawnPending = pending,
                TipRecordingId = "tip",
            };
        }

        private static bool Held(RouteEndpoint endpoint, double now, params RouteEndpointChainHold.ChainClaim[] claims)
        {
            return RouteEndpointChainHold.IsHeldByGhostChain(
                endpoint, new List<RouteEndpointChainHold.ChainClaim>(claims), now, out _);
        }

        // catches: the defect - a base hidden by a future chain tip falling through to the
        // proximity step and being rebound to a neighbour.
        [Fact]
        public void ChainGhostedBase_TipStillAhead_IsHeld()
        {
            Assert.True(RouteEndpointChainHold.IsHeldByGhostChain(
                BaseEndpoint(), new List<RouteEndpointChainHold.ChainClaim> { Claim() },
                1000.0, out RouteEndpointChainHold.ChainClaim matched));
            Assert.Equal(BasePid, matched.VesselPid);
            Assert.Equal(2000.0, matched.SpawnUT);
        }

        // catches: the flight's blocked chain tip (spawn UT passed, the spawn refused by a
        // collision - possibly with the very neighbour proximity would pick) not holding.
        [Fact]
        public void PendingTipPastSpawnUT_IsHeld()
        {
            Assert.True(Held(BaseEndpoint(), 2500.0, Claim(pending: true)));
        }

        // Mirror: a base destroyed or recovered in the committed future is genuinely lost,
        // and its route keeps the transfer-to-a-nearby-craft behaviour.
        [Fact]
        public void TerminatedChain_IsNotHeld()
        {
            Assert.False(Held(BaseEndpoint(), 1000.0, Claim(terminated: true)));
            Assert.False(Held(BaseEndpoint(), 1000.0, Claim(terminated: true, pending: true)));
        }

        // Mirror: an endpoint no chain claims (destroyed in live play) is not held.
        [Fact]
        public void NoClaimForTheEndpoint_IsNotHeld()
        {
            Assert.False(Held(BaseEndpoint(), 1000.0));
            Assert.False(Held(BaseEndpoint(), 1000.0, Claim(pid: 9999u)));
            Assert.False(RouteEndpointChainHold.IsHeldByGhostChain(BaseEndpoint(), null, 1000.0, out _));
        }

        // Once the tip spawn UT has passed and the flight is not waiting on it, the base is
        // back (or will be by identity); a miss then is a real loss, not a hold.
        [Fact]
        public void TipSpawnUTPassed_NotPending_IsNotHeld()
        {
            Assert.False(Held(BaseEndpoint(), 2000.0, Claim()));
            Assert.False(Held(BaseEndpoint(), 3000.0, Claim()));
        }

        // catches: a craft-baked pid shared by a DIFFERENT launch of the same craft holding
        // the route; an unknown guid on either side is no evidence and still holds.
        [Fact]
        public void LaunchGuidGate()
        {
            Assert.False(Held(BaseEndpoint(), 1000.0, Claim(guid: OtherLaunchGuid)));
            Assert.True(Held(BaseEndpoint(guid: null), 1000.0, Claim(guid: OtherLaunchGuid)));
            Assert.True(Held(BaseEndpoint(), 1000.0, Claim(guid: null)));
        }

        // An endpoint with no pid cannot be matched to a chain (chains are keyed by pid).
        [Fact]
        public void EndpointWithoutPid_IsNotHeld()
        {
            Assert.False(Held(BaseEndpoint(pid: 0u), 1000.0, Claim(pid: 0u)));
        }

        [Fact]
        public void BuildClaims_MergesWalkerAndFlightPending()
        {
            var walker = new List<GhostChain>
            {
                new GhostChain { OriginalVesselPid = 1u, LaunchGuid = "g1", SpawnUT = 100.0, TipRecordingId = "t1" },
                new GhostChain { OriginalVesselPid = 2u, SpawnUT = 200.0, IsTerminated = true },
            };
            var flight = new List<GhostChain>
            {
                new GhostChain { OriginalVesselPid = 1u, LaunchGuid = "g1", SpawnUT = 100.0, TipRecordingId = "t1" },
                new GhostChain { OriginalVesselPid = 3u, SpawnUT = 300.0 },
            };

            List<RouteEndpointChainHold.ChainClaim> claims = RouteEndpointChainHold.BuildClaims(walker, flight);

            Assert.Equal(3, claims.Count);
            RouteEndpointChainHold.ChainClaim one = claims.Find(c => c.VesselPid == 1u);
            Assert.True(one.SpawnPending);
            Assert.Equal("g1", one.LaunchGuid);
            Assert.Equal("t1", one.TipRecordingId);
            RouteEndpointChainHold.ChainClaim two = claims.Find(c => c.VesselPid == 2u);
            Assert.True(two.IsTerminated);
            Assert.False(two.SpawnPending);
            Assert.True(claims.Find(c => c.VesselPid == 3u).SpawnPending);

            Assert.Empty(RouteEndpointChainHold.BuildClaims(null, null));
        }
    }
}
