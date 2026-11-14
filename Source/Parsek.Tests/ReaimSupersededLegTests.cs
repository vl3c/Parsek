using System.Collections.Generic;
using Parsek.Display;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOOP-SUN-LEG-DRAWN-OFF-PATH: a re-aim loop unit draws ONE synthesized transfer conic in
    /// place of the recorded heliocentric stretch, so the recorded course-correction burn legs
    /// inside that stretch belong to the transfer the re-aim replaced and must not draw (they
    /// landed 12.4 Gm off the "Duna Supply 1" icon, on the recorded transfer). The numbers are
    /// the TP-8 subject's: recording 36c7688b, recorded Sun segments [63901473.6, 68378174.7],
    /// [68378180.4, 68378236.0], [68378313.4, 72258643.3], re-aimed into one segment
    /// [63901473.6, 72258643.3], with burn legs [68378174.9, 68378180.1] and
    /// [68378239.0, 68378311.3].
    /// </summary>
    public class ReaimSupersededLegTests
    {
        private static OrbitSegment Seg(string body, double start, double end)
            => new OrbitSegment { bodyName = body, startUT = start, endUT = end, semiMajorAxis = 1.7e10 };

        private static List<OrbitSegment> Recorded() => new List<OrbitSegment>
        {
            Seg("Kerbin", 63825858.8, 63901473.5),
            Seg("Sun", 63901473.58, 68378174.69),
            Seg("Sun", 68378180.39, 68378235.96),
            Seg("Sun", 68378313.44, 72258643.30),
            Seg("Duna", 72258643.30, 72279764.82),
        };

        private static List<OrbitSegment> Reaimed() => new List<OrbitSegment>
        {
            Seg("Kerbin", 63825858.8, 63901473.5),
            Seg("Sun", 63901473.58, 72258643.30),
            Seg("Duna", 72258643.30, 72279764.82),
        };

        // MUTATION NOTE: making IsLegSupersededByReaimedConic return false reds this; it is the
        // one predicate the head-gated pass, the head-leg bridge candidate and the forward run
        // legs all read.
        [Fact]
        public void BurnLegsInsideTheReaimedTransfer_AreSuperseded()
        {
            var recorded = Recorded();
            var reaimed = Reaimed();
            Assert.True(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Sun", 68378174.93, 68378180.09));
            Assert.True(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Sun", 68378238.98, 68378311.28));
        }

        [Fact]
        public void FaithfulWindow_NeverSupersedes()
        {
            // The resolver hands back the RECORDED list by reference for a faithful member or a
            // declined window: drawing stays exactly as before.
            var recorded = Recorded();
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                recorded, recorded, "Sun", "Sun", 68378174.93, 68378180.09));
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                null, recorded, "Sun", "Sun", 68378174.93, 68378180.09));
        }

        [Fact]
        public void LegsOnOtherBodies_OrOutsideTheConic_StillDraw()
        {
            var recorded = Recorded();
            var reaimed = Reaimed();
            // The Kerbin ascent / escape and the Duna descent are not the re-aimed body.
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Kerbin", 63825858.8, 63826434.9));
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Duna", 72353168.6, 72353218.8));
            // A Sun leg that straddles the end of the re-aimed span is not inside it.
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Sun", 72258600.0, 72258700.0));
            // No re-aimed body named: nothing to compare against.
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, null, "Sun", 68378174.93, 68378180.09));
            // A degenerate leg span.
            Assert.False(GhostTrajectoryPolylineRenderer.IsLegSupersededByReaimedConic(
                reaimed, recorded, "Sun", "Sun", 68378180.0, 68378170.0));
        }

        [Fact]
        public void TryResolveReaimedLegFilter_NoLoopUnits_IsOff()
        {
            var rec = new Recording { RecordingId = "r", OrbitSegments = Recorded() };
            Assert.False(GhostTrajectoryPolylineRenderer.TryResolveReaimedLegFilter(
                0, rec, 1.0e8, null, out List<OrbitSegment> eff, out string body));
            Assert.Null(eff);
            Assert.Null(body);
        }
    }
}
