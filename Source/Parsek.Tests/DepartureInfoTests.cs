using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for SelectiveSpawnUI.OrbitsMatch and ComputeDepartureInfo.
    /// Verifies departure detection logic for the Real Spawn Warp system.
    /// </summary>
    public class DepartureInfoTests
    {
        private static OrbitSegment MakeSegment(
            double startUT, double endUT, string body = "Kerbin",
            double sma = 700000, double ecc = 0.0, double inc = 0.0,
            double argPe = 0.0, double lan = 0.0)
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                semiMajorAxis = sma,
                eccentricity = ecc,
                inclination = inc,
                argumentOfPeriapsis = argPe,
                longitudeOfAscendingNode = lan,
                meanAnomalyAtEpoch = 0,
                epoch = startUT
            };
        }

        // ════════════════════════════════════════════════════════════════
        //  OrbitsMatch tests
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void OrbitsMatch_IdenticalSegments_ReturnsTrue()
        {
            var seg = MakeSegment(0, 1000, sma: 700000, ecc: 0.001, inc: 28.5);
            Assert.True(SelectiveSpawnUI.OrbitsMatch(seg, seg));
        }

        [Fact]
        public void OrbitsMatch_DifferentBody_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, body: "Kerbin");
            var b = MakeSegment(0, 1000, body: "Mun");
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentSma_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, sma: 700000);
            var b = MakeSegment(0, 1000, sma: 750000); // >0.1% difference
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentEccentricity_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, ecc: 0.001);
            var b = MakeSegment(0, 1000, ecc: 0.01); // >0.0001 difference
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentInclination_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, inc: 28.5);
            var b = MakeSegment(0, 1000, inc: 28.52); // >0.01 degree difference
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_WithinTolerances_ReturnsTrue()
        {
            // Tiny differences from float noise in same orbit re-captured
            var a = MakeSegment(0, 1000, sma: 700000, ecc: 0.0010, inc: 28.500);
            var b = MakeSegment(0, 1000, sma: 700050, ecc: 0.0010, inc: 28.505);
            // SMA diff: 50/700050 = 0.00007 < 0.001 ✓
            // Ecc diff: 0.0 < 0.0001 ✓
            // Inc diff: 0.005 < 0.01 ✓
            Assert.True(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentLan_SameShape_ReturnsTrue()
        {
            // LAN is not compared — precesses over time
            var a = MakeSegment(0, 1000, sma: 700000, lan: 45.0);
            var b = MakeSegment(0, 1000, sma: 700000, lan: 90.0);
            Assert.True(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentArgPe_EccentricOrbit_ReturnsFalse()
        {
            // For eccentric orbits (ecc > 0.01), argPe matters
            var a = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 90);
            var b = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 120); // 30° > 1° tolerance
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_DifferentArgPe_NearCircularOrbit_ReturnsTrue()
        {
            // For near-circular orbits (ecc < 0.01), argPe is numerically unstable — skip it
            var a = MakeSegment(0, 1000, sma: 700000, ecc: 0.005, argPe: 90);
            var b = MakeSegment(0, 1000, sma: 700000, ecc: 0.005, argPe: 250);
            Assert.True(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_HyperbolicMatching_ReturnsTrue()
        {
            // Hyperbolic: negative SMA
            var a = MakeSegment(0, 1000, sma: -500000, ecc: 1.5, inc: 10);
            var b = MakeSegment(0, 1000, sma: -500000, ecc: 1.5, inc: 10);
            Assert.True(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_HyperbolicDifferent_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, sma: -500000, ecc: 1.5, inc: 10);
            var b = MakeSegment(0, 1000, sma: -600000, ecc: 1.5, inc: 10);
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_ArgPeWrapAround360_WithinTolerance_ReturnsTrue()
        {
            var a = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 0.3);
            var b = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 359.8);
            // Diff = 359.5, wrapped = 0.5 < 1.0° tolerance
            Assert.True(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void OrbitsMatch_ArgPeWrapAround360_ExceedsTolerance_ReturnsFalse()
        {
            var a = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 5);
            var b = MakeSegment(0, 1000, sma: 700000, ecc: 0.3, argPe: 355);
            // Diff = 350, wrapped = 10 > 1.0° tolerance
            Assert.False(SelectiveSpawnUI.OrbitsMatch(a, b));
        }

        [Fact]
        public void ComputeDepartureInfo_CurrentUtAtSegmentBoundary_NoDeparture()
        {
            // currentUT exactly at segment endUT (inclusive for last segment)
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 5000, sma: 700000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0, null, 5000);
            // currentUT = endUT = 5000, still in last segment (inclusive), matches itself
            Assert.False(info.willDepart);
        }

        // ════════════════════════════════════════════════════════════════
        //  ComputeDepartureInfo tests
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void ComputeDepartureInfo_NoOrbitSegments_NoDeparture()
        {
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                null, 5000, null, 0, 0, 0, 0, null, 100);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_EmptyOrbitSegments_NoDeparture()
        {
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                new List<OrbitSegment>(), 5000, null, 0, 0, 0, 0, null, 100);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_SingleSegmentCoversFullRange_NoDeparture()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 5000, sma: 700000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0, null, 2500);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_TwoSegments_DifferentBody_Departs()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000),
                MakeSegment(2000, 5000, body: "Mun", sma: 250000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0, null, 500);
            Assert.True(info.willDepart);
            Assert.Equal(1000, info.departureUT);
            Assert.Equal("Mun", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_TwoSegments_SameBody_DifferentSma_Departs()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000),
                MakeSegment(1020, 5000, body: "Kerbin", sma: 2000000) // transfer orbit
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0, null, 500);
            Assert.True(info.willDepart);
            Assert.Equal(1000, info.departureUT);
            Assert.Equal("maneuver", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_CurrentUtOutsideAnySegment_NoDeparture()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(100, 500, sma: 700000),
                MakeSegment(600, 1000, sma: 800000)
            };
            // currentUT = 50, before any segment
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 1000, null, 0, 0, 0, 0, null, 50);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_TerminalOrbitDiffers_Departs()
        {
            // Single segment, but terminal orbit (at EndUT beyond segment) differs
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000,
                "Mun", 250000, 0.01, 5.0, 0,
                TerminalState.Orbiting, 500);
            Assert.True(info.willDepart);
            Assert.Equal(1000, info.departureUT);
            Assert.Equal("Mun", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_TerminalOrbitMatches_NoDeparture()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000, inc: 28.5)
            };
            // Terminal orbit matches current segment
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000,
                "Kerbin", 700000, 0, 28.5, 0,
                TerminalState.Orbiting, 500);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_LandedTerminalState_OrbitalCurrent_Departs()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000,
                null, 0, 0, 0, 0,
                TerminalState.Landed, 500);
            Assert.True(info.willDepart);
            Assert.Equal(1000, info.departureUT);
            Assert.Equal("Kerbin", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_EndUtInGap_LastSegmentDiffers_Departs()
        {
            // Recording ends during an off-rails phase (no segment covers EndUT)
            // Last segment differs from current → departure
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000),
                MakeSegment(1020, 3000, body: "Kerbin", sma: 2000000) // transfer orbit
            };
            // EndUT = 3500, beyond last segment. Fallback: compare current (seg 0) vs last (seg 1)
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 3500, null, 0, 0, 0, 0, null, 500);
            Assert.True(info.willDepart);
            Assert.Equal("maneuver", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_EndUtInGap_LastSegmentMatches_NoDeparture()
        {
            // Recording ends during off-rails, but last segment matches current
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000),
                MakeSegment(1020, 2000, body: "Mun", sma: 250000),
                MakeSegment(2500, 3000, body: "Kerbin", sma: 700000) // returned to same orbit
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 3500, null, 0, 0, 0, 0, null, 500);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_ReturnTrip_NoDeparture()
        {
            // Ghost: Kerbin orbit → Mun → Kerbin orbit (same parameters)
            // The final orbit matches the current one, so no departure
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000, inc: 0),
                MakeSegment(1500, 3000, body: "Mun", sma: 250000, inc: 5),
                MakeSegment(4000, 5000, body: "Kerbin", sma: 700000, inc: 0)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0, null, 500);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_SplashedTerminalState_Departs()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Kerbin", sma: 700000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0,
                TerminalState.Splashed, 500);
            Assert.True(info.willDepart);
            Assert.Equal("Kerbin", info.destination);
        }

        [Fact]
        public void ComputeDepartureInfo_DestroyedTerminalState_Departs()
        {
            var segs = new List<OrbitSegment>
            {
                MakeSegment(0, 1000, body: "Duna", sma: 500000)
            };
            var info = SelectiveSpawnUI.ComputeDepartureInfo(
                segs, 5000, null, 0, 0, 0, 0,
                TerminalState.Destroyed, 500);
            Assert.True(info.willDepart);
            Assert.Equal("Duna", info.destination);
        }

        // ════════════════════════════════════════════════════════════════
        //  Recording convenience overload
        // ════════════════════════════════════════════════════════════════

        [Fact]
        public void ComputeDepartureInfo_NullRecording_NoDeparture()
        {
            var info = SelectiveSpawnUI.ComputeDepartureInfo((Recording)null, 500);
            Assert.False(info.willDepart);
        }

        [Fact]
        public void ComputeDepartureInfo_RecordingOverload_DelegatesCorrectly()
        {
            var rec = new Recording();
            rec.OrbitSegments.Add(MakeSegment(0, 1000, body: "Kerbin", sma: 700000));
            rec.OrbitSegments.Add(MakeSegment(2000, 5000, body: "Mun", sma: 250000));
            // Set EndUT via Points (Recording.EndUT uses last point's UT)
            rec.Points.Add(new TrajectoryPoint { ut = 0 });
            rec.Points.Add(new TrajectoryPoint { ut = 5000 });

            var info = SelectiveSpawnUI.ComputeDepartureInfo(rec, 500);
            Assert.True(info.willDepart);
            Assert.Equal("Mun", info.destination);
        }
    }
}
