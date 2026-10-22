using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>The pure half of the automation-only DriveVessel seam verb.</summary>
    public class TestCommandDriveVesselTests
    {
        private const string Verb = TestCommandDriveVessel.Verb;

        [Fact]
        public void Verb_IsImplementedFlightOnlyAndMutating()
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor(Verb));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb(Verb));
            // The span cap keeps the drive inside the default wall budget it rides.
            Assert.Equal(DeferralBudget.DefaultSeconds, DeferralBudget.BudgetSeconds(Verb));
            Assert.True(TestCommandDriveVessel.MaxSeconds * 2.0 <= DeferralBudget.BudgetSeconds(Verb));
        }

        [Fact]
        public void MinMovedSpeed_ExceedsTheSurfaceMobileThreshold()
        {
            // Classify answers SurfaceMobile above 0.1 m/s; a drive must clear that by a margin.
            Assert.True(TestCommandDriveVessel.MinMovedSrfSpeed > 0.1);
            Assert.Equal(SegmentEnvironment.SurfaceMobile,
                EnvironmentDetector.Classify(hasAtmosphere: true, altitude: 70.0,
                    atmosphereDepth: 70000.0, situation: 1 /* LANDED */,
                    srfSpeed: TestCommandDriveVessel.MinMovedSrfSpeed, hasActiveThrust: false));
        }

        [Theory]
        [InlineData("1", 1f)]
        [InlineData("0.5", 0.5f)]
        [InlineData("-1", -1f)]
        [InlineData("-0.25", -0.25f)]
        public void ValidateThrottle_AcceptsInRange(string raw, float expected)
        {
            Assert.Null(TestCommandDriveVessel.ValidateThrottle(raw, out float throttle));
            Assert.Equal((double)expected, (double)throttle, 5);
        }

        [Theory]
        [InlineData(null, "drivevessel-throttle-arg-missing")]
        [InlineData("", "drivevessel-throttle-arg-missing")]
        [InlineData("0", "drivevessel-throttle-arg-invalid")]
        [InlineData("1.5", "drivevessel-throttle-arg-invalid")]
        [InlineData("-1.01", "drivevessel-throttle-arg-invalid")]
        [InlineData("NaN", "drivevessel-throttle-arg-invalid")]
        [InlineData("Infinity", "drivevessel-throttle-arg-invalid")]
        [InlineData("full", "drivevessel-throttle-arg-invalid")]
        public void ValidateThrottle_RefusesOutOfRangeOrMalformed(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandDriveVessel.ValidateThrottle(raw, out float throttle));
            Assert.Equal(0f, throttle);
        }

        [Theory]
        [InlineData("8", 8.0)]
        [InlineData("0.5", 0.5)]
        [InlineData("30", 30.0)]
        public void ValidateSeconds_AcceptsInRange(string raw, double expected)
        {
            Assert.Null(TestCommandDriveVessel.ValidateSeconds(raw, out double seconds));
            Assert.Equal(expected, seconds, 9);
        }

        [Theory]
        [InlineData(null, "drivevessel-seconds-arg-missing")]
        [InlineData("", "drivevessel-seconds-arg-missing")]
        [InlineData("0", "drivevessel-seconds-arg-invalid")]
        [InlineData("-3", "drivevessel-seconds-arg-invalid")]
        [InlineData("30.5", "drivevessel-seconds-arg-invalid")]
        [InlineData("NaN", "drivevessel-seconds-arg-invalid")]
        [InlineData("eight", "drivevessel-seconds-arg-invalid")]
        public void ValidateSeconds_RefusesOutOfRangeOrMalformed(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandDriveVessel.ValidateSeconds(raw, out double seconds));
            Assert.Equal(0.0, seconds);
        }

        [Fact]
        public void Validators_AreInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Null(TestCommandDriveVessel.ValidateThrottle("0.5", out float throttle));
                Assert.Equal(0.5, (double)throttle, 5);
                Assert.Null(TestCommandDriveVessel.ValidateSeconds("7.5", out double seconds));
                Assert.Equal(7.5, seconds, 9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        [InlineData(false, false, false, 0, "drivevessel-no-active-vessel")]
        [InlineData(true, false, true, 4, "drivevessel-vessel-packed")]
        [InlineData(true, true, false, 4, "drivevessel-not-on-surface")]
        [InlineData(true, true, true, 0, "drivevessel-no-wheel-motor")]
        [InlineData(true, true, true, 4, null)]
        public void DecideGate_RefusesInOrder(bool hasVessel, bool unpacked, bool onSurface,
            int motors, string expected)
        {
            Assert.Equal(expected, TestCommandDriveVessel.DecideGate(hasVessel, unpacked, onSurface, motors));
        }

        [Fact]
        public void Reasons_ArePrefixedAndDistinct()
        {
            var seen = new HashSet<string>();
            foreach (string reason in TestCommandDriveVessel.Reasons)
            {
                Assert.StartsWith("drivevessel-", reason);
                Assert.True(seen.Add(reason), reason);
            }
            foreach (string reason in TestCommandDriveVessel.ErrorReasons)
            {
                Assert.StartsWith("drivevessel-", reason);
                Assert.True(seen.Add(reason), reason);
            }
            Assert.Contains(TestCommandDriveVessel.VesselLostReason, TestCommandDriveVessel.ErrorReasons);
            Assert.Contains(TestCommandDriveVessel.DidNotMoveReason, TestCommandDriveVessel.ErrorReasons);
            Assert.Contains(TestCommandDriveVessel.TimeoutReason, TestCommandDriveVessel.ErrorReasons);
        }

        [Theory]
        // Still inside the span, inside the budget.
        [InlineData(true, 4.0, 8.0, 3.0, 5.0, 60.0, "StillDriving")]
        // Span covered and the vessel moved.
        [InlineData(true, 8.0, 8.0, 3.0, 9.0, 60.0, "CompleteOk")]
        [InlineData(true, 8.02, 8.0, 0.5, 9.0, 60.0, "CompleteOk")]
        // Span covered but the vessel never reached the moved threshold.
        [InlineData(true, 8.0, 8.0, 0.49, 9.0, 60.0, "DidNotMove")]
        [InlineData(true, 8.0, 8.0, 0.0, 9.0, 60.0, "DidNotMove")]
        // A lost vessel ends the drive at once, whatever else holds.
        [InlineData(false, 8.0, 8.0, 3.0, 9.0, 60.0, "VesselLost")]
        [InlineData(false, 1.0, 8.0, 3.0, 1.0, 60.0, "VesselLost")]
        // A clock that never covered the span (paused game) times out on the wall budget.
        [InlineData(true, 0.0, 8.0, 0.0, 60.0, 60.0, "Timeout")]
        public void DecideCompletion(bool usable, double gameElapsed, double requested,
            double maxSpeed, double wallElapsed, double budget,
            string expected)
        {
            Assert.Equal(expected, TestCommandDriveVessel.DecideCompletion(
                usable, gameElapsed, requested, maxSpeed, wallElapsed, budget).ToString());
        }

        [Fact]
        public void SurfaceDistance_MatchesArcLength()
        {
            const double kerbinRadius = 600000.0;
            Assert.Equal(0.0, TestCommandDriveVessel.ComputeSurfaceDistanceMeters(
                -0.0486, -74.72, -0.0486, -74.72, kerbinRadius), 9);
            // One thousandth of a degree of longitude on the equator: r * 1e-3 * pi / 180.
            double expected = kerbinRadius * 0.001 * System.Math.PI / 180.0;
            Assert.Equal(expected, TestCommandDriveVessel.ComputeSurfaceDistanceMeters(
                0.0, -74.720, 0.0, -74.719, kerbinRadius), 6);
            // Symmetric, and a latitude step reads the same arc.
            Assert.Equal(expected, TestCommandDriveVessel.ComputeSurfaceDistanceMeters(
                0.0, -74.719, 0.0, -74.720, kerbinRadius), 6);
            Assert.Equal(expected, TestCommandDriveVessel.ComputeSurfaceDistanceMeters(
                0.0, 10.0, 0.001, 10.0, kerbinRadius), 6);
        }

        [Fact]
        public void StartAndEndLines_AreInvariantAndGrepStable()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "drivevessel start: vessel=B pid=313889796 situation=LANDED wheelThrottle=1.00 "
                    + "seconds=8.0 wheelMotors=4 brakesWereOn=true startUT=980.12 srfSpeed=0.00",
                    TestCommandDriveVessel.FormatStartLine("B", 313889796u, "LANDED", 1f, 8.0, 4,
                        true, 980.123, 0.001));
                Assert.Equal(
                    "drivevessel end: outcome=moved vessel=B pid=313889796 gameSeconds=8.02 "
                    + "startSrfSpeed=0.00 endSrfSpeed=6.25 maxSrfSpeed=6.31 pathMeters=31.4 "
                    + "displacementMeters=30.9 situation=LANDED",
                    TestCommandDriveVessel.FormatEndLine("moved", "B", 313889796u, 8.021, 0.0,
                        6.25, 6.31, 31.42, 30.91, "LANDED"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Theory]
        // Neither arg: the drive mode, whose throttle validator names the missing arg.
        [InlineData(null, null, null, false, 0.0)]
        [InlineData("1", null, null, false, 0.0)]
        // settleBelow alone: settle mode, in (0, 0.1].
        [InlineData(null, "0.05", null, true, 0.05)]
        [InlineData(null, "0.1", null, true, 0.1)]
        // Both: a conflict.
        [InlineData("1", "0.05", "drivevessel-settle-arg-conflict", false, 0.0)]
        // Out of range or malformed.
        [InlineData(null, "0", "drivevessel-settle-arg-invalid", false, 0.0)]
        [InlineData(null, "-0.05", "drivevessel-settle-arg-invalid", false, 0.0)]
        [InlineData(null, "0.11", "drivevessel-settle-arg-invalid", false, 0.0)]
        [InlineData(null, "NaN", "drivevessel-settle-arg-invalid", false, 0.0)]
        [InlineData(null, "slow", "drivevessel-settle-arg-invalid", false, 0.0)]
        [InlineData(null, "", "drivevessel-settle-arg-invalid", false, 0.0)]
        public void ValidateSettle_PicksTheModeAndBounds(string throttle, string settleRaw,
            string reason, bool settle, double settleBelow)
        {
            Assert.Equal(reason, TestCommandDriveVessel.ValidateSettle(throttle, settleRaw,
                out bool gotSettle, out double gotBelow));
            Assert.Equal(settle, gotSettle);
            Assert.Equal(settleBelow, gotBelow, 9);
        }

        [Fact]
        public void ValidateSettle_IsInvariantCulture()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Null(TestCommandDriveVessel.ValidateSettle(null, "0.05",
                    out bool settle, out double below));
                Assert.True(settle);
                Assert.Equal(0.05, below, 9);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void SettleBound_IsTheSurfaceStationaryThreshold()
        {
            // A vessel settled at the bound must classify SurfaceStationary, the state the
            // recorder's hysteresis has to start in (RVR-21-START-SPEED-ABOVE-STATIONARY).
            Assert.Equal(SegmentEnvironment.SurfaceStationary,
                EnvironmentDetector.Classify(hasAtmosphere: true, altitude: 70.0,
                    atmosphereDepth: 70000.0, situation: 1 /* LANDED */,
                    srfSpeed: TestCommandDriveVessel.MaxSettleBelow, hasActiveThrust: false));
            Assert.True(TestCommandDriveVessel.SettleHoldSeconds > 0.0);
            Assert.Contains(TestCommandDriveVessel.DidNotSettleReason, TestCommandDriveVessel.ErrorReasons);
        }

        [Fact]
        public void DecideGate_SettleModeNeedsNoWheelMotor()
        {
            Assert.Null(TestCommandDriveVessel.DecideGate(true, true, true, 0, settle: true));
            Assert.Equal("drivevessel-not-on-surface",
                TestCommandDriveVessel.DecideGate(true, true, false, 0, settle: true));
            Assert.Equal("drivevessel-no-wheel-motor",
                TestCommandDriveVessel.DecideGate(true, true, true, 0, settle: false));
        }

        [Theory]
        // Below the threshold for the hold: settled, even on the span's last frame.
        [InlineData(true, 2.0, 20.0, 0.5, 3.0, 60.0, "Settled")]
        [InlineData(true, 20.0, 20.0, 0.6, 21.0, 60.0, "Settled")]
        // Below, but not yet for the hold; or still above it (negative).
        [InlineData(true, 2.0, 20.0, 0.49, 3.0, 60.0, "StillDriving")]
        [InlineData(true, 2.0, 20.0, -1.0, 3.0, 60.0, "StillDriving")]
        // The span ran out with the vessel still moving.
        [InlineData(true, 20.0, 20.0, -1.0, 21.0, 60.0, "DidNotSettle")]
        [InlineData(true, 20.0, 20.0, 0.2, 21.0, 60.0, "DidNotSettle")]
        // A lost vessel ends it at once; a paused clock times out.
        [InlineData(false, 2.0, 20.0, 0.6, 3.0, 60.0, "VesselLost")]
        [InlineData(true, 0.0, 20.0, -1.0, 60.0, 60.0, "Timeout")]
        public void DecideSettleCompletion(bool usable, double gameElapsed, double requested,
            double belowFor, double wallElapsed, double budget, string expected)
        {
            Assert.Equal(expected, TestCommandDriveVessel.DecideSettleCompletion(
                usable, gameElapsed, requested, belowFor, wallElapsed, budget).ToString());
        }

        [Fact]
        public void SettleLinesAndPayload_AreInvariantAndGrepStable()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal(
                    "drivevessel settle start: vessel=B pid=313889796 situation=LANDED "
                    + "settleBelow=0.050 seconds=20.0 brakesOn=true startUT=980.12 srfSpeed=0.120",
                    TestCommandDriveVessel.FormatSettleStartLine("B", 313889796u, "LANDED", 0.05,
                        20.0, true, 980.123, 0.1201));
                Assert.Equal(
                    "drivevessel settle end: outcome=settled vessel=B pid=313889796 "
                    + "gameSeconds=2.53 startSrfSpeed=0.120 endSrfSpeed=0.004 situation=LANDED",
                    TestCommandDriveVessel.FormatSettleEndLine("settled", "B", 313889796u, 2.531,
                        0.1201, 0.0041, "LANDED"));
                List<KeyValuePair<string, string>> payload =
                    TestCommandDriveVessel.BuildSettledPayload(2.531, 0.0041, "LANDED");
                Assert.Equal(new[] { "settled", "gameSeconds", "srfSpeed", "situation" },
                    payload.ConvertAll(kv => kv.Key).ToArray());
                Assert.Equal("true", payload[0].Value);
                Assert.Equal("2.53", payload[1].Value);
                Assert.Equal("0.004", payload[2].Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void CompletePayload_IsInWireOrder()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandDriveVessel.BuildCompletePayload(8.021, 6.31, 31.42, 30.91, "LANDED");
            Assert.Equal(new[] { "moved", "gameSeconds", "maxSrfSpeed", "pathMeters",
                "displacementMeters", "situation" }, payload.ConvertAll(kv => kv.Key).ToArray());
            Assert.Equal("true", payload[0].Value);
            Assert.Equal("8.02", payload[1].Value);
            Assert.Equal("6.31", payload[2].Value);
            Assert.Equal("31.4", payload[3].Value);
            Assert.Equal("30.9", payload[4].Value);
            Assert.Equal("LANDED", payload[5].Value);
        }
    }
}
