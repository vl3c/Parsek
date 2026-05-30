using System;
using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Detects loiter runs in a recorded re-aim mission and computes the whole-period cuts that compress
    // them, plus the recorded->compressed UT map (docs/dev/plans/reaim-loiter-compression.md). PURE:
    // operates on OrbitSegment structs + a body->mu lookup, so all detection / cut / map logic is
    // unit-testable. The builder applies the cuts by time-shifting each member's segments onto the
    // compressed clock; the shared span clock is never touched.
    //
    // Model (section 2): a loiter is a closed orbit traversed > N revolutions; compress it to N revs by
    // excising an EXACT integer number of periods, which is position-/velocity-continuous (seamless,
    // section 2.1: getPositionAtUT attaches the recorded phase to the LIVE body, and a cut advances
    // RECORDED time by k*T while live time advances one frame). A transfer arc is a single pass
    // (duration < period) -> never a loiter -> kept.
    internal static class ReaimLoiterCompressor
    {
        // Defaults (section 4): keep ~1 revolution; a run ends at a > 5% semi-major-axis step (a
        // deliberate orbit-raise) so T_rep stays valid and the cut lands on a true period; segments are
        // contiguous when the UT gap is under a second (consecutive on-rails captures of one coast).
        internal const int DefaultKeepRevs = 1;
        internal const double DefaultAStepRelThreshold = 0.05;
        internal const double DefaultContiguityEpsilonSeconds = 1.0;

        /// <summary>An excised whole-period interval of a loiter run: recorded
        /// [<see cref="StartUT"/>, <see cref="StartUT"/> + <see cref="LengthSeconds"/>] is removed from
        /// the loop timeline.</summary>
        internal struct LoiterCut
        {
            public double StartUT;
            public double LengthSeconds;
            public double EndUT => StartUT + LengthSeconds;
        }

        /// <summary>
        /// Orbital period <c>2*pi*sqrt(a^3/mu)</c> in seconds for an ELLIPTICAL orbit (a &gt; 0, finite
        /// mu &gt; 0). Returns NaN for a hyperbolic/parabolic (a &lt;= 0 in the KSP convention),
        /// degenerate, or unknown-mu segment - i.e. anything that is not a closed repeatable orbit and so
        /// is never a loiter candidate. Pure.
        /// </summary>
        internal static double OrbitalPeriod(double semiMajorAxis, double mu)
        {
            if (double.IsNaN(semiMajorAxis) || double.IsInfinity(semiMajorAxis) || semiMajorAxis <= 0.0
                || double.IsNaN(mu) || double.IsInfinity(mu) || mu <= 0.0)
                return double.NaN;
            return 2.0 * Math.PI * Math.Sqrt(semiMajorAxis * semiMajorAxis * semiMajorAxis / mu);
        }

        /// <summary>
        /// Detects loiter runs in <paramref name="segs"/> (must be startUT-sorted) and returns the
        /// whole-period cuts that compress each to <paramref name="keepRevs"/> revolutions. A loiter run
        /// is a maximal contiguous run of same-body, non-predicted, elliptical segments with no &gt;
        /// <paramref name="aStepRelThreshold"/> semi-major-axis step, whose TOTAL duration exceeds
        /// <c>keepRevs * T_rep</c> (T_rep = the period from the run's first segment's a). The cut excises
        /// <c>(wholeRevs - keepRevs)</c> whole periods from the run's start (keeping the tail = N revs +
        /// remainder, ending at the recorded run end so the exit phase is preserved). Pure. Returns an
        /// empty list when there are no loiters (then the compressed timeline == the recorded timeline).
        /// </summary>
        internal static List<LoiterCut> ComputeCuts(
            IReadOnlyList<OrbitSegment> segs,
            Func<string, double> bodyMu,
            int keepRevs = DefaultKeepRevs,
            double aStepRelThreshold = DefaultAStepRelThreshold,
            double contiguityEpsilonSeconds = DefaultContiguityEpsilonSeconds)
        {
            var cuts = new List<LoiterCut>();
            if (segs == null || segs.Count == 0 || bodyMu == null || keepRevs < 0)
                return cuts;

            int i = 0;
            while (i < segs.Count)
            {
                OrbitSegment seg = segs[i];
                double period = OrbitalPeriod(seg.semiMajorAxis, bodyMu(seg.bodyName));
                // Not a loiter candidate (predicted / non-elliptical / unknown body): skip; it also ends
                // any run (a body change, a burn, an atmospheric pass, a ballistic tail).
                if (seg.isPredicted || string.IsNullOrEmpty(seg.bodyName)
                    || double.IsNaN(period) || period <= 0.0)
                {
                    i++;
                    continue;
                }

                // Extend the run while contiguous, same body, elliptical, no a-step. The a-step is
                // measured against the run's FIRST a (the anchor T_rep is computed from), NOT the
                // previous segment: a slow gradual drift (sub-threshold per step) must still end the run
                // once it has drifted past the threshold from the anchor, or T_rep would be wrong for the
                // late segments and the cut would land off a true period (review M2).
                string body = seg.bodyName;
                double firstA = seg.semiMajorAxis;
                double runStart = seg.startUT;
                double runEnd = seg.endUT;
                double prevEnd = seg.endUT;
                int j = i;
                while (j + 1 < segs.Count)
                {
                    OrbitSegment next = segs[j + 1];
                    if (next.isPredicted || next.bodyName != body)
                        break;
                    double nextPeriod = OrbitalPeriod(next.semiMajorAxis, bodyMu(next.bodyName));
                    if (double.IsNaN(nextPeriod) || nextPeriod <= 0.0)
                        break; // non-elliptical ends the run
                    if (next.startUT - prevEnd > contiguityEpsilonSeconds)
                        break; // a gap ends the run
                    double aRel = Math.Abs(next.semiMajorAxis - firstA) / Math.Max(1.0, Math.Abs(firstA));
                    if (aRel > aStepRelThreshold)
                        break; // drifted past the threshold from the anchor -> ends the run (T_rep valid)
                    j++;
                    runEnd = next.endUT;
                    prevEnd = next.endUT;
                }

                double tRep = OrbitalPeriod(firstA, bodyMu(body));
                double dur = runEnd - runStart;
                if (tRep > 0.0 && !double.IsNaN(tRep))
                {
                    // Snap-tolerant floor (section 4.3): a loiter of EXACTLY k periods accumulates
                    // floating-point rounding so dur/T lands a hair under k -> a raw Floor would lose a
                    // whole revolution. The epsilon snaps dur/T up to the nearest integer when it is
                    // within ~1e-6 rev, which also keeps cutLength = (wholeRevs - keepRevs)*T an exact
                    // integer number of periods (the seam-exactness requirement of section 2.1). A real
                    // partial revolution (remainder >> 1e-6 rev) is unaffected.
                    long wholeRevs = (long)Math.Floor(dur / tRep + 1e-6);
                    if (wholeRevs > keepRevs)
                    {
                        double cutLength = (wholeRevs - keepRevs) * tRep;
                        cuts.Add(new LoiterCut { StartUT = runStart, LengthSeconds = cutLength });
                    }
                }

                i = j + 1;
            }

            return cuts;
        }

        /// <summary>
        /// The compressed UT for a recorded UT <paramref name="t"/>: <c>t - sum of the parts of each cut
        /// at or before t</c>. Monotonic non-decreasing; a recorded UT INSIDE a cut interval collapses to
        /// the cut's start (the cut interval maps to a single compressed instant). For an empty cut list
        /// this is the identity. Pure.
        /// </summary>
        internal static double CompressUT(double t, IReadOnlyList<LoiterCut> cuts)
        {
            if (cuts == null || cuts.Count == 0)
                return t;
            double removed = 0.0;
            for (int c = 0; c < cuts.Count; c++)
            {
                LoiterCut cut = cuts[c];
                if (t <= cut.StartUT)
                    continue;
                // Overlap of the cut with (-inf, t]: full cut when t is past it, partial when t is inside.
                double overlapEnd = t < cut.EndUT ? t : cut.EndUT;
                removed += overlapEnd - cut.StartUT;
            }
            return t - removed;
        }
    }
}
