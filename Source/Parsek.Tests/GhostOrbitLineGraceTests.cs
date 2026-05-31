using System;
using Parsek.Patches;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="GhostOrbitLinePatch.ShouldDeferOrbitLineHide"/>
    /// (FIX #26 orbit-line blink grace) and the per-pid grace map in
    /// <see cref="GhostMapPresence"/>. The grace debounces only the two TRANSIENT
    /// off reasons at a short phase-boundary segment; the DURABLE off reasons hide
    /// instantly, and a SUSTAINED transient phase still hides once the grace window
    /// expires (the coupling with FIX #27's sustained descent ownership).
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
            // currentUT (100) <= graceUntil (101) and a finite ellipse: defer.
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentUT: 100.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_StaleSegment_InsideGrace_KeepsVisible()
        {
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentUT: 100.0,
                graceUntilUT: 101.5,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_AtExactGraceDeadline_StillDefers()
        {
            // Inclusive deadline: currentUT == graceUntil is still inside.
            Assert.True(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentUT: 101.0,
                graceUntilUT: 101.0,
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
            const double cur = 100.0;
            const double until = 101.0;
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
            // Sustained polyline ownership: the deadline is in the past, so the
            // hide is NOT deferred (no double-draw with the polyline). This is
            // the FIX #27 coupling: a sustained below-atmosphere descent keeps
            // hiding the orbit line.
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentUT: 105.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_StaleSegment_AfterGraceExpired_Hides()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonStaleSegment,
                currentUT: 200.0,
                graceUntilUT: 150.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_NoGraceStamped_Hides()
        {
            // Negative-infinity sentinel (no stamp) never defers.
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                GhostOrbitLinePatch.OffReasonPolylineOwns,
                currentUT: 100.0,
                graceUntilUT: double.NegativeInfinity,
                orbitFiniteElliptical: true));
        }

        // --- Durable off reasons are NEVER graced ---

        [Fact]
        public void Defer_BelowAtmosphere_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "below-atmosphere",
                currentUT: 100.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_PastBodyFrameEnd_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "past-body-frame-end",
                currentUT: 100.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_BeforeBodyFrameStart_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "before-body-frame-start",
                currentUT: 100.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: true));
        }

        [Fact]
        public void Defer_TerminalBelowAtmosphere_NeverDeferred()
        {
            Assert.False(GhostOrbitLinePatch.ShouldDeferOrbitLineHide(
                "terminal-below-atmosphere",
                currentUT: 100.0,
                graceUntilUT: 101.0,
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
                currentUT: 100.0,
                graceUntilUT: 101.0,
                orbitFiniteElliptical: false));
        }

        // --- Per-pid grace map (GhostMapPresence) ---

        [Fact]
        public void GraceMap_Unstamped_ReturnsNegativeInfinity()
        {
            Assert.Equal(double.NegativeInfinity, GhostMapPresence.GetOrbitLineGraceUntil(4242u));
        }

        [Fact]
        public void GraceMap_StampThenRead_RoundTrips()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 123.5);
            Assert.Equal(123.5, GhostMapPresence.GetOrbitLineGraceUntil(4242u));
        }

        [Fact]
        public void GraceMap_StampOverwrites()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 100.0);
            GhostMapPresence.StampOrbitLineGrace(4242u, 200.0);
            Assert.Equal(200.0, GhostMapPresence.GetOrbitLineGraceUntil(4242u));
        }

        [Fact]
        public void GraceMap_PerPidIndependent()
        {
            GhostMapPresence.StampOrbitLineGrace(1u, 10.0);
            GhostMapPresence.StampOrbitLineGrace(2u, 20.0);
            Assert.Equal(10.0, GhostMapPresence.GetOrbitLineGraceUntil(1u));
            Assert.Equal(20.0, GhostMapPresence.GetOrbitLineGraceUntil(2u));
        }

        [Fact]
        public void GraceMap_ResetForTesting_Clears()
        {
            GhostMapPresence.StampOrbitLineGrace(4242u, 123.5);
            GhostMapPresence.ResetForTesting();
            Assert.Equal(double.NegativeInfinity, GhostMapPresence.GetOrbitLineGraceUntil(4242u));
        }

        // --- Grace constant is a sane warp-stable window ---

        [Fact]
        public void GraceSeconds_IsPositiveAndAround1To2Seconds()
        {
            Assert.True(GhostOrbitLinePatch.OrbitLineGraceSeconds > 0.0);
            Assert.True(GhostOrbitLinePatch.OrbitLineGraceSeconds >= 1.0
                && GhostOrbitLinePatch.OrbitLineGraceSeconds <= 2.0);
        }
    }
}
