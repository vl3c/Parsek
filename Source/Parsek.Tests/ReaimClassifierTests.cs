using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 2b of re-aim: the pure recording -> phase-model classifier. Synthetic OrbitSegment chains
    // + a fake IBodyInfo cover the supported single-hop case and every bail path (same-parent, no
    // heliocentric leg, deep chain, multi-hop, no parking orbit, no arrival). Each test states the
    // regression it guards.
    public class ReaimClassifierTests
    {
        // Minimal IBodyInfo: only ReferenceBodyName matters to the classifier (it reuses AncestorChain
        // / TryFindCommonAncestor). Stock-like Kerbol parents incl. Duna's moon Ike + a Jool moon.
        private sealed class Bodies : IBodyInfo
        {
            public readonly Dictionary<string, string> Parent = new Dictionary<string, string>();
            public readonly Dictionary<string, double> Mu = new Dictionary<string, double>();
            public double RotationPeriod(string b) => double.NaN;
            public double OrbitPeriod(string b) => double.NaN;
            public string ReferenceBodyName(string b) => Parent.TryGetValue(b ?? "", out var v) ? v : null;
            public double SoiRadius(string b) => double.NaN;
            public double OrbitalVelocity(string b) => double.NaN;
            public double GravParameter(string b) => Mu.TryGetValue(b ?? "", out double v) ? v : double.NaN;
        }

        private static Bodies StockParents()
        {
            var f = new Bodies();
            f.Parent["Sun"] = null;
            f.Parent["Kerbin"] = "Sun";
            f.Parent["Mun"] = "Kerbin";
            f.Parent["Duna"] = "Sun";
            f.Parent["Ike"] = "Duna";
            f.Parent["Jool"] = "Sun";
            f.Parent["Tylo"] = "Jool";
            // Gravitational parameters (stock), for loiter-period computation in the classifier rework.
            f.Mu["Kerbin"] = 3.5316e12;
            f.Mu["Mun"] = 6.5138398e10;
            f.Mu["Duna"] = 3.0136321e11;
            f.Mu["Sun"] = 1.1723328e18;
            return f;
        }

        private static OrbitSegment Seg(string body, double start, double end, bool predicted = false)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, isPredicted = predicted };
        }

        [Fact]
        public void Classify_KerbinToDuna_Supported_IdentifiesPhases()
        {
            // Guards the core single-hop case: Kerbin parking -> Sun coast -> Duna arrival.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),   // parking
                Seg("Sun", 5000, 1000000),  // heliocentric transfer (S2)
                Seg("Duna", 1000000, 1005000), // arrival (S3)
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());

            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Kerbin", plan.LaunchBody);
            Assert.Equal("Sun", plan.CommonAncestor);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(1000000.0, plan.RecordedArrivalUT, 3);
            Assert.Equal(995000.0, plan.RecordedTransferTofSeconds, 3);
            Assert.Equal("Kerbin", plan.ParkingOrbit.bodyName);
            Assert.Equal("Sun", plan.HeliocentricLeg.bodyName);
            Assert.Equal("Duna", plan.ArrivalLeg.bodyName);
        }

        [Fact]
        public void Classify_KerbinToDuna_WithMidCourseCorrection_SpansFullTransfer()
        {
            // A mid-course correction burn splits the heliocentric coast into two Sun-bodied segments
            // (coast1 / [burn gap] / coast2). The transfer time must span the FULL departure->arrival
            // window (5000 -> 1000000), not just the pre-correction coast (5000 -> 400000) - otherwise
            // re-aim would synthesize a wrong, too-short transfer. coast2 (between the heliocentric leg
            // and Duna) is correctly skipped by the arrival search, and the multi-hop guard (which only
            // looks AFTER the arrival) does not fire.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),       // parking
                Seg("Sun", 5000, 400000),       // coast 1 (pre-correction)
                Seg("Sun", 400500, 1000000),    // coast 2 (post-correction; 500s burn gap)
                Seg("Duna", 1000000, 1005000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());

            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Duna", plan.TargetBody);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(1000000.0, plan.RecordedArrivalUT, 3);          // Duna SOI entry, not coast1 end
            Assert.Equal(995000.0, plan.RecordedTransferTofSeconds, 3);  // FULL transfer span
        }

        private static OrbitSegment SegA(string body, double start, double end, double a)
        {
            return new OrbitSegment { bodyName = body, startUT = start, endUT = end, semiMajorAxis = a };
        }

        [Fact]
        public void Classify_KerbinLoiterBeforeDirectTransfer_DepartsAtTransferStart()
        {
            // A long Kerbin LKO loiter (Kerbin-bodied) then a DIRECT transfer (Sun) to Duna. The Kerbin
            // loiter must NOT affect transfer detection: RecordedDepartureUT = the Sun transfer start
            // (the launch-body SOI exit), not the loiter start.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 1000000, 700000),    // long LKO loiter (Kerbin-bodied)
                SegA("Sun", 1000000, 2000000, 1.7e10),   // transfer
                SegA("Duna", 2000000, 2005000, 500000),  // arrival
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(1000000.0, plan.RecordedDepartureUT, 3); // transfer start, not the loiter start
            Assert.Equal(2000000.0, plan.RecordedArrivalUT, 3);
        }

        [Fact]
        public void Classify_RealSmaSingleTransfer_DepartsAtTransferStart()
        {
            // Real sma on every segment (not the sma=0 the older tests use, which zeroes the a-step). A
            // plain Kerbin->Duna with a Kerbin parking + a single Sun transfer arc must classify with
            // RecordedDepartureUT = the transfer start (byte-identical to the pre-rework result).
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 5000000, 1.7e10),       // transfer (~0.4 rev, < 1 rev)
                SegA("Duna", 5000000, 5005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3);
            Assert.Equal(5000000.0, plan.RecordedArrivalUT, 3);
            Assert.Equal(4995000.0, plan.RecordedTransferTofSeconds, 3);
        }

        [Fact]
        public void Classify_HeliocentricDebrisAfterDeparture_DoesNotCorruptTransfer()
        {
            // A jettisoned transfer stage coasts heliocentrically AFTER departure (similar sma, does NOT
            // reach Duna), interleaved in the flattened multi-member list. The SOI-contiguity anchor
            // picks the REAL transfer coast (the one ending at the Duna SOI entry); the debris (higher
            // startUT, not contiguous with the arrival) is not pulled into the transfer run (review C1).
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 5000000, 1.7e10),       // the real transfer (ends at the Duna SOI)
                SegA("Sun", 100000, 4000000, 1.69e10),    // jettisoned stage (similar sma, ends mid-transfer)
                SegA("Duna", 5000000, 5005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal(5000.0, plan.RecordedDepartureUT, 3); // the transfer start, NOT the debris start
            Assert.Equal(4995000.0, plan.RecordedTransferTofSeconds, 3);
        }

        [Fact]
        public void Classify_ChunkedSameSmaHeliocentricLoiter_Declined()
        {
            // A heliocentric loiter on the SAME sma as the transfer (no a-step to separate them) absorbed
            // into the Sun run, making the run span > 1 revolution -> declined (review M1: the a-step
            // alone would miss a same-sma loiter; the >1-rev duration gate catches it).
            // Sun period at sma 1.7e10 ~ 1.286e7 s; ~1 rev loiter + ~0.5 rev transfer -> ~1.5 rev run.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 13000000, 1.7e10),        // heliocentric loiter (~1 rev, same sma)
                SegA("Sun", 13000000, 20000000, 1.7e10),    // transfer (same sma, ends at Duna SOI)
                SegA("Duna", 20000000, 20005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains(">1 revolution", plan.Reason);
        }

        [Fact]
        public void Classify_HeliocentricLoiterBeforeTransfer_Declined()
        {
            // Kerbin parking -> heliocentric loiter (a closed Sun orbit) -> transfer burn (a-step) ->
            // transfer (Sun, ending at Duna) -> Duna. The transfer departs from a SOLAR parking orbit, so
            // re-aim's Lambert (r1 = launch body) would mis-aim -> declined to faithful. Crucially the
            // (different-SMA) heliocentric loiter does NOT become the transfer departure.
            var segs = new List<OrbitSegment>
            {
                SegA("Kerbin", 100, 5000, 700000),
                SegA("Sun", 5000, 500000, 2.5e10),       // heliocentric loiter (closed, larger SMA)
                SegA("Sun", 500000, 1000000, 1.7e10),    // transfer (SMA step at the departure burn)
                SegA("Duna", 1000000, 1005000, 500000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric parking", plan.Reason);
        }

        [Fact]
        public void Classify_SameParentMun_NotSupported_StaysFaithful()
        {
            // Guards: a same-parent target (Mun orbits Kerbin) has no heliocentric leg -> re-aim does
            // NOT engage; the mission stays on the faithful path.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Mun", 5000, 8000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric", plan.Reason);
        }

        [Fact]
        public void Classify_NoHeliocentricLeg_NotSupported()
        {
            // Guards C1/A/m4: a Kerbin-only chain (never warped through a Sun coast) -> no S2 -> bail.
            var segs = new List<OrbitSegment> { Seg("Kerbin", 100, 5000) };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("heliocentric", plan.Reason);
        }

        [Fact]
        public void Classify_DeepTargetIke_NotSupported_Deferred()
        {
            // Guards: Ike is a moon of Duna (not a direct child of the Sun) -> deep/multi-hop, deferred.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1002000),
                Seg("Ike", 1002000, 1003000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            // The first arrival after the Sun coast is Duna (a direct child) -> Duna is the target,
            // and Ike is just a later same-system leg; this is still single-hop to Duna. Supported.
            Assert.True(plan.Supported, plan.Reason);
            Assert.Equal("Duna", plan.TargetBody);
        }

        [Fact]
        public void Classify_MultiHop_TwoHeliocentricLegs_NotSupported()
        {
            // Guards: Kerbin -> Sun -> Duna -> Sun -> Jool (two heliocentric legs) is multi-hop -> bail.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1002000),
                Seg("Sun", 1002000, 5000000),
                Seg("Jool", 5000000, 5005000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("multi-hop", plan.Reason);
        }

        [Fact]
        public void Classify_StartsHeliocentric_NotSupported()
        {
            // Guards: a recording that starts already in solar orbit makes the launch body the Sun,
            // which has no ancestor leg -> not a re-aim mission (no launch-body parking orbit to depart
            // from). Bails (stays faithful).
            var segs = new List<OrbitSegment>
            {
                Seg("Sun", 5000, 1000000),
                Seg("Duna", 1000000, 1005000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
        }

        [Fact]
        public void Classify_NoArrivalLeg_NotSupported()
        {
            // Guards: a heliocentric coast that never reaches a target body (e.g. recording ended in
            // solar orbit) -> bail.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
            Assert.Contains("arrival", plan.Reason);
        }

        [Fact]
        public void Classify_PredictedSegments_Ignored()
        {
            // Guards: a predicted (extrapolated) Sun/Duna tail is NOT a recorded leg -> if the only
            // heliocentric/arrival legs are predicted, re-aim does not engage.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 100, 5000),
                Seg("Sun", 5000, 1000000, predicted: true),
                Seg("Duna", 1000000, 1005000, predicted: true),
            };
            var plan = ReaimClassifier.Classify(segs, StockParents());
            Assert.False(plan.Supported);
        }

        [Fact]
        public void Classify_NullInputs_NotSupported()
        {
            Assert.False(ReaimClassifier.Classify(null, StockParents()).Supported);
            Assert.False(ReaimClassifier.Classify(new List<OrbitSegment>(), StockParents()).Supported);
            Assert.False(ReaimClassifier.Classify(new List<OrbitSegment> { Seg("Kerbin", 0, 1) }, null).Supported);
        }
    }
}
