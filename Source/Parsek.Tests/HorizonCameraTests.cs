using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    public class HorizonCameraTests
    {
        #region ShouldAutoHorizonLock

        [Fact]
        public void ShouldAutoHorizonLock_AtmosphericBody_BelowAtmosphere_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 30000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_AtmosphericBody_AboveAtmosphere_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 80000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_AtmosphericBody_AtThreshold_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 70000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_AirlessBody_Below50km_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: false, atmosphereDepth: 0, altitude: 10000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_AirlessBody_Above50km_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: false, atmosphereDepth: 0, altitude: 60000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_AirlessBody_AtThreshold_ReturnsFalse()
        {
            Assert.False(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: false, atmosphereDepth: 0, altitude: 50000));
        }

        [Fact]
        public void ShouldAutoHorizonLock_ZeroAltitude_ReturnsTrue()
        {
            Assert.True(ParsekFlight.ShouldAutoHorizonLock(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 0));
        }

        #endregion

        #region ComputeHorizonForward

        [Fact]
        public void ComputeHorizonForward_NormalVelocity_ForwardOnHorizon()
        {
            Vector3 up = Vector3.up;
            Vector3 velocity = new Vector3(100, 0, 0); // moving east

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, Vector3.forward);

            // Forward should point east (velocity is already on horizon)
            Assert.True(Vector3.Dot(forward, Vector3.right) > 0.99f,
                $"Forward should point east, got {forward}");
        }

        [Fact]
        public void ComputeHorizonForward_VelocityWithVerticalComponent_ProjectsToHorizon()
        {
            Vector3 up = Vector3.up;
            Vector3 velocity = new Vector3(100, 50, 0); // ascending east

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, Vector3.forward);

            // Forward should be on horizon plane (Y component ~0)
            Assert.True(Mathf.Abs(forward.y) < 0.01f,
                $"Forward should be on horizon (Y=0), got Y={forward.y}");
            Assert.True(forward.x > 0.99f,
                $"Forward should point east after projection, got {forward}");
        }

        [Fact]
        public void ComputeHorizonForward_NearZeroVelocity_FallsBackToLastForward()
        {
            Vector3 up = Vector3.up;
            Vector3 velocity = new Vector3(0.001f, 0, 0); // nearly stopped
            Vector3 lastForward = Vector3.forward;         // was heading north

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, lastForward);

            // Should fall back to lastForward (north)
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f,
                $"Should fall back to lastForward (north), got {forward}");
        }

        [Fact]
        public void ComputeHorizonForward_ZeroVelocityAndZeroLastForward_PicksArbitrary()
        {
            Vector3 up = Vector3.up;
            Vector3 velocity = Vector3.zero;
            Vector3 lastForward = Vector3.zero;

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, lastForward);

            // Should pick some valid forward (not zero, perpendicular to up)
            Assert.True(forward.sqrMagnitude > 0.9f,
                $"Forward should be normalized, got magnitude {forward.magnitude}");
            Assert.True(Mathf.Abs(Vector3.Dot(forward, up)) < 0.01f,
                $"Forward should be perpendicular to up, got dot={Vector3.Dot(forward, up)}");
        }

        [Fact]
        public void ComputeHorizonForward_ArbitraryUpAxis_OutputPerpendicular()
        {
            Vector3 up = new Vector3(0.5f, 0.7f, 0.5f).normalized;
            Vector3 velocity = new Vector3(10, 5, -3);

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, Vector3.forward);

            // Forward should be perpendicular to up
            float dot = Mathf.Abs(Vector3.Dot(forward, up));
            Assert.True(dot < 0.01f,
                $"Forward should be perpendicular to up, got dot={dot}");
            Assert.True(Mathf.Abs(forward.magnitude - 1f) < 0.01f,
                $"Forward should be normalized, got magnitude={forward.magnitude}");
        }

        [Fact]
        public void ComputeHorizonForward_UpAlignedWithRight_FallbackWorks()
        {
            // Edge case: up = (1,0,0), Cross(up, right) = zero
            Vector3 up = Vector3.right;
            Vector3 velocity = Vector3.zero;
            Vector3 lastForward = Vector3.zero;

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, lastForward);

            // Should produce valid forward via Cross(up, forward) fallback
            Assert.True(forward.sqrMagnitude > 0.9f,
                $"Forward should be valid even when up=right, got {forward}");
            Assert.True(Mathf.Abs(Vector3.Dot(forward, up)) < 0.01f,
                $"Forward should be perpendicular to up, got dot={Vector3.Dot(forward, up)}");
        }

        [Fact]
        public void ComputeHorizonForward_PureVerticalVelocity_FallsBackToLastForward()
        {
            // Velocity pointing straight up — projection onto horizon = zero
            Vector3 up = Vector3.up;
            Vector3 velocity = new Vector3(0, 200, 0);
            Vector3 lastForward = new Vector3(0, 0, 1);

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, lastForward);

            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f,
                $"Should fall back to lastForward when velocity is pure vertical, got {forward}");
        }

        #endregion

        // Note: ComputeHorizonRotation and CompensateCameraAngles depend on
        // Quaternion.LookRotation / Quaternion.Euler / Quaternion.Inverse which are
        // native Unity methods — tested via in-game tests in RuntimeTests.cs.
    }
}
