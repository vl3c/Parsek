using Parsek.Display;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for the Bug 1 (looped-mission map trajectory lines vanish when zoomed out) fix: the pure
    /// draw-mode decision <see cref="GhostTrajectoryPolylineRenderer.ShouldDraw3DMapLine"/>. It mirrors
    /// stock <c>OrbitRendererBase.DrawSpline</c>, which draws an orbit line with <c>VectorLine.Draw3D()</c>
    /// only while <c>MapView.Draw3DLines</c> is true and falls back to the 2D <c>VectorLine.Draw()</c> path
    /// otherwise. Stock flips <c>MapView.Draw3DLines</c> to false once the map camera zooms out past
    /// <c>max3DlineDrawDist</c> (1500) - exactly the threshold past which Vectrosity's <c>Draw3D</c>
    /// world-space reconstruction degrades and drops the line. Parsek previously always called
    /// <c>Draw3D()</c>, so its lines vanished past that point while stock's (flipped to 2D) persisted.
    ///
    /// The live <c>DrawMapLine</c> wrapper that reads <c>MapView</c> and dispatches to
    /// <c>VectorLine.Draw3D()</c> / <c>Draw()</c> is Unity / Vectrosity-coupled (both live in
    /// <c>Assembly-CSharp</c> / <c>Assembly-CSharp-firstpass</c>, not referenced by this headless test
    /// assembly); it is covered by the in-game test (RuntimeTests, MapView category).
    /// </summary>
    public class GhostTrajectoryPolylineMapModeTests
    {
        [Fact]
        public void ShouldDraw3DMapLine_MapView3DLinesOn_DrawsIn3D()
        {
            // Below the stock far-zoom 2D-flip threshold (and not in high-orbit-count mode) MapView keeps
            // Draw3DLines true; mirror it so a leg / arc / bridge draws 3D exactly like stock orbit lines.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDraw3DMapLine(
                mapViewPresent: true, mapViewDraw3DLines: true));
        }

        [Fact]
        public void ShouldDraw3DMapLine_MapView3DLinesOff_DrawsIn2D()
        {
            // Past the far-zoom threshold (or with too many orbits shown) stock sets Draw3DLines false and
            // draws the 2D screen-space line. Parsek must follow: this is the Bug 1 fix - in 2D mode the
            // line renders via the vectorCam overlay instead of the Draw3D path that vanishes on zoom-out.
            Assert.False(GhostTrajectoryPolylineRenderer.ShouldDraw3DMapLine(
                mapViewPresent: true, mapViewDraw3DLines: false));
        }

        [Fact]
        public void ShouldDraw3DMapLine_MapViewAbsent_DefaultsTo3D()
        {
            // MapView.fetch can be transiently null (a DDOL Driver tick during a scene switch). Default to
            // 3D - the prior behavior - rather than risk an NPE reading MapView.Draw3DLines. The
            // mapViewDraw3DLines argument is irrelevant when MapView is absent.
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDraw3DMapLine(
                mapViewPresent: false, mapViewDraw3DLines: false));
            Assert.True(GhostTrajectoryPolylineRenderer.ShouldDraw3DMapLine(
                mapViewPresent: false, mapViewDraw3DLines: true));
        }
    }
}
