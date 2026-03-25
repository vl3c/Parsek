using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AutoLoopTests : System.IDisposable
    {
        public AutoLoopTests()
        {
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        static Recording MakeRec(
            double startUT = 100, double endUT = 200,
            double loopInterval = 10.0,
            LoopTimeUnit unit = LoopTimeUnit.Sec)
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                LoopPlayback = true,
                LoopIntervalSeconds = loopInterval,
                LoopTimeUnit = unit,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = startUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = endUT, latitude = 0, longitude = 0, altitude = 0,
                bodyName = "Kerbin", rotation = Quaternion.identity, velocity = Vector3.zero
            });
            return rec;
        }

        // --- Serialization round-trip ---

        [Fact]
        public void LoopTimeUnit_SaveLoad_RoundTrip_Auto()
        {
            var source = new Recording
            {
                RecordingId = "auto-test",
                LoopPlayback = true,
                LoopIntervalSeconds = 30.0,
                LoopTimeUnit = LoopTimeUnit.Auto,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(LoopTimeUnit.Auto, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_SaveLoad_RoundTrip_Hour()
        {
            var source = new Recording
            {
                RecordingId = "hr-test",
                LoopTimeUnit = LoopTimeUnit.Hour,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(LoopTimeUnit.Hour, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Load_MissingKey_DefaultsSec()
        {
            var node = new ConfigNode("RECORDING");
            // No loopTimeUnit key at all
            var loaded = new Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(LoopTimeUnit.Sec, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Save_DefaultSec_NotWritten()
        {
            var source = new Recording
            {
                RecordingId = "sec-default",
                LoopTimeUnit = LoopTimeUnit.Sec,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopTimeUnit"));
        }

        // --- ResolveLoopInterval ---

        [Fact]
        public void ResolveLoopInterval_AutoMode_ReturnsGlobalValue()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto, loopInterval: 999);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(42.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_ClampsNonNegative()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, -5.0, 10.0, 1.0);
            Assert.Equal(0.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, double.NaN, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_ReturnsRecordingValue()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec, loopInterval: 25.0);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(25.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NegativePreserved()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Min, loopInterval: -30.0);
            // duration=100, so clamp is Max(-100+0.001, -30) = -30
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(-30.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_NullRec_ReturnsDefault()
        {
            double result = GhostPlaybackLogic.ResolveLoopInterval(null, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        // --- Unit helpers ---

        [Theory]
        [InlineData(LoopTimeUnit.Sec, "sec")]
        [InlineData(LoopTimeUnit.Min, "min")]
        [InlineData(LoopTimeUnit.Hour, "hr")]
        [InlineData(LoopTimeUnit.Auto, "auto")]
        public void UnitLabel_AllValues(LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitLabel(unit));
        }

        [Theory]
        [InlineData(LoopTimeUnit.Sec, "s")]
        [InlineData(LoopTimeUnit.Min, "m")]
        [InlineData(LoopTimeUnit.Hour, "h")]
        [InlineData(LoopTimeUnit.Auto, "s")] // Auto falls through to "s" — callers resolve to concrete unit first
        public void UnitSuffix_AllValues(LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitSuffix(unit));
        }

        [Fact]
        public void CycleRecordingUnit_FullCycle()
        {
            var u = LoopTimeUnit.Sec;
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Min, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Auto, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(LoopTimeUnit.Sec, u);
        }

        [Fact]
        public void CycleDisplayUnit_FullCycle_NoAuto()
        {
            var u = LoopTimeUnit.Sec;
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Min, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(LoopTimeUnit.Sec, u);
        }

        [Theory]
        [InlineData(3600.0, LoopTimeUnit.Sec, 3600.0)]
        [InlineData(3600.0, LoopTimeUnit.Min, 60.0)]
        [InlineData(3600.0, LoopTimeUnit.Hour, 1.0)]
        [InlineData(120.0, LoopTimeUnit.Min, 2.0)]
        public void ConvertFromSeconds_AllUnits(double seconds, LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertFromSeconds(seconds, unit), 6);
        }

        [Theory]
        [InlineData(10.0, LoopTimeUnit.Sec, 10.0)]
        [InlineData(2.0, LoopTimeUnit.Min, 120.0)]
        [InlineData(1.0, LoopTimeUnit.Hour, 3600.0)]
        public void ConvertToSeconds_AllUnits(double value, LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertToSeconds(value, unit), 6);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesLoopTimeUnit()
        {
            var source = new Recording
            {
                LoopTimeUnit = LoopTimeUnit.Auto,
            };
            var target = new Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(LoopTimeUnit.Auto, target.LoopTimeUnit);
        }

        // --- FormatLoopValue ---

        [Theory]
        [InlineData(0.0, LoopTimeUnit.Sec, "0")]
        [InlineData(5.5, LoopTimeUnit.Sec, "5")]
        [InlineData(10.0, LoopTimeUnit.Sec, "10")]
        [InlineData(1.5, LoopTimeUnit.Min, "1.5")]
        [InlineData(2.0, LoopTimeUnit.Min, "2.0")]
        [InlineData(0.5, LoopTimeUnit.Hour, "0.5")]
        [InlineData(1.0, LoopTimeUnit.Hour, "1.0")]
        public void FormatLoopValue_Formatting(double value, LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatLoopValue(value, unit));
        }

        // --- TryParseLoopInput ---

        [Theory]
        [InlineData("10", LoopTimeUnit.Sec, true, 10.0)]
        [InlineData("1.5", LoopTimeUnit.Sec, false, 0.0)]  // float rejected for sec
        [InlineData("1.5", LoopTimeUnit.Min, true, 1.5)]
        [InlineData("0.5", LoopTimeUnit.Hour, true, 0.5)]
        [InlineData("abc", LoopTimeUnit.Sec, false, 0.0)]
        [InlineData("abc", LoopTimeUnit.Min, false, 0.0)]
        [InlineData("", LoopTimeUnit.Sec, false, 0.0)]    // empty string
        [InlineData("", LoopTimeUnit.Min, false, 0.0)]    // empty string
        [InlineData("-5", LoopTimeUnit.Sec, true, -5.0)]  // negative allowed in recordings
        [InlineData("-1.5", LoopTimeUnit.Min, true, -1.5)]
        public void TryParseLoopInput_UnitRules(string text, LoopTimeUnit unit, bool expectedOk, double expectedVal)
        {
            double val;
            bool ok = ParsekUI.TryParseLoopInput(text, unit, out val);
            Assert.Equal(expectedOk, ok);
            if (expectedOk) Assert.Equal(expectedVal, val, 6);
        }

        // --- ResolveLoopInterval log assertions ---

        [Fact]
        public void ResolveLoopInterval_AutoMode_ShortDuration_Clamps()
        {
            // Recording only 5s long, global interval is 0 → clamp to -duration + minCycleDuration
            var rec = MakeRec(startUT: 100, endUT: 105, unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 0.0, 10.0, 1.0);
            Assert.Equal(0.0, result); // auto always >= 0
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_ShortDuration_Clamps()
        {
            // 5s recording, -10s interval → clamp to -(5 - 1.0) = -4.0
            var rec = MakeRec(startUT: 100, endUT: 105, loopInterval: -10.0,
                unit: LoopTimeUnit.Sec);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(-4.0, result, 6);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Auto);
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, double.PositiveInfinity, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Sec);
            rec.LoopIntervalSeconds = double.NaN;
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: LoopTimeUnit.Min);
            rec.LoopIntervalSeconds = double.PositiveInfinity;
            double result = GhostPlaybackLogic.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }
    }
}
