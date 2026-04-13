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

        [Fact]
        public void ComputeHorizonForward_LastForwardAlignedWithUp_FallsToArbitrary()
        {
            // lastForward parallel to up — its horizon projection is zero,
            // should trigger the arbitrary perpendicular fallback
            Vector3 up = Vector3.up;
            Vector3 velocity = Vector3.zero;
            Vector3 lastForward = Vector3.up; // aligned with up

            Vector3 forward = ParsekFlight.ComputeHorizonForward(up, velocity, lastForward);

            Assert.True(forward.sqrMagnitude > 0.9f,
                $"Forward should be valid, got magnitude {forward.magnitude}");
            Assert.True(Mathf.Abs(Vector3.Dot(forward, up)) < 0.01f,
                $"Forward should be perpendicular to up, got dot={Vector3.Dot(forward, up)}");
        }

        [Fact]
        public void ComputeSurfaceRelativeVelocity_SubtractsBodyRotation()
        {
            Vector3 playbackVelocity = new Vector3(120f, 0f, 0f);
            Vector3 rotatingFrameVelocity = new Vector3(200f, 0f, 0f);

            Vector3 surfaceVelocity = ParsekFlight.ComputeSurfaceRelativeVelocity(
                playbackVelocity, rotatingFrameVelocity);

            Assert.True(Vector3.Dot(surfaceVelocity, Vector3.left) > 79.9f,
                $"Surface-relative velocity should point west, got {surfaceVelocity}");
        }

        [Fact]
        public void ShouldUseSurfaceRelativeWatchHeading_InAtmosphere_True()
        {
            Assert.True(ParsekFlight.ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 15000));
        }

        [Fact]
        public void ShouldUseSurfaceRelativeWatchHeading_AboveAtmosphere_False()
        {
            Assert.False(ParsekFlight.ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere: true, atmosphereDepth: 70000, altitude: 71000));
        }

        [Fact]
        public void ShouldUseSurfaceRelativeWatchHeading_AirlessBody_False()
        {
            Assert.False(ParsekFlight.ShouldUseSurfaceRelativeWatchHeading(
                hasAtmosphere: false, atmosphereDepth: 0, altitude: 1000));
        }

        [Fact]
        public void ComputeWatchHorizonBasis_InAtmosphere_BodyRotationDominates_FollowsSurfaceRelativePrograde()
        {
            Vector3 up = Vector3.up;
            Vector3 playbackVelocity = new Vector3(120f, 0f, 0f);       // inertial east
            Vector3 rotatingFrameVelocity = new Vector3(200f, 0f, 0f);  // body rotates east faster

            var (forward, horizonVelocity, headingVelocity, appliedFrameVelocity, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, altitude: 10000,
                    up, playbackVelocity, rotatingFrameVelocity, Vector3.forward);

            Assert.Equal(HorizonForwardSource.ProjectedVelocity, source);
            Assert.True(Vector3.Dot(appliedFrameVelocity.normalized, Vector3.right) > 0.99f,
                $"Applied frame velocity should match body rotation, got {appliedFrameVelocity}");
            Assert.True(Vector3.Dot(headingVelocity.normalized, Vector3.left) > 0.99f,
                $"Heading velocity should point west, got {headingVelocity}");
            Assert.True(Vector3.Dot(horizonVelocity.normalized, Vector3.left) > 0.99f,
                $"Projected horizon velocity should point west, got {horizonVelocity}");
            Assert.True(Vector3.Dot(forward, Vector3.left) > 0.99f,
                $"Forward should follow surface-relative prograde, got {forward}");
        }

        [Fact]
        public void ComputeWatchHorizonBasis_AboveAtmosphere_PreservesPlaybackHeading()
        {
            Vector3 up = Vector3.up;
            Vector3 playbackVelocity = new Vector3(120f, 0f, 0f);
            Vector3 rotatingFrameVelocity = new Vector3(200f, 0f, 0f);

            var (forward, horizonVelocity, headingVelocity, appliedFrameVelocity, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, altitude: 71000,
                    up, playbackVelocity, rotatingFrameVelocity, Vector3.forward);

            Assert.Equal(HorizonForwardSource.ProjectedVelocity, source);
            Assert.True(appliedFrameVelocity.sqrMagnitude < 0.0001f,
                $"Above atmosphere should preserve playback heading, got {appliedFrameVelocity}");
            Assert.True(Vector3.Dot(headingVelocity.normalized, Vector3.right) > 0.99f,
                $"Heading velocity should preserve the raw playback direction, got {headingVelocity}");
            Assert.True(Vector3.Dot(horizonVelocity.normalized, Vector3.right) > 0.99f,
                $"Projected horizon velocity should preserve the raw playback direction, got {horizonVelocity}");
            Assert.True(Vector3.Dot(forward, Vector3.right) > 0.99f,
                $"Forward should preserve the raw playback direction, got {forward}");
        }

        [Fact]
        public void ComputeWatchHorizonBasis_InAtmosphere_HeadingVelocityCancels_FallsBackToLastForward()
        {
            Vector3 up = Vector3.up;
            Vector3 playbackVelocity = new Vector3(175f, 0f, 0f);
            Vector3 rotatingFrameVelocity = new Vector3(175f, 0f, 0f);
            Vector3 lastForward = Vector3.forward;

            var (forward, horizonVelocity, headingVelocity, appliedFrameVelocity, source) =
                ParsekFlight.ComputeWatchHorizonBasis(
                    hasAtmosphere: true, atmosphereDepth: 70000, altitude: 10000,
                    up, playbackVelocity, rotatingFrameVelocity, lastForward);

            Assert.Equal(HorizonForwardSource.LastForwardFallback, source);
            Assert.True(Vector3.Dot(appliedFrameVelocity.normalized, Vector3.right) > 0.99f,
                $"Applied frame velocity should use the body's rotation, got {appliedFrameVelocity}");
            Assert.True(headingVelocity.sqrMagnitude < 0.0001f,
                $"Heading velocity should cancel out, got {headingVelocity}");
            Assert.True(horizonVelocity.sqrMagnitude < 0.0001f,
                $"Projected horizon velocity should be near zero, got {horizonVelocity}");
            Assert.True(Vector3.Dot(forward, Vector3.forward) > 0.99f,
                $"Forward should fall back to lastForward, got {forward}");
        }

        #endregion

        // Note: ComputeHorizonRotation and CompensateCameraAngles depend on
        // Quaternion.LookRotation / Quaternion.Euler / Quaternion.Inverse which are
        // native Unity methods — tested via in-game tests in RuntimeTests.cs.
    }
}
