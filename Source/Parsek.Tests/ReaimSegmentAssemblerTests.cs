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

        private static ReaimMissionPlan SupportedPlan(OrbitSegment parking, OrbitSegment arrival,
            double recordedDepartureUT)
        {
            return new ReaimMissionPlan
            {
                Supported = true,
                LaunchBody = "Kerbin", TargetBody = "Duna", CommonAncestor = "Sun",
                ParkingOrbit = parking, ArrivalLeg = arrival,
                RecordedDepartureUT = recordedDepartureUT
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
        public void Assemble_KerbinToDuna_ContiguousRecordedSpanUTs()
        {
            // Recorded mission: parking [100,600], SOI exit at 600 (recorded), arrival recorded
            // [2000,5000]. Re-aim re-times the transfer to a (constant) Hohmann tof and re-anchors
            // the arrival after it; the parking keeps its recorded UTs.
            var parking = Seg("Kerbin", 100, 600, 300);     // 500 s parking, ends at the recorded exit
            var arrival = Seg("Duna", 2000, 5000, 2500);    // 3000 s arrival
            var plan = SupportedPlan(parking, arrival, recordedDepartureUT: 600.0);

            double tof = 1200.0; // Hohmann tof (constant across windows)
            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10); // per-window orientation set by caller

            var segs = ReaimSegmentAssembler.Assemble(plan, transfer, tof);

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count);
            // S0 parking: recorded UTs unchanged, ends at the recorded exit (600).
            Assert.Equal("Kerbin", segs[0].bodyName);
            Assert.Equal(100.0, segs[0].startUT, 3);
            Assert.Equal(600.0, segs[0].endUT, 3);
            // S2 transfer: [exitUT, exitUT+tof] = [600, 1800], Sun-bodied, not predicted.
            Assert.Equal("Sun", segs[1].bodyName);
            Assert.Equal(600.0, segs[1].startUT, 3);
            Assert.Equal(1800.0, segs[1].endUT, 3);
            Assert.False(segs[1].isPredicted);
            // S3 arrival: re-anchored to start at 1800, keeps its 3000 s duration, Duna-bodied.
            Assert.Equal("Duna", segs[2].bodyName);
            Assert.Equal(1800.0, segs[2].startUT, 3);
            Assert.Equal(4800.0, segs[2].endUT, 3);
            // Contiguous: parking.end == transfer.start, transfer.end == arrival.start.
            Assert.Equal(segs[0].endUT, segs[1].startUT, 3);
            Assert.Equal(segs[1].endUT, segs[2].startUT, 3);
        }

        [Fact]
        public void Assemble_WithRecordedTof_ArrivalLandsAtRecordedArrival_FitsSpan()
        {
            // Span coherence (review C1): when the caller passes the RECORDED transfer tof
            // (tof == recordedArrivalUT - recordedDepartureUT), the re-anchored arrival lands back at
            // its RECORDED start, so the assembled trajectory fits the fixed recorded loop span exactly
            // (no clipping). This is the invariant that makes re-aim use the recorded tof, not Hohmann.
            var parking = Seg("Kerbin", 100, 600, 300);     // ends at recorded SOI exit (600)
            var arrival = Seg("Duna", 2600, 5000, 3000);    // recorded arrival starts at 2600
            // Recorded: departure 600, recorded transfer [600,2600] -> recorded tof 2000, arrival start 2600.
            var plan = SupportedPlan(parking, arrival, recordedDepartureUT: 600.0);
            double recordedTof = 2600.0 - 600.0; // == arrival.startUT - departure

            var transfer = Seg("Sun", 0, 0, 0, sma: 2.0e10);
            var segs = ReaimSegmentAssembler.Assemble(plan, transfer, recordedTof);

            Assert.NotNull(segs);
            Assert.Equal(3, segs.Count);
            // Transfer occupies the recorded transfer interval; arrival re-anchors to its RECORDED start.
            Assert.Equal(600.0, segs[1].startUT, 3);
            Assert.Equal(2600.0, segs[1].endUT, 3);
            Assert.Equal(2600.0, segs[2].startUT, 3);   // == recorded arrival start (unchanged)
            Assert.Equal(5000.0, segs[2].endUT, 3);     // == recorded arrival end -> fits the recorded span
        }

        [Fact]
        public void Assemble_UnsupportedOrDegenerate_ReturnsNull()
        {
            var parking = Seg("Kerbin", 100, 600, 300);
            var arrival = Seg("Duna", 2000, 5000, 2500);
            var plan = SupportedPlan(parking, arrival, recordedDepartureUT: 600.0);
            var transfer = Seg("Sun", 0, 0, 0);

            // Unsupported plan.
            var bad = plan; bad.Supported = false;
            Assert.Null(ReaimSegmentAssembler.Assemble(bad, transfer, 1200.0));
            // Degenerate tof.
            Assert.Null(ReaimSegmentAssembler.Assemble(plan, transfer, 0.0));
            Assert.Null(ReaimSegmentAssembler.Assemble(plan, transfer, double.NaN));
        }
    }
}
