using System;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="GhostOrbitLinePatch.ShouldDeferOrbitLineHide"/>
    /// (orbit-line blink grace) and the per-pid grace map in
    /// <see cref="GhostMapPresence"/>. The grace debounces only the two TRANSIENT
    /// off reasons at a short phase-boundary segment; the DURABLE off reasons hide
    /// instantly, and a SUSTAINED transient phase still hides once the grace window
    /// expires (the coupling with FIX #27's sustained descent ownership).
    ///
    /// The grace deadline is a RENDER FRAME count (Time.frameCount), not a UT
    /// window: the original UT window collapsed below one frame's UT step under time
    /// warp and deferred nothing, so the heliocentric orbit line blinked. Frames are
    /// warp-independent. These tests pass frame numbers directly to the pure
    /// decision; the comparison (currentFrame &lt;= graceUntilFrame) is unit-agnostic.
    /// </summary>
    [Collection("Sequential")]
    public class GhostOrbitLineGraceTests : IDisposable
    {
        public GhostOrbitLineGraceTests()
        {
            GhostMapPresence.ResetForTesting();
        }

        public void Dispose()
        {
            GhostMapPresence.ResetForTesting();
        }

        // --- ShouldDeferOrbitLineHide: transient reasons inside the window ---

        [Fact]
        public void Defer_PolylineOwns_InsideGrace_KeepsVisible()
        {
            // currentFrame (100) <= graceUntilFrame (120) and a finite ellipse: defer.
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_StaleSegment_InsideGrace_KeepsVisible()
        {
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentFrame: 100,
                graceUntilFrame: 115,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_AtExactGraceDeadline_StillDefers()
        {
            // Inclusive deadline: currentFrame == graceUntilFrame is still inside.
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentFrame: 101,
                graceUntilFrame: 101,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_BothTransientReasons_DeferTheLineIdentically()
        {
            // CHANGE 1 invariant: the LINE-hide deferral decision is symmetric for
            // the two transient reasons (both keep the orbit line visible across a
            // transient boundary dip). The asymmetry between the two branches is
            // PURELY in the proto-ICON side-effect, NOT in this decision: the
            // polyline-owns branch keeps the proto icon SUPPRESSED (drawIcons=NONE)
            // because IsPolylineOwningGhostPhase is true there, so the non-proto
            // marker draws and re-showing the proto icon would double up; the
            // stale-segment branch re-shows the proto icon (OBJ) because the
            // polyline does not own the phase and the marker is already skipped.
            // That side-effect lives in the Unity Postfix; here we pin that the
            // decision input itself is identical for both reasons.
            const int cur = 100;
            const int until = 120;
            bool polyline = GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns, cur, until, true);
            bool stale = GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment, cur, until, true);
            Assert.True(polyline);
            Assert.True(stale);
            Assert.Equal(polyline, stale);
        }

        [Fact]
        public void OffReasonConstants_AreDistinct()
        {
            Assert.NotEqual(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                GhostOrbitLinePatch.OffReasonStaleSegment);
        }

        // --- Grace window expired: sustained transient phase hides ---

        [Fact]
        public void Defer_PolylineOwns_AfterGraceExpired_Hides()
        {
            // Sustained polyline ownership: the deadline frame is in the past, so
            // the hide is NOT deferred (no double-draw with the polyline). This is
            // the FIX #27 coupling: a sustained below-atmosphere descent (more than
            // OrbitLineGraceFrames consecutive off-frames) keeps hiding the line.
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentFrame: 200,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_StaleSegment_AfterGraceExpired_Hides()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentFrame: 300,
                graceUntilFrame: 250,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_NoGraceStamped_Hides()
        {
            // int.MinValue sentinel (no stamp) never defers.
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentFrame: 100,
                graceUntilFrame: int.MinValue,
                orbitFiniteElliptical: true));
        }

        // --- Durable off reasons are NEVER graced ---

        [Fact]
        public void Defer_BelowAtmosphere_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "below-atmosphere",
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_PastBodyFrameEnd_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "past-body-frame-end",
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_BeforeBodyFrameStart_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "before-body-frame-start",
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_TerminalBelowAtmosphere_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "terminal-below-atmosphere",
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: true));
        }

        // --- Non-elliptical orbit: nothing to keep showing ---

        [Fact]
        public void Defer_TransientButNotElliptical_Hides()
        {
            // A hyperbolic / degenerate orbit has no ellipse to bridge across the
            // boundary chatter, so the transient hide is not deferred even inside
            // the window.
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentFrame: 100,
                graceUntilFrame: 120,
                orbitFiniteElliptical: false));
        }

        // --- Per-pid grace map (GhostMapPresence) ---

        [Fact]
        public void GraceMap_Unstamped_ReturnsIntMinValue()
        {
            Assert.Equal(int.MinValue, GhostMapPresence.GetOrbitLineGraceUntilFrame(4242u));
        }

        [Fact]
        public void GraceMap_StampThenRead_RoundTrips()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 124);
            Assert.Equal(124, GhostMapPresence.GetOrbitLineGraceUntilFrame(4242u));
        }

        [Fact]
        public void GraceMap_StampOverwrites()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 100);
            GhostMapPresence.StampOrbitLineGrace(4242u, 200);
            Assert.Equal(200, GhostMapPresence.GetOrbitLineGraceUntilFrame(4242u));
        }

        [Fact]
        public void GraceMap_PerPidIndependent()
        {
            GhostMapPresence.StampOrbitLineGrace(1u, 10);
            GhostMapPresence.StampOrbitLineGrace(2u, 20);
            Assert.Equal(10, GhostMapPresence.GetOrbitLineGraceUntilFrame(1u));
            Assert.Equal(20, GhostMapPresence.GetOrbitLineGraceUntilFrame(2u));
        }

        [Fact]
        public void GraceMap_ResetForTesting_Clears()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 124);
            GhostMapPresence.ResetForTesting();
            Assert.Equal(int.MinValue, GhostMapPresence.GetOrbitLineGraceUntilFrame(4242u));
        }

        // --- Grace constant is a sane frame window ---

        [Fact]
        public void GraceFrames_IsPositiveAndSaneWindow()
        {
            // A few frames (enough to bridge the ~1-12 frame transfer-phase chatter
            // dips) but well short of a sustained phase that should hide.
            Assert.True(GhostOrbitLinePatch.OrbitLineGraceFrames > 0);
            Assert.True(GhostOrbitLinePatch.OrbitLineGraceFrames >= 10
                && GhostOrbitLinePatch.OrbitLineGraceFrames <= 120);
        }
    }
}
