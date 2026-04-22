using Xunit;

namespace Parsek.Tests
{
    public class ParsekFlightExplosionAnchorTests
    {
        [Fact]
        public void ResolveExplosionAnchorBodyName_PrefersLastInterpolatedBody()
        {
            var traj = new MockTrajectory
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 0,
                    longitude = 0,
                    altitude = 0
                }
            };
            traj.Points.Add(new TrajectoryPoint { bodyName = "Minmus" });

            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                "Kerbin", traj);

            Assert.Equal("Kerbin", bodyName);
        }

        [Fact]
        public void ResolveExplosionAnchorBodyName_FallsBackToLastPointBeforeSurfacePosition()
        {
            var traj = new MockTrajectory
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 0,
                    longitude = 0,
                    altitude = 0
                }
            };
            traj.Points.Add(new TrajectoryPoint { bodyName = "Minmus" });

            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                null, traj);

            Assert.Equal("Minmus", bodyName);
        }

        [Fact]
        public void ResolveExplosionAnchorBodyName_FallsBackToPersistedEndpointDecision()
        {
            var traj = new MockTrajectory
            {
                EndpointPhase = RecordingEndpointPhase.SurfacePosition,
                EndpointBodyName = "Eve",
                SurfacePos = new SurfacePosition
                {
                    body = "Mun",
                    latitude = 0,
                    longitude = 0,
                    altitude = 0
                }
            };
            traj.Points.Add(new TrajectoryPoint { bodyName = "" });

            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                null, traj);

            Assert.Equal("Eve", bodyName);
        }

        [Fact]
        public void ResolveExplosionAnchorBodyName_FallsBackToSurfacePositionBody()
        {
            var traj = new MockTrajectory
            {
                SurfacePos = new SurfacePosition
                {
                    body = "Duna",
                    latitude = 0,
                    longitude = 0,
                    altitude = 0
                }
            };

            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                null, traj);

            Assert.Equal("Duna", bodyName);
        }

        [Fact]
        public void ResolveExplosionAnchorBodyName_NoPlaybackContext_ReturnsNull()
        {
            string bodyName = ParsekFlight.ResolveExplosionAnchorBodyName(
                null, traj: null);

            Assert.Null(bodyName);
        }
    }
}
