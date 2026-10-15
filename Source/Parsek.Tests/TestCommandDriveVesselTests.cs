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
