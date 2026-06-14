using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Stage B of re-aim eccentric-target tof reliability (docs/dev/plans/reaim-eccentric-tof-reliability.md
    // section 4.1.5). The pure tof-centering helper's regression contract is the four invariants (a-d). These
    // tests assert the INVARIANTS / BEHAVIOURS, NOT the placeholder band-law constants (EccGain,
    // MaxHalfWidthFraction), so those measure-first placeholders can be re-pinned against the in-game Eeloo
    // measurement without rewriting the suite. The helper is pure (no Unity, no shared static state), so no
    // [Collection("Sequential")] is needed.
    public class ReaimTofSearchTests
    {
        // The recorded-tof step grid the resolver uses today (kept fine, matches the resolver constant).
        private const double Step = ReaimTofSearch.DefaultStepFraction;

        // Reconstructs today's recorded-centered +-6% candidate set EXACTLY as the pre-stage-B resolver
        // built it: s=0 -> {recorded}, then s=1..12 -> {recorded + s*step, recorded - s*step}. This is the
        // literal pre-change loop, the ground truth invariant (a) is measured against.
        private static List<double> TodayRecordedCenteredSet(double recordedTof)
        {
            double step = recordedTof * Step;
            const int searchMaxSteps = 12; // BaseHalfWidthFraction(0.06) / DefaultStepFraction(0.005)
            var set = new List<double> { recordedTof };
            for (int s = 1; s <= searchMaxSteps; s++)
            {
                double up = recordedTof + s * step;
                double down = recordedTof - s * step;
                if (up > 0.0) set.Add(up);
                if (down > 0.0) set.Add(down);
            }
            return set;
        }

        [Fact]
        public void InvariantA_ZeroEccentricity_CandidateSetIdenticalToTodaysRecordedCenteredBand()
        {
            // Invariant (a): at eTarget = 0 the search collapses to today's recorded-centered +-6% set,
            // BYTE-FOR-BYTE (order included). This is the structural zero-regression proof for a circular
            // target - the search must not widen, recenter, or reorder when eccentricity is zero, regardless
            // of where the geometric tof lies.
            const double recordedTof = 200.0 * 86400.0; // ~Kerbin->Duna scale, in seconds
            double[] geomTofVariants =
            {
                recordedTof,                 // geomTof == recorded (no drift)
                recordedTof * 1.30,          // geomTof far longer (would pull the band out if eTarget>0)
                recordedTof * 0.70,          // geomTof far shorter
                double.NaN,                  // degenerate geomTof
            };

            List<double> today = TodayRecordedCenteredSet(recordedTof);
            foreach (double geomTof in geomTofVariants)
            {
                IReadOnlyList<double> got =
                    ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.0);
                Assert.Equal(today, got.ToList()); // order-sensitive equality
            }
        }

        [Fact]
        public void InvariantB_RecordedTofAlwaysFirstCandidate_AcrossEccentricities()
        {
            // Invariant (b): the recorded tof is always present AND is the very first candidate probed, for
            // every eccentricity and geomTof. Window 0 (the recorded geometry) must always be reachable, and
            // probing it first preserves the zero-regression "resolves identically today" property.
            const double recordedTof = 150.0 * 86400.0;
            double[] eccs = { 0.0, 0.05, 0.2, 0.26, 0.9, 5.0 /* absurd, still bounded */ };
            double[] geomTofs = { recordedTof * 1.4, recordedTof * 0.6, recordedTof, double.NaN };

            foreach (double ecc in eccs)
            {
                foreach (double geomTof in geomTofs)
                {
                    IReadOnlyList<double> got =
                        ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, ecc);
                    Assert.NotEmpty(got);
                    Assert.Equal(recordedTof, got[0]);              // first
                    Assert.Contains(recordedTof, (IEnumerable<double>)got); // present
                }
            }
        }

        [Fact]
        public void InvariantC_AllCandidatesWithinMaxHalfWidthFraction_OfRecordedTof()
        {
            // Invariant (c): every candidate stays within <= MaxHalfWidthFraction of the recorded tof, no
            // matter how eccentric the target. This is the hard bound that prevents the eccentricity term from
            // reintroducing the unbounded knife-edge tof WIDENING M-MIS-1 refused. Asserted as a RELATIONSHIP
            // to the cap (not a magic absolute), so the cap can be re-pinned without breaking this test.
            const double recordedTof = 300.0 * 86400.0;
            double cap = ReaimTofSearch.MaxHalfWidthFraction;
            // A tiny tolerance absorbs the integer-step grid rounding at the band edge.
            double maxDeviation = recordedTof * cap + recordedTof * Step;

            double[] eccs = { 0.0, 0.1, 0.26, 0.5, 1.0, 100.0 };
            foreach (double ecc in eccs)
            {
                IReadOnlyList<double> got =
                    ReaimTofSearch.BuildCandidateTofs(recordedTof, recordedTof * 1.5, ecc);
                foreach (double tof in got)
                {
                    Assert.True(tof > 0.0, "no non-positive candidate should be emitted");
                    double deviation = Math.Abs(tof - recordedTof);
                    Assert.True(deviation <= maxDeviation,
                        $"candidate {tof} deviates {deviation} from recorded {recordedTof}, " +
                        $"exceeding the bounded max {maxDeviation} (cap fraction {cap})");
                }
            }
        }

        [Fact]
        public void InvariantD_PositiveEccentricity_SetExtendsTowardGeomTof_LongerSide()
        {
            // Invariant (d): for eTarget > 0 the candidate set EXTENDS toward the geometric tof. Here geomTof
            // is LONGER than recorded, so the eccentric search must reach to longer tofs than the base +6%
            // band did (and reach them BEFORE the equally-far shorter ones, since the bias probes the geomTof
            // side first). A circular target (ecc=0) must NOT reach there - that delta IS the eccentricity
            // mechanism.
            const double recordedTof = 200.0 * 86400.0;
            double geomTof = recordedTof * 1.5; // well outside +-6%, on the LONGER side

            IReadOnlyList<double> baseSet =
                ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.0);
            IReadOnlyList<double> eccSet =
                ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.26);

            double baseMax = baseSet.Max();
            double eccMax = eccSet.Max();
            Assert.True(eccMax > baseMax,
                $"eccentric search must reach longer tofs toward geomTof (eccMax={eccMax} > baseMax={baseMax})");

            // The eccentric set must contain a candidate STRICTLY beyond the base band's longest probe, i.e.
            // a genuine extension toward the longer geomTof, not just a reshuffle of the base set.
            Assert.Contains(eccSet, t => t > baseMax + 0.5 * (recordedTof * Step));

            // Bias check: the first candidate beyond the base band edge is on the geomTof (longer) side, so the
            // search reaches toward the geometric center first.
            double baseEdge = recordedTof + 12 * (recordedTof * Step); // base +6% longest probe
            double firstBeyond = eccSet.First(t => Math.Abs(t - recordedTof) > Math.Abs(baseEdge - recordedTof) + 1e-6);
            Assert.True(firstBeyond > recordedTof,
                $"first expansion candidate {firstBeyond} should be on the longer (geomTof) side of recorded {recordedTof}");
        }

        [Fact]
        public void InvariantD_PositiveEccentricity_SetExtendsTowardGeomTof_ShorterSide()
        {
            // Invariant (d), mirror case: when geomTof is SHORTER than recorded, the eccentric search must
            // reach to SHORTER tofs than the base band, biased to the shorter side first. Guards that the
            // toward-geomTof bias is symmetric (an inbound / near-periapsis recorded sample drifts the other
            // way).
            const double recordedTof = 200.0 * 86400.0;
            double geomTof = recordedTof * 0.55; // well outside -6%, on the SHORTER side

            IReadOnlyList<double> baseSet =
                ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.0);
            IReadOnlyList<double> eccSet =
                ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.26);

            double baseMin = baseSet.Min();
            double eccMin = eccSet.Min();
            Assert.True(eccMin < baseMin,
                $"eccentric search must reach shorter tofs toward geomTof (eccMin={eccMin} < baseMin={baseMin})");

            double baseEdge = recordedTof - 12 * (recordedTof * Step); // base -6% shortest probe
            double firstBeyond = eccSet.First(t => Math.Abs(t - recordedTof) > Math.Abs(baseEdge - recordedTof) + 1e-6);
            Assert.True(firstBeyond < recordedTof,
                $"first expansion candidate {firstBeyond} should be on the shorter (geomTof) side of recorded {recordedTof}");
        }

        [Fact]
        public void HalfWidthFraction_GrowsWithEccentricity_Bounded_AndBaseAtZero()
        {
            // The band law itself: base at zero eccentricity (invariant a's mechanism), monotonic non-decrease
            // with eccentricity, and hard-capped (invariant c's mechanism). Asserted as relationships, not
            // magic numbers, so the placeholder gain/cap can be re-pinned.
            Assert.Equal(ReaimTofSearch.BaseHalfWidthFraction, ReaimTofSearch.HalfWidthFraction(0.0));

            double prev = ReaimTofSearch.HalfWidthFraction(0.0);
            foreach (double e in new[] { 0.05, 0.1, 0.2, 0.26, 0.5, 1.0, 10.0 })
            {
                double f = ReaimTofSearch.HalfWidthFraction(e);
                Assert.True(f >= prev, $"half-width must not decrease as eccentricity grows (e={e})");
                Assert.True(f <= ReaimTofSearch.MaxHalfWidthFraction, $"half-width must be capped (e={e})");
                Assert.True(f >= ReaimTofSearch.BaseHalfWidthFraction, $"half-width must never go below base (e={e})");
                prev = f;
            }

            // A high-eccentricity target reaches the cap (the band is genuinely bounded, not unbounded).
            Assert.Equal(ReaimTofSearch.MaxHalfWidthFraction, ReaimTofSearch.HalfWidthFraction(100.0));
        }

        [Fact]
        public void HalfWidthFraction_DegenerateEccentricity_ClampsToBase()
        {
            // A degenerate body read (NaN or a nonsensical negative eccentricity) must NOT narrow the band or
            // NaN-poison the search; it clamps to the base band (fail-safe).
            Assert.Equal(ReaimTofSearch.BaseHalfWidthFraction, ReaimTofSearch.HalfWidthFraction(double.NaN));
            Assert.Equal(ReaimTofSearch.BaseHalfWidthFraction, ReaimTofSearch.HalfWidthFraction(-0.3));
        }

        [Fact]
        public void BuildCandidateTofs_DistinctAndPositive_AndNoPreferredSideStillProbesBothSides()
        {
            // Robustness: a geomTof with NO preferred side (NaN, non-positive, OR exactly equal to the
            // recorded tof) must not crash or bias degenerately - the expansion still probes both sides
            // symmetrically (like the base band), no candidate is non-positive / NaN, and no candidate is
            // emitted twice (the expansion ring k starts strictly above the base band, so there is no overlap;
            // this asserts that distinctness the test name claims, not just positivity).
            const double recordedTof = 100.0 * 86400.0;
            double baseEdgeUp = recordedTof + 12 * (recordedTof * Step);
            double baseEdgeDown = recordedTof - 12 * (recordedTof * Step);

            // Each variant has no preferred expansion side: NaN, non-positive, and exactly-equal geomTof. The
            // exactly-equal case at e>0 is the boundary the bias logic falls through to its symmetric branch -
            // exercise it directly here (InvariantA only covers equal geomTof at e=0, where no expansion runs).
            double[] noPreferredSideGeomTofs = { double.NaN, 0.0, -123.0, recordedTof };
            foreach (double geomTof in noPreferredSideGeomTofs)
            {
                IReadOnlyList<double> got =
                    ReaimTofSearch.BuildCandidateTofs(recordedTof, geomTof, targetEccentricity: 0.26);

                Assert.NotEmpty(got);
                Assert.All(got, t => Assert.True(t > 0.0 && !double.IsNaN(t)));
                // No duplicates: the candidate list is genuinely distinct (the expansion ring does not re-emit
                // a base-band probe).
                Assert.Equal(got.Distinct().Count(), got.Count);
                // No preferred side => the expansion widened symmetrically: both a longer and a shorter
                // candidate exist beyond the base band edge.
                Assert.Contains(got, t => t > baseEdgeUp + 1e-6);
                Assert.Contains(got, t => t < baseEdgeDown - 1e-6);
            }
        }

        [Fact]
        public void BuildCandidateTofs_DegenerateRecordedTof_ReturnsEmpty()
        {
            // A NaN / non-positive recorded tof leaves nothing to search; the helper returns empty and the
            // resolver keeps the faithful trajectory (it guards positivity anyway).
            Assert.Empty(ReaimTofSearch.BuildCandidateTofs(double.NaN, 100.0, 0.2));
            Assert.Empty(ReaimTofSearch.BuildCandidateTofs(0.0, 100.0, 0.2));
            Assert.Empty(ReaimTofSearch.BuildCandidateTofs(-5.0, 100.0, 0.2));
        }
    }
}
