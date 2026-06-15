using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-static tests for the seam icon re-snap helpers added for Bug A
    /// (loop-member ghost map icon renders far off its own orbit line at high
    /// warp). The fix itself lives in <c>GhostMapPresence.UpdateGhostOrbitFrom
    /// StateVectors</c> and forces a <c>VesselPrecalculate.CalculatePhysicsStats</c>
    /// CoMD refresh on the reseed seam; that Harmony/Unity-runtime path is covered
    /// by the in-game <c>LoopBoundaryIconWarpLagTest</c> + the manual warp playtest
    /// greps. These xUnit tests pin the two PURE pieces the fix is built from:
    /// the element-change gate that keeps the refresh off the steady-state path,
    /// and the icon-vs-conic angle metric used for the proof-of-fire summary.
    /// Both are Unity-math only (no FlightGlobals / no scene), so they run headless.
    /// </summary>
    public class LoopIconWarpLagTests
    {
        // ---- GhostOrbitElementsChanged: the seam gate ----------------------

        [Fact]
        public void ElementsChanged_FirstApply_True()
        {
            // hadLast=false: nothing applied yet -> must refresh (seed the cache).
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: false, 0, 0, 0, 750491, 0.0071, 1000.0));
        }

        [Fact]
        public void ElementsChanged_Unchanged_False()
        {
            Assert.False(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, 750491, 0.0071, 1000.0));
        }

        [Fact]
        public void ElementsChanged_SmaDiffers_True()
        {
            // LKO parking (750491) -> transfer (6735240): the Kerbin escape seam.
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, 6735240, 0.0071, 1000.0));
        }

        [Fact]
        public void ElementsChanged_EccDiffers_True()
        {
            // 0.0071 -> 0.8882: the orbit-raise seam (frame 17952 in the capture).
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, 750491, 0.8882, 1000.0));
        }

        [Fact]
        public void ElementsChanged_EpochDiffers_True()
        {
            // Same near-circular conic but a new epoch (the gap-glide advances to the
            // next recorded point): a phase change -> CoMD goes stale -> must refresh.
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, 750491, 0.0071, 1002.0));
        }

        [Fact]
        public void ElementsChanged_HyperbolicEcc_True()
        {
            // 0.2376 -> 1.6989: the hyperbolic Mun-capture seam (frame 21729, the
            // largest 168 deg anomaly). The gate must still fire on hyperbolic.
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 460578, 0.2376, 1000.0, -502456, 1.6989, 1000.0));
        }

        [Fact]
        public void ElementsChanged_WithinEpsilon_False()
        {
            // sma delta 0.3 < relative-1e-6 tol (max(1, 0.75)); ecc delta 5e-10 < 1e-9;
            // epoch delta 5e-4 < 1e-3 -> all within band -> no refresh (steady state).
            Assert.False(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750000, 0.0071, 1000.0,
                750000.3, 0.0071 + 5e-10, 1000.0 + 5e-4));
        }

        [Fact]
        public void ElementsChanged_NaNNewSma_True()
        {
            // A non-finite NEW element counts as changed: the gate must not suppress
            // and strand a stale icon; the downstream NaN-safe path handles it.
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, double.NaN, 0.0071, 1000.0));
        }

        [Fact]
        public void ElementsChanged_InfinityNewEpoch_True()
        {
            Assert.True(GhostMapPresence.GhostOrbitElementsChanged(
                hadLast: true, 750491, 0.0071, 1000.0, 750491, 0.0071, double.PositiveInfinity));
        }

        // ---- IconVsOrbitAngleDeg: the proof-of-fire metric -----------------

        [Fact]
        public void IconVsOrbit_Aligned_Zero()
        {
            double a = GhostMapPresence.IconVsOrbitAngleDeg(
                new Vector3d(1e5, 0, 0), new Vector3d(1e5, 0, 0));
            Assert.Equal(0.0, a, 3);
        }

        [Fact]
        public void IconVsOrbit_Opposed_180()
        {
            double a = GhostMapPresence.IconVsOrbitAngleDeg(
                new Vector3d(1e5, 0, 0), new Vector3d(-1e5, 0, 0));
            Assert.Equal(180.0, a, 3);
        }

        [Fact]
        public void IconVsOrbit_Perpendicular_90()
        {
            double a = GhostMapPresence.IconVsOrbitAngleDeg(
                new Vector3d(1e5, 0, 0), new Vector3d(0, 1e5, 0));
            Assert.Equal(90.0, a, 3);
        }

        [Fact]
        public void IconVsOrbit_DegenerateIcon_NaN()
        {
            // Icon vector below the 1 m floor (unresolved CoMD) -> NaN so the summary
            // skips it instead of logging a phantom 90 deg (angle of a zero vector).
            double a = GhostMapPresence.IconVsOrbitAngleDeg(
                new Vector3d(0.5, 0, 0), new Vector3d(1e5, 0, 0));
            Assert.True(double.IsNaN(a));
        }

        [Fact]
        public void IconVsOrbit_DegenerateOrbit_NaN()
        {
            double a = GhostMapPresence.IconVsOrbitAngleDeg(
                new Vector3d(1e5, 0, 0), new Vector3d(0, 0, 0));
            Assert.True(double.IsNaN(a));
        }
    }
}
