using System;
using System.Collections.Generic;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    // Two halves of the M-MIS-4 joint-arrival gate's headless coverage (PR #1255):
    // (1) pure decision logic of the in-game RealSaveMissionFinder's joint-arrival classifier -
    //     IsJointHoldUnit must accept exactly the units whose joint fields the span clock's
    //     per-loop dispatch would consume (finite positive secondary period + positive
    //     whole-period budget + finite hold-at UT + an applied hold) and reject every
    //     sentinel-carrying single-constraint unit, so the real-save scan never claims a joint
    //     hold that the clock would run single-period;
    // (2) a HEADLESS end-to-end mirror of the in-game JointArrivalHoldInGameTest gate - the
    //     SAME shared fixture (JointArrivalHoldInGameTest.BuildJointFixture) driven through the
    //     REAL MissionLoopUnitBuilder with a stock-like fake IBodyInfo, so the fixture shape
    //     (single-member rotation hand-off, rendezvous anchor, T_station = T_rot/16 lattice)
    //     is proven to resolve the joint hold without KSP. The in-game gate then only adds the
    //     live-ephemerides + live-classifier dimension.
    [Collection("Sequential")]
    public class RealSaveMissionFinderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RealSaveMissionFinderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
            MissionLoopUnitBuilder.ResetArrivalAmberLogForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionPeriodicity.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }
        private static GhostPlaybackLogic.LoopUnit MakeUnit(
            double holdSeconds,
            double holdAtUT,
            double jointSecondaryPeriod,
            double jointSecondaryTolerance,
            int jointBudget)
        {
            return new GhostPlaybackLogic.LoopUnit(
                0, new[] { 0 }, 0.0, 600.0, 1000.0, 700.0, 1000.0, null, null, null, null,
                loiterCuts: null,
                arrivalHoldSeconds: holdSeconds,
                arrivalHoldAtUT: holdAtUT,
                arrivalAlignPeriodSeconds: 100.0,
                arrivalJointSecondaryPeriodSeconds: jointSecondaryPeriod,
                arrivalJointSecondaryToleranceSeconds: jointSecondaryTolerance,
                arrivalJointMaxWholeHoldPeriods: jointBudget);
        }

        [Fact]
        public void IsJointHoldUnit_ValidJointFields_True()
        {
            var unit = MakeUnit(50.0, 400.0, 360.0, 5.0, 64);
            Assert.True(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Fact]
        public void IsJointHoldUnit_SingleConstraintSentinels_False()
        {
            // The documented "not joint" sentinels every single-period unit carries
            // (NaN secondary period / NaN tolerance / 0 budget).
            var unit = MakeUnit(50.0, 400.0, double.NaN, double.NaN, 0);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Fact]
        public void IsJointHoldUnit_ZeroBudget_False()
        {
            // A valid-looking secondary period with no whole-period budget degrades to the
            // single-period clock path, so it is NOT a joint unit.
            var unit = MakeUnit(50.0, 400.0, 360.0, 5.0, 0);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Fact]
        public void IsJointHoldUnit_NoAppliedHold_False()
        {
            // Joint fields without an applied hold (hold 0) - unreachable from the builder
            // (a joint engage always emits w0 > 0) but the predicate must stay conservative.
            var unit = MakeUnit(0.0, 400.0, 360.0, 5.0, 64);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Fact]
        public void IsJointHoldUnit_NaNHoldAt_False()
        {
            // A NaN hold-at UT breaks the per-loop entryOffset0 derivation (the clock's own
            // jointValid gate), so the classifier must reject it too.
            var unit = MakeUnit(50.0, double.NaN, 360.0, 5.0, 64);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-1.0)]
        [InlineData(double.PositiveInfinity)]
        public void IsJointHoldUnit_DegenerateSecondaryPeriod_False(double period)
        {
            var unit = MakeUnit(50.0, 400.0, period, 5.0, 64);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        [Fact]
        public void IsJointHoldUnit_InfiniteHold_False()
        {
            var unit = MakeUnit(double.PositiveInfinity, 400.0, 360.0, 5.0, 64);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit));
        }

        // -----------------------------------------------------------------
        // Headless end-to-end mirror of the in-game joint-arrival gate
        // -----------------------------------------------------------------

        private const double KerbinRotation = 21549.425;
        private const double DunaRotation = 65517.86;
        private const double KerbinOrbit = 9203545.0;
        private const double DunaOrbit = 17315400.0;
        private const uint StationPid = 987_654_321u;

        // A stock-like Sun/Kerbin/Duna fake (the MissionPeriodicityTests StockFake shape,
        // trimmed to the bodies the joint fixture transits) with the station's live orbit.
        private sealed class FakeBodyInfo : IBodyInfo
        {
            public readonly Dictionary<string, double> Rotation = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Orbit = new Dictionary<string, double>();
            public readonly Dictionary<string, string> Parent = new Dictionary<string, string>();
            public readonly Dictionary<string, double> Soi = new Dictionary<string, double>();
            public readonly Dictionary<string, double> Velocity = new Dictionary<string, double>();
            public readonly Dictionary<uint, (double period, string body)> VesselOrbits =
                new Dictionary<uint, (double period, string body)>();

            public double RotationPeriod(string b) => Rotation.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitPeriod(string b) => Orbit.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public string ReferenceBodyName(string b) => Parent.TryGetValue(b ?? "", out string v) ? v : null;
            public double SoiRadius(string b) => Soi.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double OrbitalVelocity(string b) => Velocity.TryGetValue(b ?? "", out double v) ? v : double.NaN;
            public double GravParameter(string b) => double.NaN; // no loiter compression in the fixture
            public double Radius(string b) => 6.0e5;

            public bool TryGetVesselOrbit(uint pid, string recordedVesselGuid,
                out double periodSeconds, out string orbitBodyName)
            {
                if (VesselOrbits.TryGetValue(pid, out var o))
                {
                    periodSeconds = o.period;
                    orbitBodyName = o.body;
                    return true;
                }
                periodSeconds = double.NaN;
                orbitBodyName = null;
                return false;
            }
        }

        private static FakeBodyInfo StockFake(bool withStation)
        {
            var f = new FakeBodyInfo();
            f.Rotation["Kerbin"] = KerbinRotation;
            f.Rotation["Duna"] = DunaRotation;
            f.Rotation["Sun"] = 432000.0;
            f.Orbit["Kerbin"] = KerbinOrbit;
            f.Orbit["Duna"] = DunaOrbit;
            f.Orbit["Sun"] = double.NaN;
            f.Parent["Kerbin"] = "Sun";
            f.Parent["Duna"] = "Sun";
            f.Parent["Sun"] = null;
            f.Soi["Duna"] = 47921949.0;
            f.Velocity["Duna"] = 5670.0;
            if (withStation)
                f.VesselOrbits[StationPid] = (DunaRotation / 16.0, "Duna");
            return f;
        }

        private static GhostPlaybackLogic.LoopUnit BuildFixtureUnit(
            bool includeStationRendezvous, FakeBodyInfo fake)
        {
            JointArrivalHoldInGameTest.BuildJointFixture(
                "mmis4-joint-headless", "Kerbin", "Sun", "Duna",
                includeStationRendezvous, StationPid,
                out Mission mission, out RecordingTree tree, out Recording rec);
            bool built = MissionLoopUnitBuilder.TryBuildLoopUnitForSelection(
                mission, new List<RecordingTree> { tree }, new List<Recording> { rec },
                30.0, fake, TransitedBodyRotationMode.Loose,
                out GhostPlaybackLogic.LoopUnit unit);
            Assert.True(built, "the real builder must resolve a loop unit for the shared joint fixture");
            return unit;
        }

        // Distance of x from the nearest whole multiple of period (independent modular
        // arithmetic, the same check the in-game gate runs).
        private static double CircErr(double x, double period)
        {
            double m = x % period;
            if (m < 0.0)
                m += period;
            return Math.Min(m, period - m);
        }

        [Fact]
        public void HeadlessMirror_LandingPlusStation_ResolvesJointHold_AndAlignsBothPerLoop()
        {
            double tRot = DunaRotation;
            double tSta = tRot / 16.0;
            var unit = BuildFixtureUnit(includeStationRendezvous: true, StockFake(withStation: true));

            Assert.True(unit.IsReaim, "the shared fixture must engage re-aim through the real builder");
            Assert.True(RealSaveMissionFinder.IsJointHoldUnit(unit),
                $"the shared fixture must resolve a JOINT hold (hold={unit.ArrivalHoldSeconds} " +
                $"secondary={unit.ArrivalJointSecondaryPeriodSeconds} budget={unit.ArrivalJointMaxWholeHoldPeriods} " +
                $"amber='{unit.ArrivalAmberReason}')");
            Assert.Equal(tSta, unit.ArrivalAlignPeriodSeconds, 9);          // station-lattice exact
            Assert.Equal(tRot, unit.ArrivalJointSecondaryPeriodSeconds, 9); // the landing rotation
            Assert.Equal(tRot * 5.0 / 360.0, unit.ArrivalJointSecondaryToleranceSeconds, 6); // Loose band
            Assert.True(unit.ArrivalJointMaxWholeHoldPeriods > 0
                && unit.ArrivalJointMaxWholeHoldPeriods <= Reaim.DestinationArrivalSolver.MaxJointHoldWholePeriods);
            Assert.Null(unit.ArrivalAmberReason);
            Assert.Contains(logLines, l =>
                l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD")
                && l.Contains("kind=joint") && l.Contains("dest=Duna"));

            // Per-loop dual satisfaction (the in-game gate's assertion, proven headlessly):
            // the production per-loop dispatch lands every checked loop exactly on the station
            // lattice AND within the rotation tolerance.
            double entryOffset0 = unit.PhaseAnchorUT
                + (GhostPlaybackLogic.CompressSpanUT(unit.ArrivalHoldAtUT, unit.LoiterCuts) - unit.SpanStartUT)
                - unit.ArrivalHoldAtUT;
            foreach (long n in new[] { 0L, 1L, 2L, 5L, 1000L })
            {
                double holdN = GhostPlaybackLogic.ComputePerLoopJointArrivalHoldSeconds(
                    unit.ArrivalHoldSeconds, n, unit.CadenceSeconds, unit.ArrivalAlignPeriodSeconds,
                    unit.ArrivalJointSecondaryPeriodSeconds, unit.ArrivalJointSecondaryToleranceSeconds,
                    entryOffset0, unit.ArrivalJointMaxWholeHoldPeriods);
                Assert.InRange(holdN, 0.0, (unit.ArrivalJointMaxWholeHoldPeriods + 1) * tSta + 1e-6);
                double delta = entryOffset0 + n * unit.CadenceSeconds + holdN;
                Assert.True(CircErr(delta, tSta) <= 1e-3,
                    $"loop N={n}: station lattice must be exact (err={CircErr(delta, tSta)})");
                Assert.True(CircErr(delta, tRot) <= unit.ArrivalJointSecondaryToleranceSeconds + 1e-3,
                    $"loop N={n}: rotation must be within tolerance (err={CircErr(delta, tRot)})");
            }
        }

        [Fact]
        public void HeadlessMirror_LandingOnly_KeepsSingleRotationHold_NoJointFields()
        {
            var unit = BuildFixtureUnit(includeStationRendezvous: false, StockFake(withStation: false));

            Assert.True(unit.IsReaim);
            Assert.False(RealSaveMissionFinder.IsJointHoldUnit(unit),
                "the landing-only control must NOT classify as a joint-hold unit");
            Assert.True(double.IsNaN(unit.ArrivalJointSecondaryPeriodSeconds));
            Assert.True(double.IsNaN(unit.ArrivalJointSecondaryToleranceSeconds));
            Assert.Equal(0, unit.ArrivalJointMaxWholeHoldPeriods);
            Assert.DoesNotContain(logLines, l => l.Contains("kind=joint"));
            if (unit.ArrivalHoldSeconds > 0.0)
            {
                // The pre-existing single-period ROTATION hold (byte-identical-off guarantee).
                Assert.Equal(DunaRotation, unit.ArrivalAlignPeriodSeconds, 9);
                Assert.Contains(logLines, l =>
                    l.Contains("[Reaim]") && l.Contains("ARRIVAL HOLD") && l.Contains("kind=rotation"));
            }
        }
    }
}
