using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 3a of re-aim: the pure per-window OrbitSegment assembler. Guards the assembly ordering,
    // UT coherence (parking hands off to transfer at departure, transfer to arrival at SOI entry),
    // and the time-shift phase preservation (epoch moves with the UTs).
    public class ReaimSegmentAssemblerTests
    {
        private static OrbitSegment Seg(string body, double start, double end, double epoch,
            double sma = 1.0e7, double mEp = 0.5)
        {
            return new OrbitSegment
            {
                bodyName = body, startUT = start, endUT = end, epoch = epoch,
                semiMajorAxis = sma, eccentricity = 0.1, inclination = 5.0,
                longitudeOfAscendingNode = 30.0, argumentOfPeriapsis = 45.0, meanAnomalyAtEpoch = mEp
            };
        }

        private static ReaimMissionPlan SupportedPlan(OrbitSegment parking, OrbitSegment arrival)
        {
            return new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin", TargetBody = "Duna", CommonAncestor = "Sun",
                ParkingOrbit = parking, ArrivalLeg = arrival
            };
        }

        [Fact]
        public void ShiftInTime_MovesUTsAndEpochTogether()
        {
            var s = Seg("Kerbin", 100, 200, 150);
            var shifted = ReaimSegmentAssembler.ShiftInTime(s, 1000.0);
            Assert.Equal(1100.0, shifted.startUT, 6);
            Assert.Equal(1200.0, shifted.endUT, 6);
            Assert.Equal(1150.0, shifted.epoch, 6); // epoch moved with the window -> phase preserved
            // Shape + body untouched.
            Assert.Equal(s.semiMajorAxis, shifted.semiMajorAxis, 6);
            Assert.Equal("Kerbin", shifted.bodyName);
        }

        [Fact]
        public void ReanchorStartAndEnd_PreserveDuration()
        {
            var s = Seg("Duna", 1000, 1500, 1200);
            var atStart = ReaimSegmentAssembler.ReanchorStart(s, 9000.0);
            Assert.Equal(9000.0, atStart.startUT, 6);
            Assert.Equal(9500.0, atStart.endUT, 6);   // duration 500 preserved
            Assert.Equal(9200.0, atStart.epoch, 6);

            var atEnd = ReaimSegmentAssembler.ReanchorEnd(s, 9000.0);
            Assert.Equal(8500.0, atEnd.startUT, 6);
            Assert.Equal(9000.0, atEnd.endUT, 6);
        }

        [Fact]
        public void Assemble_KerbinToDuna_ContiguousAbsoluteUTs()
        {
            var parking = Seg("Kerbin", 100, 600, 300);     // 500 s parking
            var arrival = Seg("Duna", 2000, 5000, 2500);    // 3000 s arrival
            var plan = SupportedPlan(parking, arrival);

            double departureUT = 1_000_000.0;
            double soiEntryUT = 1_500_000.0;
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10); // raw transfer; assembler normalizes span

            var segs = ReaimSegmentAssembler.Assemble(plan, departureUT, transfer, soiEntryUT);

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count);
            // S0 parking: ends exactly at departure, keeps its 500 s duration.
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal(departureUT, segs[0].endUT, 3);
            Assert.Equal(departureUT - 500.0, segs[0].startUT, 3);
            // S2 transfer: spans [departure, soiEntry], Sun-bodied, not predicted.
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(departureUT, segs[1].startUT, 3);
            Assert.Equal(soiEntryUT, segs[1].endUT, 3);
            Assert.False(segs[1].isPredicted);
            // S3 arrival: starts at SOI entry, keeps its 3000 s duration, Duna-bodied.
            Assert.Equal("Duna", segs[2].bodyName);
            Assert.Equal(soiEntryUT, segs[2].startUT, 3);
            Assert.Equal(soiEntryUT + 3000.0, segs[2].endUT, 3);
            // Contiguous: parking.end == transfer.start, transfer.end == arrival.start.
            Assert.Equal(segs[0].endUT, segs[1].startUT, 3);
            Assert.Equal(segs[1].endUT, segs[2].startUT, 3);
        }

        [Fact]
        public void Assemble_UnsupportedOrDegenerate_ReturnsNull()
        {
            var parking = Seg("Kerbin", 100, 600, 300);
            var arrival = Seg("Duna", 2000, 5000, 2500);
            var plan = SupportedPlan(parking, arrival);
            var transfer = Seg("Sun", 0, 0, 0);

            // Unsupported plan.
            var bad = plan; bad.Supported = false;
            Assert.Null(ReaimSegmentAssembler.Assemble(bad, 1e6, transfer, 1.5e6));
            // soiEntry <= departure (degenerate span).
            Assert.Null(ReaimSegmentAssembler.Assemble(plan, 1.5e6, transfer, 1.0e6));
            // NaN.
            Assert.Null(ReaimSegmentAssembler.Assemble(plan, double.NaN, transfer, 1.5e6));
        }
    }
}
