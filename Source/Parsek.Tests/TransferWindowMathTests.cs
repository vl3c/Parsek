using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests
{
    // Phase 1 of re-aim (docs/dev/plans/reaim-interplanetary-transfers.md): the pure transfer-window
    // math, validated against textbook interplanetary numbers (Earth->Mars: ~44 deg departure lead,
    // ~259-day transfer, ~780-day / ~25.6-month synodic) and stock Kerbin->Duna. Each test states the
    // regression it guards.
    public class TransferWindowMathTests
    {
        // Earth-Mars (SI), the canonical sanity case.
        private const double EarthSma = 1.496e11, MarsSma = 2.279e11, MuSun = 1.327e20;
        private const double EarthPeriod = 3.156e7, MarsPeriod = 5.935e7; // ~1 yr, ~1.88 yr
        // Stock Kerbol.
        private const double KerbinSma = 13599840256.0, DunaSma = 20726155264.0, MuKerbol = 1.1723328e18;
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;

        [Fact]
        public void HohmannPhaseAngle_EarthToMars_About44Degrees()
        {
            // Guards the departure-lead formula against the textbook Earth->Mars ~44 deg.
            double phase = TransferWindowMath.HohmannPhaseAngleTargetDegrees(EarthSma, MarsSma);
            Assert.InRange(phase, 43.0, 45.5);
        }

        [Fact]
        public void HohmannPhaseAngle_KerbinToDuna_About44Degrees()
        {
            double phase = TransferWindowMath.HohmannPhaseAngleTargetDegrees(KerbinSma, DunaSma);
            Assert.InRange(phase, 43.0, 45.5);
        }

        [Fact]
        public void HohmannTransferTime_EarthToMars_About259Days()
        {
            double tof = TransferWindowMath.HohmannTransferTimeSeconds(EarthSma, MarsSma, MuSun);
            double days = tof / 86400.0;
            Assert.InRange(days, 255.0, 263.0);
        }

        [Fact]
        public void SynodicPeriod_EarthToMars_About780Days()
        {
            double syn = TransferWindowMath.SynodicPeriodSeconds(EarthPeriod, MarsPeriod);
            double days = syn / 86400.0;
            Assert.InRange(days, 770.0, 790.0);
        }

        [Fact]
        public void SynodicPeriod_KerbinToDuna_About2Point1KerbinYears()
        {
            double syn = TransferWindowMath.SynodicPeriodSeconds(KerbinPeriod, DunaPeriod);
            double kerbinYears = syn / KerbinPeriod;
            Assert.InRange(kerbinYears, 2.0, 2.3); // the real ~2.1-yr Kerbin->Duna launch cadence
        }

        [Fact]
        public void SynodicPeriod_EqualOrDegeneratePeriods_Infinity()
        {
            Assert.True(double.IsPositiveInfinity(TransferWindowMath.SynodicPeriodSeconds(100.0, 100.0)));
            Assert.True(double.IsPositiveInfinity(TransferWindowMath.SynodicPeriodSeconds(0.0, 100.0)));
            Assert.True(double.IsPositiveInfinity(TransferWindowMath.SynodicPeriodSeconds(100.0, double.NaN)));
        }

        [Fact]
        public void TimeToNextWindow_PhaseAlreadyAtTarget_IsZero()
        {
            // Guards: when the current phase equals the target, the window is now (T-0).
            double t = TransferWindowMath.TimeToNextWindowSeconds(44.3, 44.3, KerbinPeriod, DunaPeriod);
            Assert.InRange(t, 0.0, 1.0);
        }

        [Fact]
        public void TimeToNextWindow_JustMissed_WrapsToNearlyAFullSynodic()
        {
            // Current phase just BELOW the target with an outbound (negative) drift rate must wrap all
            // the way around (~one synodic period), never return a tiny negative-time "window".
            double syn = TransferWindowMath.SynodicPeriodSeconds(KerbinPeriod, DunaPeriod);
            double t = TransferWindowMath.TimeToNextWindowSeconds(34.3, 44.3, KerbinPeriod, DunaPeriod);
            Assert.True(t > 0.0);
            Assert.InRange(t, 0.9 * syn, 1.0 * syn);
        }

        [Fact]
        public void TimeToNextWindow_EqualPeriods_Infinity()
        {
            Assert.True(double.IsPositiveInfinity(
                TransferWindowMath.TimeToNextWindowSeconds(10.0, 44.3, 100.0, 100.0)));
        }

        [Fact]
        public void NextDepartureUT_AddsTimeToNextWindow()
        {
            double afterUT = 1_000_000.0;
            double dt = TransferWindowMath.TimeToNextWindowSeconds(0.0, 44.3, KerbinPeriod, DunaPeriod);
            double ut = TransferWindowMath.NextDepartureUT(afterUT, 0.0, 44.3, KerbinPeriod, DunaPeriod);
            Assert.Equal(afterUT + dt, ut, 3);
        }

        [Fact]
        public void NextDepartureUT_DegeneratePeriods_NaN()
        {
            Assert.True(double.IsNaN(
                TransferWindowMath.NextDepartureUT(1000.0, 0.0, 44.3, 100.0, 100.0)));
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(360.0, 0.0)]
        [InlineData(-90.0, 270.0)]
        [InlineData(450.0, 90.0)]
        [InlineData(-370.0, 350.0)]
        public void ClampDegrees360_WrapsIntoZeroTo360(double input, double expected)
        {
            Assert.Equal(expected, TransferWindowMath.ClampDegrees360(input), 6);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(180.0, 180.0)]
        [InlineData(190.0, -170.0)]
        [InlineData(-190.0, 170.0)]
        public void ClampDegrees180_WrapsIntoMinus180To180(double input, double expected)
        {
            Assert.Equal(expected, TransferWindowMath.ClampDegrees180(input), 6);
        }

        [Fact]
        public void DegenerateSmas_ReturnNaN()
        {
            Assert.True(double.IsNaN(TransferWindowMath.HohmannPhaseAngleTargetDegrees(0.0, MarsSma)));
            Assert.True(double.IsNaN(TransferWindowMath.HohmannTransferTimeSeconds(EarthSma, MarsSma, 0.0)));
        }
    }
}
