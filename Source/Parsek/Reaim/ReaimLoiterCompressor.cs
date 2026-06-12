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
        // A gap LARGER than the contiguity window still merges when the two segments are the SAME orbit to
        // this tight relative tolerance (~0.1%). The recorder/optimizer splits one continuous parking
        // orbit into several OrbitalCheckpoint sections at warp boundaries, leaving small UT gaps between
        // byte-identical-sma segments; merging them keeps ONE parking orbit as ONE loiter run (kept to
        // ~keepRevs total, not keepRevs per chunk). The whole-period cut stays seamless because the merged
        // segments are the same orbit (identical period). A real maneuver shifts sma well past this
        // tolerance, so a genuine orbit change across a gap still ends the run.
        internal const double DefaultSameOrbitRelThreshold = 0.001;

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
        /// One detected loiter run: a maximal contiguous stretch of same-body, non-predicted,
        /// elliptical segments traversing at least one whole revolution of the run's anchor period.
        /// The metadata the per-cycle phasing knob (M4b) solves against: the recorded window, the
        /// repeat period T_rep, and the recorded whole-rev count R.
        /// </summary>
        internal struct LoiterRun
        {
            public double StartUT;
            public double EndUT;
            public double PeriodSeconds;
            public long WholeRevs;
            public string BodyName;
        }

        /// <summary>
        /// Detects loiter runs in <paramref name="segs"/> (must be startUT-sorted). A run is a maximal
        /// contiguous run of same-body, non-predicted, elliptical segments with no &gt;
        /// <paramref name="aStepRelThreshold"/> semi-major-axis step from the run's FIRST segment
        /// (T_rep = the period from that first segment's a). Only runs with at least one whole
        /// revolution (snap-tolerant floor) are returned - a sub-period pass (a transfer arc) is never
        /// a loiter. Pure; <see cref="ComputeCuts"/> is a thin wrapper over this.
        /// </summary>
        internal static List<LoiterRun> DetectRuns(
            IReadOnlyList<OrbitSegment> segs,
            Func<string, double> bodyMu,
            double aStepRelThreshold = DefaultAStepRelThreshold,
            double contiguityEpsilonSeconds = DefaultContiguityEpsilonSeconds,
            double sameOrbitRelThreshold = DefaultSameOrbitRelThreshold)
        {
            var runs = new List<LoiterRun>();
            if (segs == null || segs.Count == 0 || bodyMu == null)
                return runs;

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
                    double aRel = Math.Abs(next.semiMajorAxis - firstA) / Math.Max(1.0, Math.Abs(firstA));
                    if (aRel > aStepRelThreshold)
                        break; // drifted past the threshold from the anchor -> ends the run (T_rep valid)
                    // A gap ends the run UNLESS it is a sampling artifact within the SAME orbit (sma
                    // matches to the tight sameOrbit tolerance): the recorder/optimizer splits one
                    // continuous parking orbit into warp-boundary checkpoint sections with small gaps, and
                    // merging them keeps one parking orbit as ONE loiter run. A real orbit change across a
                    // gap (sma shifted past the tolerance, but still within the 5% a-step) still ends it.
                    if (next.startUT - prevEnd > contiguityEpsilonSeconds && aRel > sameOrbitRelThreshold)
                        break; // a real gap to a (slightly) different orbit -> ends the run
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
                    if (wholeRevs >= 1)
                    {
                        runs.Add(new LoiterRun
                        {
                            StartUT = runStart,
                            EndUT = runEnd,
                            PeriodSeconds = tRep,
                            WholeRevs = wholeRevs,
                            BodyName = body,
                        });
                    }
                }

                i = j + 1;
            }

            return runs;
        }

        /// <summary>
        /// The whole-period cuts that compress each detected loiter run to
        /// <paramref name="keepRevs"/> revolutions. The cut excises <c>(wholeRevs - keepRevs)</c>
        /// whole periods from the run's START (keeping the tail = N revs + remainder, ending at the
        /// recorded run end so the exit phase is preserved). Pure. Returns an empty list when there
        /// are no loiters (then the compressed timeline == the recorded timeline). Behavior is
        /// identical to the pre-M4b inline implementation; the run walk lives in
        /// <see cref="DetectRuns"/> so the per-cycle phasing knob can solve per run.
        /// </summary>
        internal static List<GhostPlaybackLogic.LoopCut> ComputeCuts(
            IReadOnlyList<OrbitSegment> segs,
            Func<string, double> bodyMu,
            int keepRevs = DefaultKeepRevs,
            double aStepRelThreshold = DefaultAStepRelThreshold,
            double contiguityEpsilonSeconds = DefaultContiguityEpsilonSeconds,
            double sameOrbitRelThreshold = DefaultSameOrbitRelThreshold)
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>();
            if (segs == null || segs.Count == 0 || bodyMu == null || keepRevs < 0)
                return cuts;
            List<LoiterRun> runs = DetectRuns(
                segs, bodyMu, aStepRelThreshold, contiguityEpsilonSeconds, sameOrbitRelThreshold);
            for (int r = 0; r < runs.Count; r++)
            {
                if (runs[r].WholeRevs > keepRevs)
                {
                    double cutLength = (runs[r].WholeRevs - keepRevs) * runs[r].PeriodSeconds;
                    cuts.Add(new GhostPlaybackLogic.LoopCut
                    {
                        StartUT = runs[r].StartUT,
                        LengthSeconds = cutLength,
                    });
                }
            }
            return cuts;
        }
    }
}
