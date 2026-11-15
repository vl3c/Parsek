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
        private const double KerbinPeriod = 9203545.0, DunaPeriod = 17315400.0;

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
            Assert.True(double.IsNaN(TransferWindowMath.HohmannTransferTimeSeconds(EarthSma, MarsSma, 0.0)));
        }

        // M-MIS-1 (docs/dev/plans/reaim-resolver-reliability.md): the robust orientation metric
        // for near-equatorial transfers, where LAN alone is degenerate (KSP pins LAN to 0 with a
        // +X default node, so the rotation lives in AoP; the sum is well-defined in both regimes).
        [Theory]
        [InlineData(0.0, 0.0, 0.0)]       // equatorial degenerate case: LAN pinned to 0
        [InlineData(0.0, 123.4, 123.4)]   // equatorial: AoP carries the periapsis longitude
        [InlineData(40.0, 30.0, 70.0)]    // plain sum
        [InlineData(350.0, 20.0, 10.0)]   // wraps past 360
        [InlineData(180.0, 180.0, 0.0)]   // wraps exactly to 0
        [InlineData(-30.0, 10.0, 340.0)]  // negative input wraps into [0,360)
        public void LongitudeOfPeriapsis_SumsAndWraps(double lan, double aop, double expected)
        {
            Assert.Equal(expected, TransferWindowMath.LongitudeOfPeriapsisDegrees(lan, aop), 6);
        }

        [Fact]
        public void LongitudeOfPeriapsis_NaNPropagates()
        {
            Assert.True(double.IsNaN(TransferWindowMath.LongitudeOfPeriapsisDegrees(double.NaN, 10.0)));
            Assert.True(double.IsNaN(TransferWindowMath.LongitudeOfPeriapsisDegrees(10.0, double.NaN)));
        }
    }
}
