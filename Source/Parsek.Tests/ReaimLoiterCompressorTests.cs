using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Re-aim loiter compression (docs/dev/plans/reaim-loiter-compression.md): the pure loiter detector +
    // whole-period cut + compressed-UT map. Validates detection robustness (single long segment, chunked
    // run, a-step split, hyperbolic/body-change run termination, travel-arc kept) and the seam-exact
    // cut + monotonic compression map.
    public class ReaimLoiterCompressorTests
    {
        private const double MuKerbin = 3.5316e12;
        private const double MuSun = 1.1723328e18;
        // Low Kerbin orbit ~700 km radius -> period ~1958 s.
        private const double LkoA = 700000.0;
        private static readonly double LkoT = ReaimLoiterCompressor.OrbitalPeriod(LkoA, MuKerbin);

        private static Func<string, double> Mu => b => b == "Kerbin" ? MuKerbin : (b == "Sun" ? MuSun : double.NaN);

        private static OrbitSegment Seg(string body, double start, double end, double a, bool predicted = false)
        {
            return new OrbitSegment
            {
                bodyName = body, startUT = start, endUT = end, semiMajorAxis = a,
                eccentricity = 0.01, isPredicted = predicted
            };
        }

        [Fact]
        public void OrbitalPeriod_EllipticalVsHyperbolic()
        {
            Assert.True(LkoT > 1900.0 && LkoT < 2050.0); // ~1958 s
            Assert.True(double.IsNaN(ReaimLoiterCompressor.OrbitalPeriod(-1.0e7, MuKerbin)));  // hyperbolic a<0
            Assert.True(double.IsNaN(ReaimLoiterCompressor.OrbitalPeriod(LkoA, double.NaN)));   // unknown mu
            Assert.True(double.IsNaN(ReaimLoiterCompressor.OrbitalPeriod(0.0, MuKerbin)));       // degenerate
        }

        [Fact]
        public void ComputeCuts_SingleLongLoiterSegment_CutsAllButOneRev()
        {
            // One Kerbin LKO segment spanning ~51 revs. Keep 1 rev -> cut 50 periods from the start.
            double dur = 100000.0; // ~51 LKO revs
            var segs = new List<OrbitSegment> { Seg("Kerbin", 0.0, dur, LkoA) };

            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);

            Assert.Single(cuts);
            long wholeRevs = (long)Math.Floor(dur / LkoT);
            Assert.Equal(0.0, cuts[0].StartUT, 3);
            Assert.Equal((wholeRevs - 1) * LkoT, cuts[0].LengthSeconds, 3); // exact whole periods
            // Kept tail = dur - cut = ~1 rev + remainder, < 2 periods.
            double keptTail = dur - cuts[0].LengthSeconds;
            Assert.InRange(keptTail, LkoT, 2.0 * LkoT);
        }

        [Fact]
        public void ComputeCuts_ChunkedLoiterRun_CompressesByTotalDuration()
        {
            // The same LKO loiter captured as many ~1-rev contiguous chunks (the 83-segment form).
            var segs = new List<OrbitSegment>();
            double t = 0.0;
            for (int k = 0; k < 30; k++) // 30 revs
            {
                segs.Add(Seg("Kerbin", t, t + LkoT, LkoA));
                t += LkoT;
            }
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);

            Assert.Single(cuts); // the whole run -> one cut
            Assert.Equal(0.0, cuts[0].StartUT, 3);
            Assert.Equal(29.0 * LkoT, cuts[0].LengthSeconds, 0); // keep 1, cut 29 periods
        }

        [Fact]
        public void ComputeCuts_AStepBetweenSegments_SplitsIntoTwoRuns()
        {
            // Two contiguous Kerbin runs at different SMAs (an orbit-raise, no SOI exit). The a-step guard
            // must end the first run so each compresses with its OWN period (T_rep stays valid).
            double highA = LkoA * 1.5; // +50% -> well over the 5% step threshold
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 50000.0, LkoA),          // ~25 LKO revs
                Seg("Kerbin", 50000.0, 120000.0, highA),    // higher orbit, longer period
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);

            Assert.Equal(2, cuts.Count); // NOT merged into one run with a wrong T_rep
            Assert.Equal(0.0, cuts[0].StartUT, 3);
            Assert.Equal(50000.0, cuts[1].StartUT, 3);
        }

        [Fact]
        public void ComputeCuts_TransferArc_NotALoiter()
        {
            // A heliocentric transfer arc: duration < its own period -> single pass -> kept (no cut).
            double transferA = 1.7e10;
            double transferT = ReaimLoiterCompressor.OrbitalPeriod(transferA, MuSun);
            var segs = new List<OrbitSegment> { Seg("Sun", 0.0, 0.4 * transferT, transferA) };

            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Empty(cuts);
        }

        [Fact]
        public void ComputeCuts_HyperbolicAndPredicted_Skipped()
        {
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 100000.0, -1.0e7),                 // hyperbolic (a<0) -> not a loiter
                Seg("Kerbin", 100000.0, 200000.0, LkoA, predicted: true), // predicted -> not a loiter
            };
            Assert.Empty(ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1));
        }

        [Fact]
        public void ComputeCuts_BodyChange_EndsRun()
        {
            // Kerbin loiter then a Sun arc: the SOI change ends the run; only the Kerbin loiter is cut.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 100000.0, LkoA),                 // loiter
                Seg("Sun", 100000.0, 100001.0, 1.7e10),             // brief Sun arc (not a loiter)
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Single(cuts);
            Assert.Equal(0.0, cuts[0].StartUT, 3);
        }

        [Fact]
        public void ComputeCuts_GapBetweenSegments_EndsRun()
        {
            // A large UT gap (a burn / off-rails interval) between two Kerbin orbits ends the run.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 50000.0, LkoA),
                Seg("Kerbin", 60000.0, 110000.0, LkoA), // 10000 s gap >> contiguity epsilon
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Equal(2, cuts.Count);
        }

        [Fact]
        public void CompressUT_EmptyCuts_IsIdentity()
        {
            var cuts = new List<ReaimLoiterCompressor.LoiterCut>();
            Assert.Equal(12345.0, ReaimLoiterCompressor.CompressUT(12345.0, cuts), 6);
        }

        [Fact]
        public void CompressUT_SingleCut_RemovesAfter_CollapsesInside_KeepsBefore()
        {
            var cuts = new List<ReaimLoiterCompressor.LoiterCut>
            {
                new ReaimLoiterCompressor.LoiterCut { StartUT = 1000.0, LengthSeconds = 500.0 }, // [1000,1500]
            };
            Assert.Equal(800.0, ReaimLoiterCompressor.CompressUT(800.0, cuts), 6);    // before -> unchanged
            Assert.Equal(1000.0, ReaimLoiterCompressor.CompressUT(1000.0, cuts), 6);  // at start -> unchanged
            Assert.Equal(1000.0, ReaimLoiterCompressor.CompressUT(1250.0, cuts), 6);  // inside -> collapses to start
            Assert.Equal(1000.0, ReaimLoiterCompressor.CompressUT(1500.0, cuts), 6);  // at end -> start
            Assert.Equal(1500.0, ReaimLoiterCompressor.CompressUT(2000.0, cuts), 6);  // after -> minus full cut
        }

        [Fact]
        public void CompressUT_MultipleCuts_Monotonic_RemovesCumulatively()
        {
            var cuts = new List<ReaimLoiterCompressor.LoiterCut>
            {
                new ReaimLoiterCompressor.LoiterCut { StartUT = 1000.0, LengthSeconds = 500.0 },  // [1000,1500]
                new ReaimLoiterCompressor.LoiterCut { StartUT = 3000.0, LengthSeconds = 1000.0 }, // [3000,4000]
            };
            // After both cuts: 5000 - 500 - 1000 = 3500.
            Assert.Equal(3500.0, ReaimLoiterCompressor.CompressUT(5000.0, cuts), 6);
            // Between the cuts (e.g. 2000): only the first removed -> 1500.
            Assert.Equal(1500.0, ReaimLoiterCompressor.CompressUT(2000.0, cuts), 6);
            // Monotonic non-decreasing across the range.
            double prev = double.NegativeInfinity;
            for (double t = 0; t <= 5000; t += 250)
            {
                double c = ReaimLoiterCompressor.CompressUT(t, cuts);
                Assert.True(c >= prev - 1e-9);
                prev = c;
            }
        }
    }
}
