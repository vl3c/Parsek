using System;
using System.Collections.Generic;

namespace Parsek.Reaim
{
    // Stage B of re-aim eccentric-target tof reliability (docs/dev/plans/reaim-eccentric-tof-reliability.md
    // section 4.1). PURE decision logic, NO Unity, plain doubles, so the regression contract (the four
    // invariants below) is xUnit-provable without a live KSP.
    //
    // The problem: ReaimPlaybackResolver.BuildWindowSegments solves a Lambert transfer per synodic window,
    // searching time-of-flight around the RECORDED tof with a fixed +-6% band. An eccentric target (Eeloo
    // ecc ~0.26, Moho ~0.2) sits at a different orbital radius each window, so the geometrically-required
    // tof routinely drifts outside the recorded +-6% band and the window declines to the faithful recorded
    // trajectory. This helper widens the SEARCH (never the acceptance - every candidate still runs the full
    // synthesizer guard chain) toward the GEOMETRIC Hohmann time, gated and bounded by the target's
    // eccentricity so a low-eccentricity target is byte-identical to today.
    //
    // INVARIANTS (the regression contract; each is asserted in ReaimTofSearchTests):
    //   (a) at eTarget = 0 the candidate set is IDENTICAL to today's recorded-centered +-6% set;
    //   (b) the recorded tof is ALWAYS the first candidate (window-0 / the recorded geometry must always
    //       be reachable);
    //   (c) every candidate stays within <= MaxHalfWidthFraction of the recorded tof (bounded; no runaway);
    //   (d) for eTarget > 0 the candidate set EXTENDS the search beyond the base band, biased toward geomTof.
    //
    // The band is centered on the RECORDED tof (so step 0 is always the recorded tof, invariant b), and the
    // eccentricity term EXTENDS the band, probing the side geomTof lies on FIRST so the search reaches toward
    // the geometric center without ever moving the band off the recorded tof. The expansion is gated on
    // eTarget > 0 (the scaled half-width exceeding the base band), NOT on where geomTof lies: a known geomTof
    // only chooses which side is probed first, it does not gate whether the widening happens. (When geomTof is
    // NaN / non-positive / equal to the recorded tof there is no preferred side and the expansion probes both
    // sides symmetrically, exactly like the base band.)
    internal static class ReaimTofSearch
    {
        // --- The band law constants. ---
        //
        // BaseHalfWidthFraction reproduces today's search EXACTLY: SearchMaxSteps(12) * TofSearchStepFraction(0.005)
        // = 0.06 of the recorded tof = +-6%. A target with eTarget = 0 gets only this base band, so the
        // search is byte-identical to today for a circular target (invariant a). DO NOT change this without
        // re-deriving the zero-eccentricity equivalence.
        internal const double BaseHalfWidthFraction = 0.06;

        // DefaultStepFraction is the fine probe grid spacing as a fraction of the recorded tof, matching the
        // resolver's TofSearchStepFraction = 0.005 (~0.4 day for Kerbin->Duna). Kept fine so a wider band does
        // NOT coarsen the search resolution; the step count rises with the band instead.
        internal const double DefaultStepFraction = 0.005;

        // --- MEASURE-FIRST PLACEHOLDERS (plan open questions 1-2). ---
        // These two constants set how far the band widens per unit of target eccentricity and the hard cap on
        // that widening. They are PLACEHOLDERS chosen for reasonableness, NOT measured values: the plan
        // mandates pinning them against an in-game Eeloo fixture measurement (the failing-window-first
        // discipline) before they are trusted. The tests assert the INVARIANTS (a-d), not these specific
        // numbers, so these may be re-pinned by the Eeloo measurement without breaking the test suite.
        //
        // SECOND CONSUMER (F2): HalfWidthFraction now ALSO governs the parking-departure path via
        // BuildParkingCandidateTofs (the band CENTERED on geomTof, no recorded-tof seed). A future re-pin must
        // weigh BOTH consumers: a tighter cap shrinks how far a near-Hohmann two-burn departure can stretch its
        // tof off geomTof (and thus how early the capture re-times), not just the eccentric-target reach.
        //
        // EccGain: additional half-width fraction added per unit of target eccentricity. ~0.5 means an Eeloo
        // (ecc ~0.26) target widens by ~0.13 over the base, reaching ~0.19 before the cap - in the
        // neighbourhood of the geometric drift a high-eccentricity target shows across a synodic period.
        // PLACEHOLDER - pin against the Eeloo fixture.
        internal const double EccGain = 0.5;

        // MaxHalfWidthFraction: hard cap on the total half-width fraction (invariant c). Bounds the widening
        // so an arbitrarily eccentric target cannot reintroduce the knife-edge tof WIDENING M-MIS-1 refused.
        // ~0.20 is a candidate to be pinned by the Eeloo measurement. PLACEHOLDER - pin against the Eeloo fixture.
        internal const double MaxHalfWidthFraction = 0.20;

        /// <summary>
        /// The eccentricity-scaled band half-width as a FRACTION of the recorded tof:
        /// <c>clamp(BaseHalfWidthFraction + EccGain * targetEccentricity, BaseHalfWidthFraction, MaxHalfWidthFraction)</c>.
        /// At <paramref name="targetEccentricity"/> = 0 this is exactly <see cref="BaseHalfWidthFraction"/>
        /// (today's +-6%); it grows with eccentricity and is hard-capped at <see cref="MaxHalfWidthFraction"/>.
        /// Negative / NaN eccentricity clamps to the base band (fail-safe; an eccentricity is never negative,
        /// but a degenerate body read must not narrow or NaN-poison the search). Pure.
        /// </summary>
        internal static double HalfWidthFraction(double targetEccentricity)
        {
            double e = targetEccentricity;
            if (double.IsNaN(e) || e < 0.0)
                e = 0.0;
            double f = BaseHalfWidthFraction + EccGain * e;
            if (f < BaseHalfWidthFraction)
                f = BaseHalfWidthFraction;
            if (f > MaxHalfWidthFraction)
                f = MaxHalfWidthFraction;
            return f;
        }

        /// <summary>
        /// Builds the ORDERED candidate time-of-flight list the resolver should try, in priority order:
        /// step 0 = <paramref name="recordedTofSeconds"/> (the recorded geometry, always first - invariant b),
        /// then the recorded-centered +-<see cref="BaseHalfWidthFraction"/> probes in today's exact order
        /// (+1, -1, +2, -2, ... - invariant a at eTarget=0), then, ONLY when the eccentricity-scaled band is
        /// wider than the base band, the bounded expansion steps biased toward <paramref name="geomTofSeconds"/>
        /// (invariant d). Every candidate stays within <see cref="MaxHalfWidthFraction"/> of the recorded tof
        /// (invariant c). Non-positive tofs are dropped (the resolver only solves positive tofs). Pure.
        /// </summary>
        /// <param name="recordedTofSeconds">The recorded (schedule) tof. The band center and step-0 candidate.</param>
        /// <param name="geomTofSeconds">The geometric Hohmann tof for this mission's radii. The expansion
        /// reaches toward this; if it is NaN / non-positive the expansion has no preferred side and still
        /// probes both sides symmetrically.</param>
        /// <param name="targetEccentricity">Target body eccentricity; gates the band width (0 => base band only).</param>
        /// <param name="stepFraction">Probe grid spacing as a fraction of the recorded tof
        /// (<see cref="DefaultStepFraction"/> matches the resolver). Non-positive falls back to the default.</param>
        internal static IReadOnlyList<double> BuildCandidateTofs(
            double recordedTofSeconds, double geomTofSeconds, double targetEccentricity,
            double stepFraction = DefaultStepFraction)
        {
            var candidates = new List<double>();

            // A degenerate recorded tof leaves nothing to search; the resolver guards positivity anyway.
            if (double.IsNaN(recordedTofSeconds) || recordedTofSeconds <= 0.0)
                return candidates;

            if (double.IsNaN(stepFraction) || stepFraction <= 0.0)
                stepFraction = DefaultStepFraction;

            double step = recordedTofSeconds * stepFraction;

            // Step 0: the recorded tof, ALWAYS first (invariant b). This is exactly today's s==0 probe.
            AddIfPositive(candidates, recordedTofSeconds);

            // The base band: today's recorded-centered +-BaseHalfWidthFraction in +k,-k order (invariant a).
            // baseSteps = BaseHalfWidthFraction / stepFraction = 12 at the default 0.06 / 0.005. Rounded so a
            // changed stepFraction still lands the band edge on BaseHalfWidthFraction.
            int baseSteps = StepsForFraction(BaseHalfWidthFraction, stepFraction);
            for (int k = 1; k <= baseSteps; k++)
            {
                AddIfPositive(candidates, recordedTofSeconds + k * step);
                AddIfPositive(candidates, recordedTofSeconds - k * step);
            }

            // The eccentricity-gated expansion (invariants c, d). Only runs when the scaled band is wider than
            // the base band, i.e. eTarget > 0. The expansion steps (baseSteps+1 .. maxSteps) extend the band;
            // at each step the candidate on the side geomTof lies on is probed FIRST so the search reaches
            // toward the geometric center. maxSteps is bounded by MaxHalfWidthFraction (invariant c).
            double halfWidthFraction = HalfWidthFraction(targetEccentricity);
            int maxSteps = StepsForFraction(halfWidthFraction, stepFraction);
            if (maxSteps > baseSteps)
            {
                // The side geomTof lies on relative to the recorded tof: +1 (longer tof) or -1 (shorter).
                // NaN / non-positive / equal geomTof => no preferred side; probe + then - as the base band does.
                int towardSign = +1;
                if (!double.IsNaN(geomTofSeconds) && geomTofSeconds > 0.0)
                {
                    if (geomTofSeconds < recordedTofSeconds)
                        towardSign = -1;
                    else
                        towardSign = +1;
                }

                for (int k = baseSteps + 1; k <= maxSteps; k++)
                {
                    AddIfPositive(candidates, recordedTofSeconds + towardSign * k * step);
                    AddIfPositive(candidates, recordedTofSeconds - towardSign * k * step);
                }
            }

            return candidates;
        }

        /// <summary>
        /// Builds the ORDERED candidate time-of-flight list for the HELIOCENTRIC-PARKING-DEPARTURE path,
        /// CENTERED on the GEOMETRIC Hohmann time <paramref name="geomTofSeconds"/> (NOT the recorded tof).
        /// A two-burn departure's recorded tof is whatever the player flew (for the s15 mission ~1.44x the
        /// Hohmann time), which makes a degenerate conic when forced from the re-phased park-end; the
        /// geometric Hohmann time is the tof a clean transfer from that geometry actually takes, so step 0 is
        /// geomTof. The band is symmetric +-<see cref="HalfWidthFraction"/>(eTarget) of geomTof (the same band
        /// law as <see cref="BuildCandidateTofs"/> - base +-6% widening with target eccentricity, hard-capped
        /// at <see cref="MaxHalfWidthFraction"/>), probed in +k,-k order. Mirrors BuildCandidateTofs'
        /// structure exactly EXCEPT the center is geomTof and there is no recorded-tof seeding (the recorded
        /// tof is deliberately unused on this path). A NaN / non-positive geomTof leaves nothing to search =>
        /// empty list (fail closed - the caller declines to faithful). Non-positive tofs are dropped. Pure.
        /// </summary>
        /// <param name="geomTofSeconds">The geometric Hohmann tof for this mission's radii. The band CENTER
        /// and step-0 candidate. NaN / non-positive => empty (fail closed).</param>
        /// <param name="targetEccentricity">Target body eccentricity; widens the band (0 => base +-6%).</param>
        /// <param name="stepFraction">Probe grid spacing as a fraction of geomTof
        /// (<see cref="DefaultStepFraction"/> matches the resolver). Non-positive falls back to the default.</param>
        internal static IReadOnlyList<double> BuildParkingCandidateTofs(
            double geomTofSeconds, double targetEccentricity,
            double stepFraction = DefaultStepFraction)
        {
            var candidates = new List<double>();

            // A degenerate geomTof leaves nothing to search; fail closed (the caller declines to faithful).
            if (double.IsNaN(geomTofSeconds) || geomTofSeconds <= 0.0)
                return candidates;

            if (double.IsNaN(stepFraction) || stepFraction <= 0.0)
                stepFraction = DefaultStepFraction;

            double step = geomTofSeconds * stepFraction;

            // Step 0: the GEOMETRIC center, ALWAYS first - NOT the recorded tof. This is the F2 fix's core:
            // the parking-departure transfer is solved from the re-phased park-end with the Hohmann tof.
            AddIfPositive(candidates, geomTofSeconds);

            // Symmetric +-HalfWidthFraction(eTarget) band of geomTof in +k,-k order. Reuses the same band law
            // (base +-6% widening with eccentricity, capped at MaxHalfWidthFraction - invariant c). No
            // preferred-side bias: the band is centered on geomTof, so both sides are probed symmetrically.
            double halfWidthFraction = HalfWidthFraction(targetEccentricity);
            int maxSteps = StepsForFraction(halfWidthFraction, stepFraction);
            for (int k = 1; k <= maxSteps; k++)
            {
                AddIfPositive(candidates, geomTofSeconds + k * step);
                AddIfPositive(candidates, geomTofSeconds - k * step);
            }

            return candidates;
        }

        // Steps needed for a band edge at 'fraction' of the recorded tof, given 'stepFraction' grid spacing.
        // Rounded to nearest so the integer grid lands the band edge on 'fraction' even when stepFraction does
        // not divide it evenly. At least 0 (a sub-step fraction yields no probe ring).
        private static int StepsForFraction(double fraction, double stepFraction)
        {
            if (stepFraction <= 0.0)
                return 0;
            int n = (int)Math.Round(fraction / stepFraction, MidpointRounding.AwayFromZero);
            return n < 0 ? 0 : n;
        }

        private static void AddIfPositive(List<double> list, double tof)
        {
            if (tof > 0.0)
                list.Add(tof);
        }
    }
}
