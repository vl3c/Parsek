using System;
using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>MapCamera</c> seam verb (<see cref="TestCommandMapCamera"/>):
    /// the body resolution, every arg's parse and range refusal, the degree / radian and
    /// metre / scaled-unit conversions the applier hands stock, the clamp report, the settle
    /// decision, the payload and the OK log line.
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandMapCameraTests : IDisposable
    {
        private static readonly string[] Bodies = { "Sun", "Kerbin", "Mun", "Minmus", "Duna" };
        private readonly List<string> logLines = new List<string>();

        public TestCommandMapCameraTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static bool Parse(string focus, string pitch, string heading, string distance,
            out MapCameraRequest req, out string reason)
            => TestCommandMapCamera.TryParse(focus, pitch, heading, distance, null, Bodies, out req, out reason);

        private static bool ParseHold(string hold, out MapCameraRequest req, out string reason)
            => TestCommandMapCamera.TryParse("Kerbin", null, null, null, hold, Bodies, out req, out reason);

        // ----- focus -----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MissingFocus_IsRefused(string focus)
        {
            Assert.False(Parse(focus, null, null, null, out var req, out string reason));
            Assert.Null(req);
            Assert.Equal(TestCommandMapCamera.FocusArgMissingReason, reason);
        }

        [Theory]
        [InlineData("Jool")]
        [InlineData("Kerb")]
        [InlineData("Kerbin2")]
        public void UnknownBody_IsRefused(string focus)
        {
            Assert.False(Parse(focus, null, null, null, out _, out string reason));
            Assert.Equal(TestCommandMapCamera.UnknownBodyReason, reason);
        }

        [Theory]
        [InlineData("Kerbin", "Kerbin")]
        [InlineData("kerbin", "Kerbin")]
        [InlineData(" MUN ", "Mun")]
        public void Focus_ResolvesToTheCanonicalName(string raw, string canonical)
        {
            Assert.True(Parse(raw, null, null, null, out var req, out string reason));
            Assert.Null(reason);
            Assert.Equal(canonical, req.Focus);
            Assert.Null(req.PitchDeg);
            Assert.Null(req.HeadingDeg);
            Assert.Null(req.DistanceMetres);
            Assert.Equal(0.0, req.HoldSeconds);
        }

        [Fact]
        public void ResolveBody_ExactBeatsCaseInsensitive_AndAmbiguityRefuses()
        {
            Assert.Equal("Mun", TestCommandMapCamera.ResolveBody("Mun", new[] { "MUN", "Mun" }));
            Assert.Null(TestCommandMapCamera.ResolveBody("mun", new[] { "MUN", "Mun" }));
            Assert.Null(TestCommandMapCamera.ResolveBody("Mun", null));
        }

        // ----- pitch / heading / distance -----

        [Theory]
        [InlineData("90", 90.0)]
        [InlineData("-90", -90.0)]
        [InlineData("0", 0.0)]
        [InlineData("45.5", 45.5)]
        [InlineData("1e1", 10.0)]
        public void Pitch_InRange_Parses(string raw, double expected)
        {
            Assert.True(Parse("Kerbin", raw, null, null, out var req, out _));
            Assert.Equal(expected, req.PitchDeg);
        }

        [Theory]
        [InlineData("90.0001", "mapcamera-pitch-out-of-range")]
        [InlineData("-91", "mapcamera-pitch-out-of-range")]
        [InlineData("abc", "mapcamera-pitch-invalid")]
        [InlineData("NaN", "mapcamera-pitch-invalid")]
        [InlineData("Infinity", "mapcamera-pitch-invalid")]
        [InlineData("45,5", "mapcamera-pitch-invalid")]
        [InlineData("", "mapcamera-pitch-invalid")]
        public void Pitch_Refusals(string raw, string expected)
        {
            Assert.False(Parse("Kerbin", raw, null, null, out _, out string reason));
            Assert.Equal(expected, reason);
        }

        [Theory]
        [InlineData("360", true, null)]
        [InlineData("-360", true, null)]
        [InlineData("361", false, "mapcamera-heading-out-of-range")]
        [InlineData("east", false, "mapcamera-heading-invalid")]
        public void Heading(string raw, bool ok, string expected)
        {
            Assert.Equal(ok, Parse("Kerbin", null, raw, null, out _, out string reason));
            Assert.Equal(expected, reason);
        }

        [Theory]
        [InlineData("1", true, null)]
        [InlineData("2.5e7", true, null)]
        [InlineData("1e13", true, null)]
        [InlineData("0", false, "mapcamera-distance-out-of-range")]
        [InlineData("-5", false, "mapcamera-distance-out-of-range")]
        [InlineData("1.1e13", false, "mapcamera-distance-out-of-range")]
        [InlineData("far", false, "mapcamera-distance-invalid")]
        public void Distance(string raw, bool ok, string expected)
        {
            Assert.Equal(ok, Parse("Kerbin", null, null, raw, out _, out string reason));
            Assert.Equal(expected, reason);
        }

        [Theory]
        [InlineData("0", true, 0.0, null)]
        [InlineData("4.5", true, 4.5, null)]
        [InlineData("10", true, 10.0, null)]
        [InlineData(" 1e0 ", true, 1.0, null)]
        [InlineData("10.0001", false, 0.0, "mapcamera-hold-out-of-range")]
        [InlineData("-0.5", false, 0.0, "mapcamera-hold-out-of-range")]
        [InlineData("4,5", false, 0.0, "mapcamera-hold-invalid")]
        [InlineData("soon", false, 0.0, "mapcamera-hold-invalid")]
        [InlineData("NaN", false, 0.0, "mapcamera-hold-invalid")]
        [InlineData("", false, 0.0, "mapcamera-hold-invalid")]
        public void Hold(string raw, bool ok, double seconds, string expected)
        {
            Assert.Equal(ok, ParseHold(raw, out var req, out string reason));
            Assert.Equal(expected, reason);
            if (ok) Assert.Equal(seconds, req.HoldSeconds);
        }

        [Fact]
        public void Hold_IsInvariantUnderAnotherCulture()
        {
            var saved = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.True(ParseHold("4.5", out var req, out _));
                Assert.Equal(4.5, req.HoldSeconds);
                Assert.False(ParseHold("4,5", out _, out string reason));
                Assert.Equal(TestCommandMapCamera.HoldInvalidReason, reason);
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = saved;
            }
        }

        [Fact]
        public void TheFirstFailingArg_InFocusPitchHeadingDistanceOrder_Decides()
        {
            Assert.False(Parse("Jool", "999", "999", "-1", out _, out string r1));
            Assert.Equal(TestCommandMapCamera.UnknownBodyReason, r1);
            Assert.False(Parse("Kerbin", "999", "999", "-1", out _, out string r2));
            Assert.Equal(TestCommandMapCamera.PitchOutOfRangeReason, r2);
            Assert.False(Parse("Kerbin", "10", "999", "-1", out _, out string r3));
            Assert.Equal(TestCommandMapCamera.HeadingOutOfRangeReason, r3);
            Assert.False(Parse("Kerbin", "10", "10", "-1", out _, out string r4));
            Assert.Equal(TestCommandMapCamera.DistanceOutOfRangeReason, r4);
            Assert.False(TestCommandMapCamera.TryParse("Kerbin", "10", "10", "-1", "99", Bodies, out _, out string r5));
            Assert.Equal(TestCommandMapCamera.DistanceOutOfRangeReason, r5);
            Assert.False(TestCommandMapCamera.TryParse("Kerbin", "10", "10", "1", "99", Bodies, out _, out string r6));
            Assert.Equal(TestCommandMapCamera.HoldOutOfRangeReason, r6);
        }

        [Fact]
        public void AllFourArgs_Parse()
        {
            Assert.True(Parse("Duna", "60", "-30", "5e9", out var req, out _));
            Assert.Equal("Duna", req.Focus);
            Assert.Equal(60.0, req.PitchDeg);
            Assert.Equal(-30.0, req.HeadingDeg);
            Assert.Equal(5e9, req.DistanceMetres);
        }

        // ----- conversions + clamp report -----

        [Fact]
        public void Conversions_MatchStocksUnits()
        {
            Assert.Equal(Math.PI / 2, TestCommandMapCamera.DegToRad(90), 6);
            Assert.Equal(90.0, TestCommandMapCamera.RadToDeg((float)(Math.PI / 2)), 4);
            Assert.Equal(1000f, TestCommandMapCamera.MetresToScaled(6.0e6, 6000.0));
            Assert.Equal(0f, TestCommandMapCamera.MetresToScaled(6.0e6, 0.0));
        }

        [Fact]
        public void AngleClamped_IgnoresFloatNoise_AndAnUnrequestedAngle()
        {
            double roundTrip = TestCommandMapCamera.RadToDeg(TestCommandMapCamera.DegToRad(37.3));
            Assert.False(TestCommandMapCamera.AngleClamped(37.3, roundTrip));
            Assert.True(TestCommandMapCamera.AngleClamped(90.0, 88.0));
            Assert.False(TestCommandMapCamera.AngleClamped(null, 88.0));
        }

        [Fact]
        public void DistanceClamped_IsRelative()
        {
            Assert.False(TestCommandMapCamera.DistanceClamped(1e7, 1e7 * (1 + 1e-4)));
            Assert.True(TestCommandMapCamera.DistanceClamped(1e3, 1.2e6));
            Assert.False(TestCommandMapCamera.DistanceClamped(null, 5));
        }

        // ----- settle -----

        [Theory]
        [InlineData(false, 5, 100.0, 100.0, 9, false, "SceneLost")]
        [InlineData(true, 0, 100.0, 100.0, 9, false, "NotYet")]   // LateUpdate not run yet
        [InlineData(true, 1, 100.05, 100.0, 0, false, "Settled")]
        [InlineData(true, 1, 150.0, 100.0, 0, false, "NotYet")]
        [InlineData(true, 4, 150.0, 100.0, 3, false, "Settled")]  // held still: backstop
        [InlineData(true, 4, 150.0, 100.0, 2, false, "NotYet")]
        [InlineData(true, 4, 150.0, 100.0, 0, true, "TimedOut")]
        [InlineData(true, 4, 100.0, 100.0, 0, true, "Settled")]   // settled beats expiry
        [InlineData(true, 4, double.NaN, 100.0, 0, false, "NotYet")]
        public void DecideSettle(bool sceneOk, int frames, double actual, double held, int still, bool expired,
            string expected)
        {
            Assert.Equal(expected, TestCommandMapCamera.DecideSettle(sceneOk, frames, actual, held, still, expired).ToString());
        }

        [Theory]
        [InlineData(false, 99.0, 4.5, "SceneLost")]
        [InlineData(true, 0.0, 4.5, "Holding")]
        [InlineData(true, 4.49, 4.5, "Holding")]
        [InlineData(true, 4.5, 4.5, "Done")]
        [InlineData(true, 7.0, 4.5, "Done")]
        [InlineData(true, double.NaN, 4.5, "Holding")]
        [InlineData(false, double.NaN, 4.5, "SceneLost")]
        public void DecideHold(bool sceneOk, double elapsed, double hold, string expected)
        {
            Assert.Equal(expected, TestCommandMapCamera.DecideHold(sceneOk, elapsed, hold).ToString());
        }

        [Fact]
        public void SettleBudget_LeavesRoomForTheHold_WithinTheStepBudget()
        {
            Assert.Equal(60.0, TestCommandMapCamera.SettleBudgetSeconds(60.0, 0.0));
            Assert.Equal(55.5, TestCommandMapCamera.SettleBudgetSeconds(60.0, 4.5));
            Assert.Equal(50.0, TestCommandMapCamera.SettleBudgetSeconds(
                DeferralBudget.BudgetSeconds(TestCommandMapCamera.Verb), TestCommandMapCamera.MaxHoldSeconds));
            Assert.Equal(0.0, TestCommandMapCamera.SettleBudgetSeconds(5.0, 10.0));
            Assert.Equal(60.0, TestCommandMapCamera.SettleBudgetSeconds(60.0, -3.0));
        }

        [Fact]
        public void IsStill_IsATenthOfTheSettleTolerance()
        {
            Assert.True(TestCommandMapCamera.IsStill(0.009, 100.0));
            Assert.False(TestCommandMapCamera.IsStill(0.02, 100.0));
            Assert.False(TestCommandMapCamera.IsStill(0.0, 0.0));
            Assert.False(TestCommandMapCamera.IsStill(double.NaN, 100.0));
        }

        // ----- tokens, payload, log -----

        [Fact]
        public void RefusalTokens_AreDistinct_AndShareTheMapAdmissionTokens()
        {
            var tokens = new[]
            {
                TestCommandMapCamera.FocusArgMissingReason, TestCommandMapCamera.UnknownBodyReason,
                TestCommandMapCamera.PitchInvalidReason, TestCommandMapCamera.PitchOutOfRangeReason,
                TestCommandMapCamera.HeadingInvalidReason, TestCommandMapCamera.HeadingOutOfRangeReason,
                TestCommandMapCamera.DistanceInvalidReason, TestCommandMapCamera.DistanceOutOfRangeReason,
                TestCommandMapCamera.HoldInvalidReason, TestCommandMapCamera.HoldOutOfRangeReason,
                TestCommandMapCamera.MapViewClosedReason, TestCommandMapCamera.NotMapSceneReason,
                TestCommandMapCamera.NoCameraReason, TestCommandMapCamera.FocusNotTargetableReason,
                TestCommandMapCamera.FocusNotAppliedReason, TestCommandMapCamera.SceneLostReason,
                TestCommandMapCamera.NotSettledReason,
            };
            Assert.Equal(tokens.Length, tokens.Distinct().Count());
            Assert.Equal(TestCommandDumpMapGeometry.MapViewClosedReason, TestCommandMapCamera.MapViewClosedReason);
        }

        [Fact]
        public void ReasonTables_AreTheConstants_InOrder()
        {
            Assert.Equal(new[]
            {
                TestCommandMapCamera.FocusArgMissingReason, TestCommandMapCamera.UnknownBodyReason,
                TestCommandMapCamera.PitchInvalidReason, TestCommandMapCamera.PitchOutOfRangeReason,
                TestCommandMapCamera.HeadingInvalidReason, TestCommandMapCamera.HeadingOutOfRangeReason,
                TestCommandMapCamera.DistanceInvalidReason, TestCommandMapCamera.DistanceOutOfRangeReason,
                TestCommandMapCamera.HoldInvalidReason, TestCommandMapCamera.HoldOutOfRangeReason,
                TestCommandMapCamera.MapViewClosedReason, TestCommandMapCamera.NotMapSceneReason,
            }, TestCommandMapCamera.Reasons);
            Assert.Equal(new[]
            {
                TestCommandMapCamera.NoCameraReason, TestCommandMapCamera.FocusNotTargetableReason,
                TestCommandMapCamera.FocusNotAppliedReason, TestCommandMapCamera.SceneLostReason,
                TestCommandMapCamera.NotSettledReason,
            }, TestCommandMapCamera.ErrorReasons);
        }

        [Fact]
        public void BuildPayload_IsInvariant()
        {
            var d = TestCommandMapCamera.BuildPayload("Kerbin", 88.5, -30.25, 1.5e7, true, false, 12, 4.5)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal("Kerbin", d["focus"]);
            Assert.Equal("88.5", d["pitch"]);
            Assert.Equal("-30.25", d["heading"]);
            Assert.Equal("15000000", d["distance"]);
            Assert.Equal("true", d["pitchClamped"]);
            Assert.Equal("false", d["distanceClamped"]);
            Assert.Equal("12", d["frames"]);
            Assert.Equal("4.5", d["hold"]);
        }

        [Fact]
        public void LogSettled_NamesTheHeldValues_TheClamps_AndTheStockLimits()
        {
            TestCommandMapCamera.LogSettled("Mun", 88.5, 10, 2.5e6, true, false, -88.0, 88.5, 7, 4.5);
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[TestCommands]")
                && l.Contains("mapcamera ok focus=Mun") && l.Contains("pitch=88.5") && l.Contains("heading=10")
                && l.Contains("distance=2500000") && l.Contains("pitchClamped=true")
                && l.Contains("distanceClamped=false") && l.Contains("maxPitch=88.5") && l.Contains("frames=7")
                && l.Contains("hold=4.5"));
        }

        [Fact]
        public void LogHolding_NamesTheFocusAndTheHold_AndIsNotARefusalLine()
        {
            TestCommandMapCamera.LogHolding("Mun", 4.5, 3);
            Assert.Contains(logLines, l => l.Contains("[INFO]") && l.Contains("[TestCommands]")
                && l.Contains("mapcamera settled, holding focus=Mun hold=4.5 frames=3"));
            // The TP lanes forbid `mapcamera: ` and `mapcamera (not settled|scene lost)`.
            Assert.DoesNotContain(logLines, l => l.Contains("mapcamera: ") || l.Contains("not settled"));
        }
    }
}
