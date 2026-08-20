using System;
using System.Collections.Generic;
using Parsek.Reaim;

namespace Parsek.Tests
{
    // The Kerbin->Eeloo PINNED-SCAN geometry of the M-MIS-3 stage-B fixture, modelled headlessly so the
    // band-walk cells (EelooBandWalkTests) can answer the M-MIS-3-BAND-COMPUTED-NOT-EXERCISED question
    // off-game: IS there a departure at which the eccentricity-WIDENED region of ReaimTofSearch's band
    // (|k| > baseSteps) is where the search first succeeds? The debt's open half is the statement "no
    // accepted candidate has ever sat outside the base band" (archive max |k| = 7 against baseSteps 12).
    //
    // WHAT THIS MODELS, AND EXACTLY WHAT IT DOES NOT. ReaimTransferSynthesizer.TrySynthesizeTransfer
    // solves a Lambert conic per candidate tof, measures the transfer plane's inclination, and compares it
    // against ReaimTransferSynthesizer.InclinationBoundDegrees. A candidate ABOVE the bound takes the
    // correction path (state=fired); one UNDER it takes the noop path. That predicate -
    // plane(r1, r2(tof)) vs the bound - is PURE, so this file evaluates it without Unity.
    //
    // IT MODELS THE TILT GATE, NOT ACCEPTANCE, and the distinction was MEASURED rather than reasoned.
    // Acceptance additionally requires PatchedConics to find the encounter, which is Unity-bound. An
    // earlier version of this file assumed a fired correction then dies at that check - true at the M2
    // window-1 geometry, and NOT a law. The 2026-08-15 flight
    // (logs/2026-08-15_1517_M2-periodicity-solver) settled it: of the five departures whose tilt gate
    // first opens outside the base band, the live synthesizer ACCEPTS STEP 0 at two of them (scan indices
    // 0 and 1, whose step-0 inclinations are 35 and 44 deg - the correction fires AND the encounter
    // succeeds) and accepts exactly where this model says at the other three (scan indices 24, 25, 26, at
    // candidates 32, 48, 62). So: TryFirstTiltGateOpening answers "which candidate does the tilt gate
    // first admit", full stop. Where the ANSWER has been confirmed against the live arbiter it is pinned
    // as such, and where it has not, nothing here claims it.
    //
    // FAITHFULNESS IS MEASURED, NOT ASSUMED. EelooBandWalk_Window1InclinationSequence_MatchesTheLiveM2Run
    // reproduces all fifteen candidate inclinations the 2026-08-11 M2-periodicity-solver run logged for
    // the Eeloo member's window 1, to the log's full four-decimal precision, and the window-0 value too.
    // That cell is the licence for every other number in this file, and it survived the 2026-08-15 flight
    // untouched - the correction above narrowed what the model CLAIMS, not what it computes.
    //
    // EPHEMERIS REUSE (deliberate). The two-body Kepler propagation, the Y-up frame relabel and the
    // inclination/plane helpers are EveCycleZeroGeometry's, not copies of them. That file's own header
    // gives the reason - "it lives in its own file so the two cell groups share ONE ephemeris model
    // rather than two copies that can drift apart" - and the reason applies with more force across two
    // targets than across two cell groups. Only Eeloo's elements and the fixture/scan model are new here.
    internal static class EelooBandWalkGeometry
    {
        // --- Stock constants (Kerbol system, KSP 1.12.5). ---
        //
        // The ORBITAL PERIODS are pinned as published stock values rather than derived from the semi-major
        // axes, because the production scan grid is built from CelestialBody.orbit.period and a
        // sma-derived period is 0.02 s different - enough to move ScanDepartureUT(14) 0.006 s off the UT
        // the live run logged. With these constants the grid reproduces that logged UT to 2e-4 s
        // (EelooBandWalk_ScanGrid_ReproducesTheLoggedDepartureUT pins the agreement and its tolerance).
        internal const double KerbinSemiMajorAxis = 13599840256.0;
        internal const double KerbinPeriodSeconds = 9203544.61795353;
        internal const double EelooSemiMajorAxis = 90118820000.0;
        internal const double EelooPeriodSeconds = 156992048.35496;
        internal const double EelooEccentricity = 0.26;          // gates ReaimTofSearch's band width
        internal const double EelooInclinationDegrees = 6.15;     // sets the tilt bound
        internal const double LaunchInclinationDegrees = 0.0;     // Kerbin

        // --- The fixture, mirrored from ReaimEndToEndInGameTest. ---
        // KerbinToEeloo().RecordedTofOffsetFraction (ReaimEndToEndInGameTest.cs:202-215) and the pinned
        // scan constants (ReaimEndToEndInGameTest.cs:68-69). Mirrored rather than referenced because
        // ReaimEndToEndInGameTest is Unity-bound; EelooBandWalk_FixtureConstants_MatchTheInGameFixture
        // pins them against the values the live run emitted, so a drift here is caught by a cell.
        internal const double PinnedScanBaseUT = 5000000.0;
        internal const int ScanSteps = 48;
        internal const double RecordedTofOffsetFraction = 0.12;

        // --- The measured M2 run (2026-08-11_1514_M2-periodicity-solver, member reaim-e2e-eeloo-mid-14). ---
        // KSP.log:11993 (window 0) and :11987/:12006 (window 1). These are LOG VALUES, not derivations.
        internal const double LoggedGeomTofSeconds = 34266107.23053021;
        internal const double LoggedRecordedTofSeconds = 38378040.098193839;
        internal const double LoggedWindow0DepartureUT = 7851536.4285912551;
        internal const double LoggedWindow1DepartureUT = 46958321.734985605;
        internal const int LoggedMidScanIndex = 14;

        // The tilt-correction inc-before values the run logged for window 0 (single candidate, accepted at
        // step 0) and for window 1's fifteen candidates, in BuildCandidateTofs emission order
        // (k = 0, +1, -1, +2, -2, ... +7, -7). KSP.log:11991-12007. The last entry is the accepted one:
        // 6.6469 is the first value under the 6.6500 bound, a 0.0031 deg margin.
        internal const double LoggedWindow0InclinationDegrees = 2.5518;

        internal static IReadOnlyList<double> LoggedWindow1InclinationsDegrees => new[]
        {
            6.8115, 6.8361, 6.7871, 6.8611, 6.7631, 6.8864, 6.7393, 6.9121,
            6.7158, 6.9381, 6.6926, 6.9645, 6.6696, 6.9913, 6.6469,
        };

        internal static EveCycleZeroGeometry.Elements Kerbin => EveCycleZeroGeometry.Kerbin;

        internal static EveCycleZeroGeometry.Elements Eeloo => new EveCycleZeroGeometry.Elements
        {
            SemiMajorAxis = EelooSemiMajorAxis,
            Eccentricity = EelooEccentricity,
            InclinationDegrees = EelooInclinationDegrees,
            LanDegrees = 50.0,
            ArgPeDegrees = 260.0,
            MeanAnomalyAtEpochRadians = 3.14,
        };

        /// <summary>
        /// The geometric Hohmann tof, from the PRODUCT'S OWN helper rather than a re-derivation
        /// (<see cref="TransferWindowMath.HohmannTransferTimeSeconds"/>, the same call
        /// <c>ReaimEndToEndInGameTest.BuildGeometryOrSkip</c> makes at ReaimEndToEndInGameTest.cs:1375).
        /// </summary>
        internal static double GeomTofSeconds =>
            TransferWindowMath.HohmannTransferTimeSeconds(
                KerbinSemiMajorAxis, EelooSemiMajorAxis, EveCycleZeroGeometry.SunMu);

        /// <summary>
        /// The fixture's STAND-IN recorded tof: <c>geomTof * (1 + RecordedTofOffsetFraction)</c>, the same
        /// construction as ReaimEndToEndInGameTest.cs:1385. Displaced +12% so geomTof lands OUTSIDE the
        /// recorded +-6% base band but INSIDE Eeloo's 0.19 scaled band.
        /// </summary>
        internal static double RecordedTofSeconds => GeomTofSeconds * (1.0 + RecordedTofOffsetFraction);

        /// <summary>The Kerbin-Eeloo synodic period, from the product's own helper.</summary>
        internal static double SynodicSeconds =>
            TransferWindowMath.SynodicPeriodSeconds(KerbinPeriodSeconds, EelooPeriodSeconds);

        /// <summary>
        /// The pinned scan grid: <c>PinnedScanBaseUT + synodic*i/ScanSteps</c>, the departure UT
        /// <c>BuildPinnedScanOrSkip</c> evaluates at step <paramref name="i"/>
        /// (ReaimEndToEndInGameTest.cs:1415).
        /// </summary>
        internal static double ScanDepartureUT(int i) => PinnedScanBaseUT + (SynodicSeconds * i) / ScanSteps;

        /// <summary>
        /// The tilt bound this geometry is judged against, from the PRODUCT'S OWN law
        /// (<see cref="ReaimTransferSynthesizer.InclinationBoundDegrees"/>): Kerbin is uninclined and
        /// Eeloo is 6.15 deg, so this is 6.15 + the 0.5 deg tolerance = 6.65, the value the run logged.
        /// </summary>
        internal static double InclinationBoundDegrees =>
            ReaimTransferSynthesizer.InclinationBoundDegrees(LaunchInclinationDegrees, EelooInclinationDegrees);

        /// <summary>
        /// The ORDERED candidate tof list, built by the PRODUCT'S OWN band law and by the builder the
        /// DIRECT departure path selects (<c>ReaimPlaybackResolver.cs:458</c> dispatches on
        /// <c>hasDepartureOverride</c>, false for this fixture): recorded-centered, base +-6% in +k,-k
        /// order, then the eccentricity expansion k=13..38 probing the geomTof side first. 77 candidates.
        /// </summary>
        internal static IReadOnlyList<double> CandidateTofs() =>
            ReaimTofSearch.BuildCandidateTofs(RecordedTofSeconds, GeomTofSeconds, EelooEccentricity);

        /// <summary>
        /// The step index k of candidate <paramref name="candidateIndex"/>, i.e. its offset from the
        /// recorded tof in units of <c>recordedTof * DefaultStepFraction</c>. Derived from the candidate's
        /// own tof rather than from a re-implementation of the builder's emission order, so it stays
        /// correct if that order ever changes.
        /// </summary>
        internal static double StepIndexOf(double candidateTofSeconds)
            => (candidateTofSeconds - RecordedTofSeconds)
               / (RecordedTofSeconds * ReaimTofSearch.DefaultStepFraction);

        /// <summary>
        /// The transfer-plane inclination (degrees) a candidate of flight time
        /// <paramref name="tofSeconds"/> departing at <paramref name="departureUT"/> would carry. UvLambert
        /// returns v1 in span(r1, r2), so the solved conic's plane IS plane(r1, r2) - the H1 mechanism
        /// EveCycleZeroGeometry.PlaneNormalOfEndpoints records, and the reason this needs no solve.
        /// </summary>
        internal static double CandidateInclinationDegrees(double departureUT, double tofSeconds)
        {
            EveCycleZeroGeometry.StateAt(Kerbin, departureUT, out Vector3d r1, out _);
            EveCycleZeroGeometry.StateAt(Eeloo, departureUT + tofSeconds, out Vector3d r2, out _);
            return EveCycleZeroGeometry.InclinationOfNormalDegrees(
                EveCycleZeroGeometry.PlaneNormalOfEndpoints(r1, r2));
        }

        /// <summary>
        /// Walks <see cref="CandidateTofs"/> in order and reports the FIRST candidate the TILT GATE admits
        /// (inclination &lt;= <see cref="InclinationBoundDegrees"/>), i.e. the first to take the noop path.
        /// This is NOT necessarily the candidate the resolver accepts: a candidate the gate sends down the
        /// correction path can still be accepted when PatchedConics finds the encounter, which is exactly
        /// what the live arbiter does at scan indices 0 and 1. Returns false when every candidate sits
        /// above the bound. Pure.
        /// </summary>
        internal static bool TryFirstTiltGateOpening(
            double departureUT, out int candidateIndex, out double tofSeconds,
            out double stepIndex, out double inclinationDegrees)
        {
            IReadOnlyList<double> tofs = CandidateTofs();
            double bound = InclinationBoundDegrees;
            for (int i = 0; i < tofs.Count; i++)
            {
                double inc = CandidateInclinationDegrees(departureUT, tofs[i]);
                if (inc <= bound)
                {
                    candidateIndex = i;
                    tofSeconds = tofs[i];
                    stepIndex = StepIndexOf(tofs[i]);
                    inclinationDegrees = inc;
                    return true;
                }
            }
            candidateIndex = -1;
            tofSeconds = double.NaN;
            stepIndex = double.NaN;
            inclinationDegrees = double.NaN;
            return false;
        }

        /// <summary>
        /// True when a step index sits in the ECCENTRICITY-WIDENED region - the part of the band that only
        /// exists because <c>EccGain * eTarget</c> pushed the half-width past
        /// <see cref="ReaimTofSearch.BaseHalfWidthFraction"/>. Expressed as the debt's own closure
        /// predicate (<c>|dev| &gt; 0.06 * recordedTof</c>) rather than as <c>|k| &gt; 12</c>, so it stays
        /// correct if the step fraction is ever re-pinned.
        /// </summary>
        internal static bool IsOutsideBaseBand(double stepIndex)
            => Math.Abs(stepIndex) * ReaimTofSearch.DefaultStepFraction > ReaimTofSearch.BaseHalfWidthFraction;
    }
}
