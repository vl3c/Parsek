using System.Collections.Generic;
using Parsek.Logistics;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Exercises the pure surface-fallback helper. Production <c>Vessel</c>
    /// objects are not constructed here — every test builds
    /// <see cref="RouteEndpointResolver.SurfaceVesselSnapshot"/> records
    /// directly with a synthetic world position.
    /// </summary>
    [Collection("Sequential")]
    public class RouteEndpointResolverTests
    {
        private const string Body = "Kerbin";

        /// <summary>
        /// Make a snapshot at <paramref name="position"/>. <c>Vessel</c> is null
        /// in pure-test mode — the resolver still returns the snapshot via the
        /// <c>vessel</c> out parameter (which will be null), so we verify the
        /// match by re-checking the index/position rather than via reference.
        /// </summary>
        private static RouteEndpointResolver.SurfaceVesselSnapshot Snap(
            uint pid,
            string body,
            Vessel.Situations situation,
            Vector3d position)
        {
            return new RouteEndpointResolver.SurfaceVesselSnapshot
            {
                PersistentId = pid,
                BodyName = body,
                Situation = situation,
                WorldPosition = position,
                Vessel = null,
            };
        }

        // catches: a fallback that returns an arbitrary snapshot instead of the closest.
        [Fact]
        public void Surface_FallbackPicksClosestInRadius()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            const uint closestPid = 777u;
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(1u,          Body, Vessel.Situations.LANDED, new Vector3d(500, 0, 0)),
                Snap(closestPid,  Body, Vessel.Situations.LANDED, new Vector3d(100, 0, 0)), // closest
                Snap(3u,          Body, Vessel.Situations.LANDED, new Vector3d(1500, 0, 0)),
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out Vessel _, out uint pickedPid, out string reason);

            Assert.True(ok, reason);
            Assert.Equal(string.Empty, reason);
            // Pin the closest snapshot's PID, not just "some hit". A regression
            // that picked the farthest in-radius candidate would still pass an
            // ok==true assertion alone.
            Assert.Equal(closestPid, pickedPid);
        }

        // catches: wrong-body matches contaminating proximity ranking.
        [Fact]
        public void Surface_FallbackExcludesWrongBody()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            const uint wrongBodyPid = 1u;
            const uint rightBodyPid = 2u;
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(wrongBodyPid, "Mun",   Vessel.Situations.LANDED, new Vector3d(10, 0, 0)),
                Snap(rightBodyPid, Body,    Vessel.Situations.LANDED, new Vector3d(500, 0, 0)),
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out _, out uint pickedPid, out string reason);

            // Only the Body=="Kerbin" candidate (distance 500) should be considered.
            Assert.True(ok, reason);
            Assert.Equal(rightBodyPid, pickedPid);
        }

        // catches: ghost vessels surfacing as the closest match — they must be excluded.
        [Fact]
        public void Surface_FallbackExcludesGhostPids()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            const uint ghostPid = 100u;
            const uint realPid = 200u;
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(ghostPid, Body, Vessel.Situations.LANDED, new Vector3d(100, 0, 0)),  // closest, but is a ghost
                Snap(realPid,  Body, Vessel.Situations.LANDED, new Vector3d(500, 0, 0)),  // next-closest, real
            };
            var ghostPids = new HashSet<uint> { ghostPid };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: ghostPids,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out _, out uint pickedPid, out string reason);

            Assert.True(ok, reason);
            // Ghost was at distance 100; the real candidate at 500 is within the 500 m radius (boundary-inclusive).
            Assert.Equal(realPid, pickedPid);
        }

        // catches: a fallback that succeeds even when all candidates are outside the radius.
        [Fact]
        public void Surface_FallbackBeyondRadius_Fails()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(1u, Body, Vessel.Situations.LANDED, new Vector3d(3000, 0, 0)),
                Snap(2u, Body, Vessel.Situations.LANDED, new Vector3d(5000, 0, 0)),
                Snap(3u, Body, Vessel.Situations.LANDED, new Vector3d(10000, 0, 0)),
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out _, out uint pickedPid, out string reason);

            Assert.False(ok);
            Assert.Equal("no-vessel-within-radius", reason);
            Assert.Equal(0u, pickedPid);
        }

        // catches: ORBITING vessels passing the surface filter.
        [Fact]
        public void Surface_FallbackWrongSituation_Excluded()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            // Both candidates are non-surface; neither should be picked. Distinct
            // PIDs let us assert pickedPid==0 unambiguously.
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>
            {
                Snap(11u, Body, Vessel.Situations.ORBITING, new Vector3d(100, 0, 0)),
                Snap(22u, Body, Vessel.Situations.FLYING,   new Vector3d(200, 0, 0)),
            };

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out _, out uint pickedPid, out string reason);

            Assert.False(ok);
            Assert.Equal("no-surface-candidate", reason);
            Assert.Equal(0u, pickedPid);
        }

        // catches: a crash on an empty live-vessel list.
        [Fact]
        public void Surface_NoVessels_Fails()
        {
            Vector3d endpointPos = new Vector3d(0, 0, 0);
            var snapshots = new List<RouteEndpointResolver.SurfaceVesselSnapshot>();

            bool ok = RouteEndpointResolver.TrySurfaceFallbackPure(
                endpointPos, Body, snapshots, excludePids: null,
                radiusMeters: RouteOrchestrator.SurfaceProximityRadiusMeters,
                out _, out uint pickedPid, out string reason);

            Assert.False(ok);
            Assert.Equal("no-live-vessels", reason);
            Assert.Equal(0u, pickedPid);
        }
    }
}
