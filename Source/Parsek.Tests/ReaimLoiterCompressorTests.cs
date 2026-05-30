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
        public void ComputeCuts_GradualDriftPastThreshold_SplitsRun_AnchoredToFirst()
        {
            // Gradual sub-5%-per-step drift that cumulatively exceeds 5% from the FIRST segment. Anchoring
            // the a-step to the run's first segment (not the previous) ends the run once cumulative drift
            // passes the threshold, so T_rep stays valid (review M2). "Compare to previous" would merge
            // all three (each step <5%) into one run with a wrong T_rep -> a cut off a true period.
            double a0 = 1.0e7;
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0, 500000, a0),              // anchor
                Seg("Kerbin", 500000, 1000000, a0 * 1.03), // +3% from first (within 5%) -> same run
                Seg("Kerbin", 1000000, 1500000, a0 * 1.06),// +6% from first (>5%) -> new run
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Equal(2, cuts.Count); // split at the cumulative-drift threshold, not merged into one
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
        public void ComputeCuts_GapToDifferentOrbit_EndsRun()
        {
            // A UT gap to a DIFFERENT orbit (a real burn: sma shifted past the sameOrbit tolerance, but
            // still within the 5% a-step) ends the run -> two separate loiter runs / cuts.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 50000.0, LkoA),
                Seg("Kerbin", 60000.0, 110000.0, LkoA * 1.02), // 10000 s gap + 2% sma change (a real burn)
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Equal(2, cuts.Count);
        }

        [Fact]
        public void ComputeCuts_SameOrbitAcrossSamplingGaps_MergesToOneRun()
        {
            // The recorder/optimizer splits one continuous LKO parking orbit into several checkpoint
            // chunks at warp boundaries, leaving small UT gaps between SAME-sma segments. They must merge
            // into ONE loiter run (kept to ~1 rev TOTAL, not ~1 rev per chunk) since they are the same
            // orbit; the whole-period cut stays seamless (identical period). Regression for the Duna 'Duna
            // One' parking, which the optimizer split into 5 same-sma chunks.
            var segs = new List<OrbitSegment>
            {
                Seg("Kerbin", 0.0, 50000.0, LkoA),
                Seg("Kerbin", 50002.0, 100000.0, LkoA),  // 2 s gap (warp boundary), same orbit
                Seg("Kerbin", 100050.0, 150000.0, LkoA), // 50 s gap, same orbit
            };
            var cuts = ReaimLoiterCompressor.ComputeCuts(segs, Mu, keepRevs: 1);
            Assert.Single(cuts);                    // merged across the gaps -> ONE cut, not three
            Assert.Equal(0.0, cuts[0].StartUT, 3);  // run starts at the first chunk
            double keptTail = 150000.0 - cuts[0].LengthSeconds; // ~1 rev kept for the WHOLE parking
            Assert.InRange(keptTail, LkoT, 2.0 * LkoT);
        }

        [Fact]
        public void CompressUT_EmptyCuts_IsIdentity()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>();
            Assert.Equal(12345.0, GhostPlaybackLogic.CompressSpanUT(12345.0, cuts), 6);
        }

        [Fact]
        public void CompressUT_SingleCut_RemovesAfter_CollapsesInside_KeepsBefore()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1000.0, LengthSeconds = 500.0 }, // [1000,1500]
            };
            Assert.Equal(800.0, GhostPlaybackLogic.CompressSpanUT(800.0, cuts), 6);    // before -> unchanged
            Assert.Equal(1000.0, GhostPlaybackLogic.CompressSpanUT(1000.0, cuts), 6);  // at start -> unchanged
            Assert.Equal(1000.0, GhostPlaybackLogic.CompressSpanUT(1250.0, cuts), 6);  // inside -> collapses to start
            Assert.Equal(1000.0, GhostPlaybackLogic.CompressSpanUT(1500.0, cuts), 6);  // at end -> start
            Assert.Equal(1500.0, GhostPlaybackLogic.CompressSpanUT(2000.0, cuts), 6);  // after -> minus full cut
        }

        [Fact]
        public void CompressUT_MultipleCuts_Monotonic_RemovesCumulatively()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1000.0, LengthSeconds = 500.0 },  // [1000,1500]
                new GhostPlaybackLogic.LoopCut { StartUT = 3000.0, LengthSeconds = 1000.0 }, // [3000,4000]
            };
            // After both cuts: 5000 - 500 - 1000 = 3500.
            Assert.Equal(3500.0, GhostPlaybackLogic.CompressSpanUT(5000.0, cuts), 6);
            // Between the cuts (e.g. 2000): only the first removed -> 1500.
            Assert.Equal(1500.0, GhostPlaybackLogic.CompressSpanUT(2000.0, cuts), 6);
            // Monotonic non-decreasing across the range.
            double prev = double.NegativeInfinity;
            for (double t = 0; t <= 5000; t += 250)
            {
                double c = GhostPlaybackLogic.CompressSpanUT(t, cuts);
                Assert.True(c >= prev - 1e-9);
                prev = c;
            }
        }

        [Fact]
        public void DecompressSpanUT_EmptyCuts_IsIdentity()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>();
            Assert.Equal(12345.0, GhostPlaybackLogic.DecompressSpanUT(12345.0, cuts), 6);
            Assert.Equal(0.0, GhostPlaybackLogic.TotalCutLength(cuts), 6);
            Assert.Equal(0.0, GhostPlaybackLogic.TotalCutLength(null), 6);
        }

        [Fact]
        public void DecompressSpanUT_SingleCut_SkipsToCutEnd()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1000.0, LengthSeconds = 500.0 }, // [1000,1500]
            };
            Assert.Equal(500.0, GhostPlaybackLogic.TotalCutLength(cuts), 6);
            Assert.Equal(800.0, GhostPlaybackLogic.DecompressSpanUT(800.0, cuts), 6);   // before -> unchanged
            Assert.Equal(1500.0, GhostPlaybackLogic.DecompressSpanUT(1000.0, cuts), 6); // collapse point -> cut END
            Assert.Equal(2000.0, GhostPlaybackLogic.DecompressSpanUT(1500.0, cuts), 6); // after -> plus full cut
        }

        [Fact]
        public void DecompressSpanUT_InvertsCompressSpanUT_OutsideCuts()
        {
            // Decompress(Compress(t)) == t for any recorded UT in the kept (non-excised) ranges: strictly
            // before a cut start or at/after a cut end. The half-open cut interval [start, end) is lossy by
            // design (the whole interval collapses to one compressed instant that decompresses to the cut
            // END), so the cut START itself does NOT round-trip - it is excised. Round-trips the kept
            // points of a two-cut timeline: cut ends (1500, 4000) round-trip; cut starts (1000, 3000) do not.
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1000.0, LengthSeconds = 500.0 },  // [1000,1500]
                new GhostPlaybackLogic.LoopCut { StartUT = 3000.0, LengthSeconds = 1000.0 }, // [3000,4000]
            };
            foreach (double t in new[] { 0.0, 500.0, 999.0, 1500.0, 2000.0, 2999.0, 4000.0, 4500.0, 6000.0 })
            {
                double c = GhostPlaybackLogic.CompressSpanUT(t, cuts);
                Assert.Equal(t, GhostPlaybackLogic.DecompressSpanUT(c, cuts), 6);
            }
        }
    }
}
