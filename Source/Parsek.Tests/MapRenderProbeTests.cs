using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the pure Tier-C anomaly predicates folded into
    /// <see cref="MapRenderTrace"/> from the render-state probe prototype:
    /// <see cref="MapRenderTrace.IsIconJump"/> (orbit-derived jump threshold with
    /// the fixed floor, floating-origin shift suppression, and just-reset
    /// suppression) and <see cref="MapRenderTrace.IsLineBlink"/> (toggle within N
    /// frames). These predicates are Unity-ECall-free: they take primitives only,
    /// so the JIT verifier never walks a Unity native here. No shared static state
    /// is touched, but the class is kept in the Sequential collection for
    /// consistency with the sibling <see cref="MapRenderTraceTests"/>.
    /// </summary>
    [Collection("Sequential")]
    public class MapRenderProbeTests
    {
        private const int FrameNoFloatingOrigin = int.MinValue;

        // ---- IsIconJump: fixed floor when there is no orbit-derived motion ----

        [Fact]
        public void IsIconJump_BelowFloor_NoJump()
        {
            // Just under the 1000 km floor with zero expected motion.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters - 1.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_AboveFloor_Jump()
        {
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters + 1.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.True(jump);
        }

        // ---- IsIconJump: orbit-derived threshold overrides the floor on a
        // fast orbit so a delta below the orbit-derived threshold is not a jump
        // even though it exceeds the floor ----

        [Fact]
        public void IsIconJump_FastOrbit_BelowOrbitDerivedThreshold_NoJump()
        {
            // expected motion 2,000,000 m/frame -> threshold = 2e6 * 4 = 8e6.
            // dPos 5e6 exceeds the floor (1e6) but is below the orbit-derived
            // threshold, so it is normal fast-warp coast, not a teleport.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 5_000_000.0,
                expectedMotionMeters: 2_000_000.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_FastOrbit_AboveOrbitDerivedThreshold_Jump()
        {
            // dPos 9e6 exceeds the orbit-derived threshold (8e6): a real teleport.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 9_000_000.0,
                expectedMotionMeters: 2_000_000.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.True(jump);
        }

        // ---- IsIconJump: floating-origin shift-frame suppression ----

        [Fact]
        public void IsIconJump_OnFloatingOriginShiftFrame_Suppressed()
        {
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 50_000_000.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: 1000,
                justReset: false);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_OneFrameAfterFloatingOriginShift_StillSuppressed()
        {
            // Within FloatingOriginSuppressionFrameWindow (1) frame of slack.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 50_000_000.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1001,
                floatingOriginShiftFrame: 1000,
                justReset: false);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_TwoFramesAfterFloatingOriginShift_NotSuppressed()
        {
            // Beyond the suppression window: a real teleport flags again.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 50_000_000.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1002,
                floatingOriginShiftFrame: 1000,
                justReset: false);

            Assert.True(jump);
        }

        // ---- IsIconJump: just-reset suppression (no trustworthy prev pos) ----

        [Fact]
        public void IsIconJump_JustReset_Suppressed()
        {
            // A huge delta on the first frame after a per-pid state reset must
            // NOT fire: a stale prevWorldPos carried across a scene transition
            // would otherwise produce a spurious jump.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 50_000_000.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: true);

            Assert.False(jump);
        }

        // ---- IsIconJump: NaN / infinity guards ----

        [Fact]
        public void IsIconJump_NaNDelta_NoJump()
        {
            bool jump = MapRenderTrace.IsIconJump(
                dPos: double.NaN,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_NaNExpectedMotion_FallsBackToFloor()
        {
            // NaN expected motion degrades to the fixed floor, so a delta above
            // the floor still flags.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters + 1.0,
                expectedMotionMeters: double.NaN,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false);

            Assert.True(jump);
        }

        // ---- IsLineBlink ----

        [Fact]
        public void IsLineBlink_NoToggle_NotBlink()
        {
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: false,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1001);

            Assert.False(blink);
        }

        [Fact]
        public void IsLineBlink_FirstToggleEver_NotBlink()
        {
            // The first observed toggle for a pid is recorded but not reported.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: false,
                lastToggleFrame: 0,
                currentFrame: 1000);

            Assert.False(blink);
        }

        [Fact]
        public void IsLineBlink_ToggleWithinWindow_Blink()
        {
            // Toggled again 2 frames after the previous toggle: a flicker.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1002);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_ToggleAtWindowEdge_Blink()
        {
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1000 + MapRenderTrace.LineBlinkFrameWindow);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_ToggleBeyondWindow_NotBlink()
        {
            // The line turned off and stayed off for many frames, then back on:
            // a steady transition, not a blink.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1000 + MapRenderTrace.LineBlinkFrameWindow + 1);

            Assert.False(blink);
        }

        [Fact]
        public void IsLineBlink_NegativeSinceLast_NotBlink()
        {
            // Defensive: a lastToggleFrame in the future (frame counter reset)
            // must not report a blink.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1005,
                currentFrame: 1000);

            Assert.False(blink);
        }
    }
}
