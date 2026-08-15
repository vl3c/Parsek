using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // M-MIS-3-BAND-COMPUTED-NOT-EXERCISED, the behavioural half: the eccentricity-gated tof band is pinned
    // as COMPUTED at Eeloo's e=0.26 (V12A armed `halfWidthFraction=0.1900`) but has never been WALKED -
    // across the whole log archive no accepted candidate has sat OUTSIDE the base band (79 devFromRecorded
    // emissions, 4 genuine non-zero accepts, max |k| = 7 against baseSteps 12).
    //
    // WHAT THESE CELLS ARE FOR. They answer, unattended and off-game, whether a departure exists at which
    // the search first succeeds in the eccentricity-WIDENED region, and they are the regression floor for
    // the in-game cell that confirms it. The order below is the discipline the answer needs:
    //
    //   1. CALIBRATION  - the model reproduces what the live run measured, to the log's own precision.
    //                     Without this cell every number in the others is an assertion about a model.
    //   2. GRID + FIXTURE - the scan grid and the fixture constants agree with the live run's.
    //   3. NEGATIVE CONTROL - at the departure the product CURRENTLY drives, the answer is "step 0",
    //                     i.e. these cells do not fire on today's geometry and would not have found a
    //                     walk that was not there.
    //   4. THE MEASUREMENT - five of the 48 pinned scan departures open the tilt gate only outside the
    //                     base band, at |k| = 16..37.
    //
    // WHAT THEY DO NOT PROVE. The tilt gate is pure and is modelled here; final acceptance additionally
    // needs the PatchedConics encounter check, which is Unity-bound. These cells predict WHICH CANDIDATE
    // THE TILT GATE FIRST ADMITS. The in-game cell is the arbiter of whether it is then accepted.
    public class EelooBandWalkTests
    {
        // The log quotes inclinations to four decimals; the model agrees far tighter than that, so four
        // is the LOG's precision, not a slack tolerance.
        private const int LoggedInclinationDecimals = 4;

        // ----- 1. CALIBRATION: the licence for every other number in this file. -----

        [Fact]
        public void EelooBandWalk_Window1InclinationSequence_MatchesTheLiveM2Run()
        {
            IReadOnlyList<double> tofs = EelooBandWalkGeometry.CandidateTofs();
            IReadOnlyList<double> logged = EelooBandWalkGeometry.LoggedWindow1InclinationsDegrees;

            for (int i = 0; i < logged.Count; i++)
            {
                double modelled = EelooBandWalkGeometry.CandidateInclinationDegrees(
                    EelooBandWalkGeometry.LoggedWindow1DepartureUT, tofs[i]);
                Assert.Equal(logged[i], modelled, LoggedInclinationDecimals);
            }
        }

        [Fact]
        public void EelooBandWalk_Window0Inclination_MatchesTheLiveM2Run()
        {
            // Window 0 accepted at step 0 (devFromRecorded=0s) because its natural plane sits far under the
            // bound - a SECOND, independent departure the model must reproduce, so the window-1 agreement
            // cannot be a coincidence of one geometry.
            double modelled = EelooBandWalkGeometry.CandidateInclinationDegrees(
                EelooBandWalkGeometry.LoggedWindow0DepartureUT,
                EelooBandWalkGeometry.CandidateTofs()[0]);

            Assert.Equal(EelooBandWalkGeometry.LoggedWindow0InclinationDegrees, modelled,
                LoggedInclinationDecimals);
        }

        [Fact]
        public void EelooBandWalk_Window1_ReproducesTheArchivesOnlyEelooAccept()
        {
            // The archive's single Eeloo off-step-0 accept: k = -7, devFromRecorded = -1343231.4034367874s
            // = 0.035 of the recorded tof, INSIDE the base band. The model must land on that candidate, not
            // merely somewhere plausible - this is what ties it to the debt's own measurement.
            Assert.True(EelooBandWalkGeometry.TryFirstTiltGateOpening(
                EelooBandWalkGeometry.LoggedWindow1DepartureUT,
                out int candidateIndex, out _, out double stepIndex, out double inc));

            Assert.Equal(14, candidateIndex);
            Assert.Equal(-7.0, stepIndex, 6);
            Assert.Equal(6.6469, inc, LoggedInclinationDecimals);
            Assert.False(EelooBandWalkGeometry.IsOutsideBaseBand(stepIndex));
        }

        // ----- 2. THE GRID AND THE FIXTURE agree with the live run. -----

        [Fact]
        public void EelooBandWalk_ScanGrid_ReproducesTheLoggedDepartureUT()
        {
            // BuildPinnedScanOrSkip's grid at the mid-band index the live run picked. The residual is 2e-4 s
            // (3e-11 relative) because the stock orbital periods are pinned to their published digits while
            // KSP carries CelestialBody.orbit.period to full double precision. A millisecond tolerance is
            // three orders looser than the residual and three orders tighter than one probe step (191890 s),
            // so it cannot mask a grid that is actually wrong.
            double modelled = EelooBandWalkGeometry.ScanDepartureUT(EelooBandWalkGeometry.LoggedMidScanIndex);

            Assert.InRange(
                Math.Abs(modelled - EelooBandWalkGeometry.LoggedWindow0DepartureUT), 0.0, 0.001);
        }

        [Fact]
        public void EelooBandWalk_FixtureConstants_MatchTheInGameFixture()
        {
            // geomTof / recordedTof against the values the run logged. The ~0.08 s residuals are the
            // published semi-major axes' rounding; 1 s is far inside one probe step.
            Assert.InRange(
                Math.Abs(EelooBandWalkGeometry.GeomTofSeconds
                         - EelooBandWalkGeometry.LoggedGeomTofSeconds), 0.0, 1.0);
            Assert.InRange(
                Math.Abs(EelooBandWalkGeometry.RecordedTofSeconds
                         - EelooBandWalkGeometry.LoggedRecordedTofSeconds), 0.0, 1.0);

            // The tilt bound the run logged, and the band the run logged, both from the product's own laws.
            Assert.Equal(6.65, EelooBandWalkGeometry.InclinationBoundDegrees, 10);
            Assert.Equal(0.19,
                ReaimTofSearch.HalfWidthFraction(EelooBandWalkGeometry.EelooEccentricity), 10);

            // 1 + 2*12 base + 2*26 expansion. The expansion is 26 STEPS = 52 CANDIDATES: the distinction
            // the todo entry's earlier text conflated.
            Assert.Equal(77, EelooBandWalkGeometry.CandidateTofs().Count);
        }

        // ----- 3. NEGATIVE CONTROL: nothing fires on the departure the product drives today. -----

        [Fact]
        public void EelooBandWalk_MidBandPick_OpensAtStep0_SoTheseCellsDoNotFireOnTodaysGeometry()
        {
            // ReaimEndToEndInGameTest picks midIdx = CenterOfLongestRunIndex(scan, cyclic: true), the centre
            // of the longest run of departures whose STEP-0 tof synthesizes - by construction the most
            // comfortable departure in the band. This cell is why the band was never walked, stated as a
            // test: at scan index 14 the very first candidate is admitted, so no expansion step is ever
            // reached, and a measurement that fired here would be measuring nothing.
            Assert.True(EelooBandWalkGeometry.TryFirstTiltGateOpening(
                EelooBandWalkGeometry.ScanDepartureUT(EelooBandWalkGeometry.LoggedMidScanIndex),
                out int candidateIndex, out _, out double stepIndex, out _));

            Assert.Equal(0, candidateIndex);
            Assert.Equal(0.0, stepIndex, 6);
            Assert.False(EelooBandWalkGeometry.IsOutsideBaseBand(stepIndex));
        }

        // ----- 4. THE MEASUREMENT. -----

        [Fact]
        public void EelooBandWalk_FivePinnedScanDepartures_OpenOnlyOutsideTheBaseBand()
        {
            // scanIdx -> (candidate index, step index k, |dev| as a fraction of the recorded tof).
            // |dev|/recordedTof is EXACT by construction (|k| * DefaultStepFraction), which is why it is
            // pinned to full equality while the inclinations are not pinned here at all - the claim is
            // WHERE the gate opens, not what the angle was.
            var expected = new (int ScanIndex, int CandidateIndex, double StepIndex, double DevFraction)[]
            {
                (0,  58, +29.0, 0.145),
                (1,  74, +37.0, 0.185),
                (24, 32, +16.0, 0.080),
                (25, 48, +24.0, 0.120),
                (26, 62, +31.0, 0.155),
            };

            foreach (var e in expected)
            {
                Assert.True(EelooBandWalkGeometry.TryFirstTiltGateOpening(
                        EelooBandWalkGeometry.ScanDepartureUT(e.ScanIndex),
                        out int candidateIndex, out _, out double stepIndex, out _),
                    $"scan index {e.ScanIndex.ToString(CultureInfo.InvariantCulture)} must open the tilt gate");

                Assert.Equal(e.CandidateIndex, candidateIndex);
                Assert.Equal(e.StepIndex, stepIndex, 6);

                double devFraction = Math.Abs(stepIndex) * ReaimTofSearch.DefaultStepFraction;
                Assert.Equal(e.DevFraction, devFraction, 10);

                // THE CLOSURE PREDICATE, in the debt's own words.
                Assert.True(devFraction > ReaimTofSearch.BaseHalfWidthFraction,
                    $"scan index {e.ScanIndex.ToString(CultureInfo.InvariantCulture)} must open OUTSIDE the base band");
                Assert.True(EelooBandWalkGeometry.IsOutsideBaseBand(stepIndex));

                // ...and still inside the eccentricity-scaled band, i.e. the widening is what opened it.
                Assert.True(devFraction <= ReaimTofSearch.HalfWidthFraction(EelooBandWalkGeometry.EelooEccentricity),
                    $"scan index {e.ScanIndex.ToString(CultureInfo.InvariantCulture)} must stay inside the scaled band");
            }
        }

        [Fact]
        public void EelooBandWalk_ExactlyFiveOf48ScanDepartures_OpenOutsideTheBaseBand()
        {
            // The census, so the five above are known to be the WHOLE set rather than five that happened to
            // be looked at - and so a future re-pin of the band law, the tolerance or the stock ephemeris
            // moves this count rather than silently changing which departures qualify.
            var outsideBase = new List<int>();
            var declined = new List<int>();

            for (int i = 0; i < EelooBandWalkGeometry.ScanSteps; i++)
            {
                if (!EelooBandWalkGeometry.TryFirstTiltGateOpening(
                        EelooBandWalkGeometry.ScanDepartureUT(i), out _, out _, out double stepIndex, out _))
                {
                    declined.Add(i);
                    continue;
                }
                if (EelooBandWalkGeometry.IsOutsideBaseBand(stepIndex))
                    outsideBase.Add(i);
            }

            Assert.Equal(new[] { 0, 1, 24, 25, 26 }, outsideBase.ToArray());

            // Two departures admit NO candidate at all - the window would decline to faithful. Pinned
            // because it is the other end of the same mechanism: too much inclination excess and 38 steps
            // are not enough to walk under the bound.
            Assert.Equal(new[] { 2, 3 }, declined.ToArray());
        }

        [Fact]
        public void EelooBandWalk_TheExpansionIsWhatOpensThem_ZeroEccentricityWouldNotReach()
        {
            // The counterfactual that makes the measurement a statement about the ECCENTRICITY GAIN rather
            // than about a wide band: with eTarget = 0 the candidate set is the base band only, and none of
            // the five departures' opening candidates exist in it.
            IReadOnlyList<double> baseBandOnly = ReaimTofSearch.BuildCandidateTofs(
                EelooBandWalkGeometry.RecordedTofSeconds, EelooBandWalkGeometry.GeomTofSeconds,
                targetEccentricity: 0.0);

            Assert.Equal(25, baseBandOnly.Count); // 1 + 2*12

            foreach (int scanIndex in new[] { 0, 1, 24, 25, 26 })
            {
                Assert.True(EelooBandWalkGeometry.TryFirstTiltGateOpening(
                    EelooBandWalkGeometry.ScanDepartureUT(scanIndex),
                    out int candidateIndex, out _, out _, out _));

                Assert.True(candidateIndex >= baseBandOnly.Count,
                    $"scan index {scanIndex.ToString(CultureInfo.InvariantCulture)} must open at a candidate " +
                    "the zero-eccentricity band does not contain");
            }
        }
    }
}
