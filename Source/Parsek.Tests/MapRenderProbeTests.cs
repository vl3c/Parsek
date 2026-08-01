using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit coverage for the pure Tier-C anomaly predicates folded into
    /// <see cref="MapRenderTrace"/> from the render-state probe prototype:
    /// <see cref="MapRenderTrace.IsIconJump"/> (orbit-derived jump threshold with
    /// the fixed floor, floating-origin shift suppression, just-reset suppression,
    /// and reference-body-change suppression - the predicate's <c>dPos</c> is the
    /// body-relative orbit-frame delta the probe measures, not the raw world-frame
    /// delta) and <see cref="MapRenderTrace.IsLineBlink"/> (toggle within N
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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

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
                justReset: true,
                bodyChanged: false);

            Assert.False(jump);
        }

        // ---- IsIconJump: reference-body change (SOI crossing) suppression ----

        [Fact]
        public void IsIconJump_BodyChanged_Suppressed()
        {
            // The orbit's reference body changed this frame (e.g. SOI crossing
            // Kerbin -> Sun). The previous body-relative position was measured in
            // the OLD body's frame, so a huge cross-frame delta is a frame
            // mismatch, not a teleport, and must NOT fire.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 50_000_000.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: true);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_SameBody_AboveThreshold_Jump()
        {
            // Same reference body (bodyChanged false): a genuine off-orbit delta
            // above the floor still flags. Guards that the body-change suppression
            // does not blanket-disable the detector.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters + 1.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: false);

            Assert.True(jump);
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
                justReset: false,
                bodyChanged: false);

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
                justReset: false,
                bodyChanged: false);

            Assert.True(jump);
        }

        // ---- IsIconJump: suppression-lift edge suppression ----

        [Fact]
        public void IsIconJump_SuppressionLifted_Suppressed()
        {
            // The icon was SUPPRESSED on the previous sample (parked at a clamped endpoint while HIDDEN);
            // the first visible frame after suppression lifts re-propagates the proto to its live phase.
            // That delta was never on screen, so a huge dPos must NOT fire on the lift frame.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: 2_049_675_084.0, // the captured Sun suppression-snap magnitude
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: false,
                suppressionLifted: true);

            Assert.False(jump);
        }

        [Fact]
        public void IsIconJump_NotSuppressionLifted_AboveThreshold_StillJumps()
        {
            // Guard that the suppression-lift suppression does not blanket-disable the detector: a genuine
            // teleport on a normally-visible frame (suppressionLifted false) still flags.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters + 1.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: false,
                suppressionLifted: false);

            Assert.True(jump);
        }

        [Fact]
        public void IsIconJump_DefaultSuppressionLifted_PreservesLegacyBehavior()
        {
            // The new param defaults false, so a call that omits it is byte-identical to the pre-fix
            // predicate: a delta above the floor still flags.
            bool jump = MapRenderTrace.IsIconJump(
                dPos: MapRenderTrace.IconJumpFloorMeters + 1.0,
                expectedMotionMeters: 0.0,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: false);

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

        [Fact]
        public void IsLineBlink_WithinWindow_BodyChanged_NotBlink()
        {
            // Two toggles within the frame window but straddling a reference-body / segment change
            // (line off on a Kerbin escape hyperbola, on on the next Sun heliocentric leg): two
            // legitimate transitions at an SOI seam compressed by high warp, NOT a flicker.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1008, // 8 frames = within LineBlinkFrameWindow
                bodyChanged: true);

            Assert.False(blink);
        }

        [Fact]
        public void IsLineBlink_WithinWindow_SameBody_StillBlink()
        {
            // Guard that the bodyChanged guard does not blanket-disable the detector: a same-geometry
            // toggle out-and-back within the window (bodyChanged false) is still a blink.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1002,
                bodyChanged: false);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_DefaultBodyChanged_PreservesLegacyBehavior()
        {
            // The new param defaults false, so a call that omits it is byte-identical to the pre-fix
            // predicate: a toggle within the window is a blink.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1002);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_WithinWindow_OffWindowCovered_NotBlink()
        {
            // V1-REPLAY-LINE-BLINK: the dark window between the two toggles was covered end to end by
            // an actual polyline draw. The proto line hid because the polyline OWNED the leg - the
            // anti-double-draw invariant requires exactly that - so the user saw one continuous
            // trajectory. Two correct transitions compressed below the frame window by a 100x-1000x
            // rails ramp, not a flicker.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1004, // the shorter of the two measured V1 windows
                bodyChanged: false,
                offWindowCovered: true);

            Assert.False(blink);
        }

        [Fact]
        public void IsLineBlink_WithinWindow_OffWindowUncovered_StillBlink()
        {
            // The guard must NOT blanket-disable the detector: an UNCOVERED dark window is the case
            // worth gating - the proto line hidden while nothing drew, i.e. the map actually went dark.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1008, // the longer measured V1 window, at the frame-window edge
                bodyChanged: false,
                offWindowCovered: false);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_DefaultOffWindowCovered_PreservesLegacyBehavior()
        {
            // The new param defaults false, so a call that passes bodyChanged but omits
            // offWindowCovered is byte-identical to the pre-guard predicate.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1002,
                bodyChanged: false);

            Assert.True(blink);
        }

        [Fact]
        public void IsLineBlink_NoToggle_OffWindowCovered_NotBlink()
        {
            // Coverage never manufactures a raise: no toggle is still no blink.
            bool blink = MapRenderTrace.IsLineBlink(
                toggled: false,
                hasLastToggleFrame: true,
                lastToggleFrame: 1000,
                currentFrame: 1002,
                bodyChanged: false,
                offWindowCovered: true);

            Assert.False(blink);
        }

        // ---- NextOffWindowUncovered (dark-window coverage accounting) ----

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void NextOffWindowUncovered_LineLit_PassesVerdictThrough(bool wasUncovered)
        {
            // While the line is LIT there is no window accumulating: the just-ended window's verdict
            // must survive untouched so the off->on edge can read it (the probe clears it after).
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: false, lineWasOff: true, polylineOwns: false, wasUncovered: wasUncovered);

            Assert.Equal(wasUncovered, next);
        }

        [Fact]
        public void NextOffWindowUncovered_NewWindowStartsClean()
        {
            // A dark frame whose predecessor was LIT opens a NEW window. A stale poison from the
            // previous window must not leak forward and red an unrelated later handoff.
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: true, lineWasOff: false, polylineOwns: true, wasUncovered: true);

            Assert.False(next);
        }

        [Fact]
        public void NextOffWindowUncovered_FirstDarkFrameWithNoDraw_IsUncovered()
        {
            // A window that opens with nothing drawing is poisoned from its very first frame.
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: true, lineWasOff: false, polylineOwns: false, wasUncovered: false);

            Assert.True(next);
        }

        [Fact]
        public void NextOffWindowUncovered_CoveredWindowStaysCovered()
        {
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: true, lineWasOff: true, polylineOwns: true, wasUncovered: false);

            Assert.False(next);
        }

        [Fact]
        public void NextOffWindowUncovered_UncoveredFramePoisonsWindow()
        {
            // One dark frame with no owner is one dark frame the user saw.
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: true, lineWasOff: true, polylineOwns: false, wasUncovered: false);

            Assert.True(next);
        }

        [Fact]
        public void NextOffWindowUncovered_PoisonSurvivesLaterCoverage()
        {
            // Coverage arriving AFTER a dark frame cannot un-darken it: the verdict is sticky for the
            // rest of the window. Without this a 1-frame gap followed by a long covered leg would
            // silently pass.
            bool next = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: true, lineWasOff: true, polylineOwns: true, wasUncovered: true);

            Assert.True(next);
        }

        // ---- The two predicates together: the V1 dwell shape end to end ----

        /// <summary>
        /// Replays the probe's per-frame accounting exactly as <c>MapRenderProbe.Sample</c> does
        /// (accumulate through <see cref="MapRenderTrace.NextOffWindowUncovered"/>, then judge the
        /// off-&gt;on edge), over a lit -&gt; dark-window -&gt; lit sequence.
        /// <paramref name="ownsPerDarkFrame"/> is the polyline's actual-draw signal on each dark
        /// frame. Returns whether the re-activation edge raised <c>line-blink</c>.
        /// </summary>
        private static bool ReplayDarkWindow(params bool[] ownsPerDarkFrame)
        {
            bool uncovered = false;
            bool prevOff = false;          // the frame before the window: line lit
            int lastToggleFrame = 0;
            bool hasLastToggle = false;
            int frame = 1000;

            foreach (bool owns in ownsPerDarkFrame)
            {
                frame++;
                uncovered = MapRenderTrace.NextOffWindowUncovered(
                    lineIsOff: true, lineWasOff: prevOff, polylineOwns: owns, wasUncovered: uncovered);
                if (!prevOff) { lastToggleFrame = frame; hasLastToggle = true; } // lit -> dark toggle
                prevOff = true;
            }

            // The re-activation frame: line lit again, so the accounting passes the verdict through.
            frame++;
            uncovered = MapRenderTrace.NextOffWindowUncovered(
                lineIsOff: false, lineWasOff: prevOff, polylineOwns: false, wasUncovered: uncovered);

            return MapRenderTrace.IsLineBlink(
                toggled: true,
                hasLastToggleFrame: hasLastToggle,
                lastToggleFrame: lastToggleFrame,
                currentFrame: frame,
                bodyChanged: false,
                offWindowCovered: !uncovered);
        }

        [Fact]
        public void DarkWindow_FullyCoveredByPolyline_DoesNotRaise()
        {
            // The V1-REPLAY-LINE-BLINK shape: a 4-frame dark window (the first of the two measured
            // raises) in which the polyline drew the recording's ascent leg on every frame. By-design
            // ownership handoff - nothing on screen went dark - so no raise.
            Assert.False(ReplayDarkWindow(true, true, true, true));
        }

        [Fact]
        public void DarkWindow_WithOneUncoveredFrame_StillRaises()
        {
            // The same window with ONE frame where neither surface drew. That single frame is the
            // render defect (the classification-keyed proto suppress outrunning the actual draw), and
            // it must survive the guard.
            Assert.True(ReplayDarkWindow(true, false, true, true));
        }

        [Fact]
        public void DarkWindow_NothingDrewAtAll_StillRaises()
        {
            // The pure gap case: proto line hidden, polyline never drew. Unambiguously a raise.
            Assert.True(ReplayDarkWindow(false, false, false, false));
        }

        [Fact]
        public void DarkWindow_CoveredButBeyondFrameWindow_DoesNotRaise()
        {
            // A long covered window is not a blink either way (it exceeds LineBlinkFrameWindow), so the
            // guard and the frame window agree rather than fighting.
            var owns = new bool[MapRenderTrace.LineBlinkFrameWindow + 2];
            for (int i = 0; i < owns.Length; i++) owns[i] = true;

            Assert.False(ReplayDarkWindow(owns));
        }

        [Fact]
        public void DarkWindow_UncoveredWindowDoesNotPoisonTheNextCoveredOne()
        {
            // Two windows in ONE carried state sequence (no probe-side clear between them, so this
            // pins the pure function's own reset): an uncovered window, a lit frame, then a covered
            // window. The second edge must NOT raise - otherwise one real gap early in a dwell would
            // red every legitimate handoff after it.
            bool uncovered = false;

            // Window 1, frames 1001-1002: nothing drew.
            uncovered = MapRenderTrace.NextOffWindowUncovered(true, lineWasOff: false, polylineOwns: false, wasUncovered: uncovered);
            uncovered = MapRenderTrace.NextOffWindowUncovered(true, lineWasOff: true, polylineOwns: false, wasUncovered: uncovered);
            // Frame 1003: lit again - the edge that consumes window 1's verdict.
            uncovered = MapRenderTrace.NextOffWindowUncovered(false, lineWasOff: true, polylineOwns: false, wasUncovered: uncovered);
            Assert.True(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true, lastToggleFrame: 1001, currentFrame: 1003,
                bodyChanged: false, offWindowCovered: !uncovered));

            // Window 2, frames 1004-1005: the polyline drew throughout. lineWasOff is false on the
            // first dark frame, which is what wipes window 1's verdict.
            uncovered = MapRenderTrace.NextOffWindowUncovered(true, lineWasOff: false, polylineOwns: true, wasUncovered: uncovered);
            uncovered = MapRenderTrace.NextOffWindowUncovered(true, lineWasOff: true, polylineOwns: true, wasUncovered: uncovered);
            uncovered = MapRenderTrace.NextOffWindowUncovered(false, lineWasOff: true, polylineOwns: false, wasUncovered: uncovered);
            Assert.False(MapRenderTrace.IsLineBlink(
                toggled: true, hasLastToggleFrame: true, lastToggleFrame: 1004, currentFrame: 1006,
                bodyChanged: false, offWindowCovered: !uncovered));
        }

        // ---- ComputeMaxOrbitalSpeedMeters ----

        [Fact]
        public void ComputeMaxOrbitalSpeed_Circular_EqualsCircularSpeed()
        {
            // Circular orbit (e = 0): periapsis speed == circular speed == sqrt(mu / a).
            double mu = 3.5316e12, a = 700_000.0;
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(a, 0.0, mu, instantaneousSpeedMeters: 1.0);

            Assert.Equal(System.Math.Sqrt(mu / a), vp, 3);
        }

        [Fact]
        public void ComputeMaxOrbitalSpeed_EccentricProbe_MatchesPeriapsisFormula()
        {
            // The captured probe orbit (sma 2.36 Mm, ecc 0.6901, Kerbin mu): vp ~= 2857 m/s.
            double mu = 3.5316e12, a = 2_360_277.0, e = 0.6901;
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(a, e, mu, instantaneousSpeedMeters: 1.0);
            double expected = System.Math.Sqrt(mu * (1.0 + e) / (a * (1.0 - e)));

            Assert.Equal(expected, vp, 2);
            Assert.True(vp > 2800.0 && vp < 2900.0, $"vp={vp}");
        }

        [Fact]
        public void ComputeMaxOrbitalSpeed_Hyperbolic_Finite()
        {
            // Hyperbolic escape (a < 0, e > 1, so a*(1-e) > 0): the periapsis form is still finite.
            double mu = 3.5316e12, a = -3_818_300.0, e = 1.1916;
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(a, e, mu, instantaneousSpeedMeters: 1.0);
            double expected = System.Math.Sqrt(mu * (1.0 + e) / (a * (1.0 - e)));

            Assert.Equal(expected, vp, 2);
            Assert.True(vp > 0.0 && !double.IsInfinity(vp) && !double.IsNaN(vp), $"vp={vp}");
        }

        [Fact]
        public void ComputeMaxOrbitalSpeed_Parabolic_FallsBackToInstantaneous()
        {
            // e == 1: a*(1-e) == 0 -> non-positive denom -> fall back to the instantaneous speed.
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(
                1_000_000.0, 1.0, 3.5316e12, instantaneousSpeedMeters: 1234.5);

            Assert.Equal(1234.5, vp, 3);
        }

        [Fact]
        public void ComputeMaxOrbitalSpeed_NaNElements_FallsBackToInstantaneous()
        {
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(
                double.NaN, 0.3, 3.5316e12, instantaneousSpeedMeters: 999.0);

            Assert.Equal(999.0, vp, 3);
        }

        [Fact]
        public void ComputeMaxOrbitalSpeed_NaNInstantaneousFallback_ReturnsZero()
        {
            // Degenerate elements AND a non-finite instantaneous fallback -> 0 (jump predicate uses floor).
            double vp = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(
                double.NaN, 0.3, 3.5316e12, instantaneousSpeedMeters: double.NaN);

            Assert.Equal(0.0, vp, 6);
        }

        [Fact]
        public void IconTeleport_CapturedProbeMotion_NotFlaggedWithRealDeltaUTAndMaxSpeed()
        {
            // Regression for the 3 captured probe icon-teleports: real per-frame motion on the eccentric
            // probe orbit, which the OLD model (instantaneous speed * unscaledDeltaTime * CurrentRate)
            // under-estimated ~20x and flagged. With the real per-frame UT advance and the periapsis-speed
            // upper bound, the captured dPos is well below the threshold and does NOT fire.
            // Captured f21582: dPos 1.85e6 m, real deltaUT ~1668.6 s; orbit sma 2.36 Mm, ecc 0.6901, Kerbin mu.
            double mu = 3.5316e12, a = 2_360_277.0, e = 0.6901;
            double deltaUT = 1668.6, dPos = 1_852_200.0;
            double maxSpeed = MapRenderTrace.ComputeMaxOrbitalSpeedMeters(a, e, mu, instantaneousSpeedMeters: 1109.0);
            double expectedMotion = maxSpeed * deltaUT; // the probe's new expected-motion

            bool jump = MapRenderTrace.IsIconJump(
                dPos: dPos,
                expectedMotionMeters: expectedMotion,
                currentFrame: 1000,
                floatingOriginShiftFrame: FrameNoFloatingOrigin,
                justReset: false,
                bodyChanged: false,
                suppressionLifted: false);

            Assert.False(jump);
            // And the dPos never exceeds the orbit's max possible arc in that interval (sanity bound).
            Assert.True(dPos <= maxSpeed * deltaUT, $"dPos={dPos} maxArc={maxSpeed * deltaUT}");
        }

        // ---- IsIconOffOrbit ----

        [Fact]
        public void IsIconOffOrbit_OnOrbit_NotOff()
        {
            // Icon sits exactly on its orbit line (angle ~0): no anomaly.
            bool off = MapRenderTrace.IsIconOffOrbit(
                angleDeg: 0.0, minAngleDeg: MapRenderTrace.IconOffOrbitMinAngleDeg);

            Assert.False(off);
        }

        [Fact]
        public void IsIconOffOrbit_SmallJitterUnderThreshold_NotOff()
        {
            // Float / interpolation jitter below the threshold is not reported.
            bool off = MapRenderTrace.IsIconOffOrbit(angleDeg: 0.4, minAngleDeg: 1.0);

            Assert.False(off);
        }

        [Fact]
        public void IsIconOffOrbit_AtThreshold_NotOff()
        {
            // Strict greater-than: exactly the threshold does not fire.
            bool off = MapRenderTrace.IsIconOffOrbit(angleDeg: 1.0, minAngleDeg: 1.0);

            Assert.False(off);
        }

        [Fact]
        public void IsIconOffOrbit_LargeAngle_Off()
        {
            // The documented looped-re-aim defect (~96.8 deg body rotation over the
            // loop shift): the icon is far off its own orbit line.
            bool off = MapRenderTrace.IsIconOffOrbit(
                angleDeg: 96.84, minAngleDeg: MapRenderTrace.IconOffOrbitMinAngleDeg);

            Assert.True(off);
        }

        [Fact]
        public void IsIconOffOrbit_NaN_NotOff()
        {
            // Degenerate orbit (NaN propagation) must not report a spurious off-orbit.
            bool off = MapRenderTrace.IsIconOffOrbit(
                angleDeg: double.NaN, minAngleDeg: 1.0);

            Assert.False(off);
        }

        [Fact]
        public void IsIconOffOrbit_Infinity_NotOff()
        {
            bool off = MapRenderTrace.IsIconOffOrbit(
                angleDeg: double.PositiveInfinity, minAngleDeg: 1.0);

            Assert.False(off);
        }

        // ---- ResolveIconReferenceUT ----

        [Fact]
        public void ResolveIconReferenceUT_DriveRecorded_UsesDrivenUT()
        {
            // A fresh drive record wins: the probe compares against the exact UT the icon-drive
            // placed the icon at, regardless of the re-derived director/shift inputs.
            double ut = MapRenderTrace.ResolveIconReferenceUT(
                hasDrivenUT: true, drivenUT: 13453.3,
                currentUT: 4043195.9, directorDriveActive: true, loopShift: 4029742.5);

            Assert.Equal(13453.3, ut, 3);
        }

        [Fact]
        public void ResolveIconReferenceUT_NoRecord_DirectorActive_UsesLiveClock()
        {
            // Fallback, director epoch-bake: the icon resolves at the live clock (shift 0).
            double ut = MapRenderTrace.ResolveIconReferenceUT(
                hasDrivenUT: false, drivenUT: 0.0,
                currentUT: 4043195.9, directorDriveActive: true, loopShift: 4029742.5);

            Assert.Equal(4043195.9, ut, 3);
        }

        [Fact]
        public void ResolveIconReferenceUT_NoRecord_Legacy_UsesShiftedClock()
        {
            // Fallback, legacy raw-epoch drive: effUT = liveUT - loopShift.
            double ut = MapRenderTrace.ResolveIconReferenceUT(
                hasDrivenUT: false, drivenUT: 0.0,
                currentUT: 4043195.9, directorDriveActive: false, loopShift: 4029742.5);

            Assert.Equal(13453.4, ut, 1);
        }

        [Fact]
        public void ResolveIconReferenceUT_RegressionFacet_RecordedShiftedWins_OverDirectorReDerivation()
        {
            // The exact residual false-positive from the 2026-06-14 warp capture: the icon-drive
            // placed the icon at the legacy SHIFTED phase (effUT 13453.3), but on the probe frame the
            // shadow's StockConic seed had flipped fresh, so a re-derivation (directorDriveActive=true)
            // would evaluate the reference conic at the live clock (4043195.9) -> spurious ~132 deg.
            // With the drive's recorded propagateUT present, the recorded value wins, so the reference
            // conic lands on the icon's actual phase.
            double drivenUT = 13453.3;
            double reDerived = MapRenderTrace.ResolveIconReferenceUT(
                hasDrivenUT: false, drivenUT: 0.0,
                currentUT: 4043195.9, directorDriveActive: true, loopShift: 4029742.5);
            double resolved = MapRenderTrace.ResolveIconReferenceUT(
                hasDrivenUT: true, drivenUT: drivenUT,
                currentUT: 4043195.9, directorDriveActive: true, loopShift: 4029742.5);

            // The re-derivation would have used the (wrong) live clock; the recorded value does not.
            Assert.Equal(4043195.9, reDerived, 3);
            Assert.Equal(drivenUT, resolved, 3);
            Assert.NotEqual(reDerived, resolved, 3);
        }
    }
}
