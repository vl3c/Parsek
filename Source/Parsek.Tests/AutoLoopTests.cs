using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class AutoLoopTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public AutoLoopTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        static RecordingStore.Recording MakeRec(
            double startUT = 100, double endUT = 200,
            double loopInterval = 10.0,
            RecordingStore.LoopTimeUnit unit = RecordingStore.LoopTimeUnit.Sec)
        {
            var rec = new RecordingStore.Recording
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
            var source = new RecordingStore.Recording
            {
                RecordingId = "auto-test",
                LoopPlayback = true,
                LoopIntervalSeconds = 30.0,
                LoopTimeUnit = RecordingStore.LoopTimeUnit.Auto,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(RecordingStore.LoopTimeUnit.Auto, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_SaveLoad_RoundTrip_Hour()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "hr-test",
                LoopTimeUnit = RecordingStore.LoopTimeUnit.Hour,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(RecordingStore.LoopTimeUnit.Hour, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Load_MissingKey_DefaultsSec()
        {
            var node = new ConfigNode("RECORDING");
            // No loopTimeUnit key at all
            var loaded = new RecordingStore.Recording();
            ParsekScenario.LoadRecordingMetadata(node, loaded);

            Assert.Equal(RecordingStore.LoopTimeUnit.Sec, loaded.LoopTimeUnit);
        }

        [Fact]
        public void LoopTimeUnit_Save_DefaultSec_NotWritten()
        {
            var source = new RecordingStore.Recording
            {
                RecordingId = "sec-default",
                LoopTimeUnit = RecordingStore.LoopTimeUnit.Sec,
            };
            var node = new ConfigNode("RECORDING");
            ParsekScenario.SaveRecordingMetadata(node, source);

            Assert.Null(node.GetValue("loopTimeUnit"));
        }

        // --- ResolveLoopInterval ---

        [Fact]
        public void ResolveLoopInterval_AutoMode_ReturnsGlobalValue()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Auto, loopInterval: 999);
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(42.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_ClampsNonNegative()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Auto);
            double result = ParsekFlight.ResolveLoopInterval(rec, -5.0, 10.0, 1.0);
            Assert.Equal(0.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Auto);
            double result = ParsekFlight.ResolveLoopInterval(rec, double.NaN, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_ReturnsRecordingValue()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Sec, loopInterval: 25.0);
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(25.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NegativePreserved()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Min, loopInterval: -30.0);
            // duration=100, so clamp is Max(-100+0.001, -30) = -30
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(-30.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_NullRec_ReturnsDefault()
        {
            double result = ParsekFlight.ResolveLoopInterval(null, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        // --- Unit helpers ---

        [Theory]
        [InlineData(RecordingStore.LoopTimeUnit.Sec, "sec")]
        [InlineData(RecordingStore.LoopTimeUnit.Min, "min")]
        [InlineData(RecordingStore.LoopTimeUnit.Hour, "hr")]
        [InlineData(RecordingStore.LoopTimeUnit.Auto, "auto")]
        public void UnitLabel_AllValues(RecordingStore.LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.UnitLabel(unit));
        }

        [Fact]
        public void CycleRecordingUnit_FullCycle()
        {
            var u = RecordingStore.LoopTimeUnit.Sec;
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Min, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Auto, u);
            u = ParsekUI.CycleRecordingUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Sec, u);
        }

        [Fact]
        public void CycleDisplayUnit_FullCycle_NoAuto()
        {
            var u = RecordingStore.LoopTimeUnit.Sec;
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Min, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Hour, u);
            u = ParsekUI.CycleDisplayUnit(u); Assert.Equal(RecordingStore.LoopTimeUnit.Sec, u);
        }

        [Theory]
        [InlineData(3600.0, RecordingStore.LoopTimeUnit.Sec, 3600.0)]
        [InlineData(3600.0, RecordingStore.LoopTimeUnit.Min, 60.0)]
        [InlineData(3600.0, RecordingStore.LoopTimeUnit.Hour, 1.0)]
        [InlineData(120.0, RecordingStore.LoopTimeUnit.Min, 2.0)]
        public void ConvertFromSeconds_AllUnits(double seconds, RecordingStore.LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertFromSeconds(seconds, unit), 6);
        }

        [Theory]
        [InlineData(10.0, RecordingStore.LoopTimeUnit.Sec, 10.0)]
        [InlineData(2.0, RecordingStore.LoopTimeUnit.Min, 120.0)]
        [InlineData(1.0, RecordingStore.LoopTimeUnit.Hour, 3600.0)]
        public void ConvertToSeconds_AllUnits(double value, RecordingStore.LoopTimeUnit unit, double expected)
        {
            Assert.Equal(expected, ParsekUI.ConvertToSeconds(value, unit), 6);
        }

        // --- ApplyPersistenceArtifactsFrom ---

        [Fact]
        public void ApplyPersistenceArtifactsFrom_CopiesLoopTimeUnit()
        {
            var source = new RecordingStore.Recording
            {
                LoopTimeUnit = RecordingStore.LoopTimeUnit.Auto,
            };
            var target = new RecordingStore.Recording();
            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(RecordingStore.LoopTimeUnit.Auto, target.LoopTimeUnit);
        }

        // --- FormatLoopValue ---

        [Theory]
        [InlineData(0.0, RecordingStore.LoopTimeUnit.Sec, "0")]
        [InlineData(5.5, RecordingStore.LoopTimeUnit.Sec, "5")]
        [InlineData(10.0, RecordingStore.LoopTimeUnit.Sec, "10")]
        [InlineData(1.5, RecordingStore.LoopTimeUnit.Min, "1.5")]
        [InlineData(2.0, RecordingStore.LoopTimeUnit.Min, "2.0")]
        [InlineData(0.5, RecordingStore.LoopTimeUnit.Hour, "0.5")]
        [InlineData(1.0, RecordingStore.LoopTimeUnit.Hour, "1.0")]
        public void FormatLoopValue_Formatting(double value, RecordingStore.LoopTimeUnit unit, string expected)
        {
            Assert.Equal(expected, ParsekUI.FormatLoopValue(value, unit));
        }

        // --- TryParseLoopInput ---

        [Theory]
        [InlineData("10", RecordingStore.LoopTimeUnit.Sec, true, 10.0)]
        [InlineData("1.5", RecordingStore.LoopTimeUnit.Sec, false, 0.0)]  // float rejected for sec
        [InlineData("1.5", RecordingStore.LoopTimeUnit.Min, true, 1.5)]
        [InlineData("0.5", RecordingStore.LoopTimeUnit.Hour, true, 0.5)]
        [InlineData("abc", RecordingStore.LoopTimeUnit.Sec, false, 0.0)]
        [InlineData("abc", RecordingStore.LoopTimeUnit.Min, false, 0.0)]
        [InlineData("", RecordingStore.LoopTimeUnit.Sec, false, 0.0)]    // empty string
        [InlineData("", RecordingStore.LoopTimeUnit.Min, false, 0.0)]    // empty string
        [InlineData("-5", RecordingStore.LoopTimeUnit.Sec, true, -5.0)]  // negative allowed in recordings
        [InlineData("-1.5", RecordingStore.LoopTimeUnit.Min, true, -1.5)]
        public void TryParseLoopInput_UnitRules(string text, RecordingStore.LoopTimeUnit unit, bool expectedOk, double expectedVal)
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
            var rec = MakeRec(startUT: 100, endUT: 105, unit: RecordingStore.LoopTimeUnit.Auto);
            double result = ParsekFlight.ResolveLoopInterval(rec, 0.0, 10.0, 1.0);
            Assert.Equal(0.0, result); // auto always >= 0
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_ShortDuration_Clamps()
        {
            // 5s recording, -10s interval → clamp to -(5 - 1.0) = -4.0
            var rec = MakeRec(startUT: 100, endUT: 105, loopInterval: -10.0,
                unit: RecordingStore.LoopTimeUnit.Sec);
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(-4.0, result, 6);
        }

        [Fact]
        public void ResolveLoopInterval_AutoMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Auto);
            double result = ParsekFlight.ResolveLoopInterval(rec, double.PositiveInfinity, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_NaN_ReturnsDefault()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Sec);
            rec.LoopIntervalSeconds = double.NaN;
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }

        [Fact]
        public void ResolveLoopInterval_ManualMode_Infinity_ReturnsDefault()
        {
            var rec = MakeRec(unit: RecordingStore.LoopTimeUnit.Min);
            rec.LoopIntervalSeconds = double.PositiveInfinity;
            double result = ParsekFlight.ResolveLoopInterval(rec, 42.0, 10.0, 1.0);
            Assert.Equal(10.0, result);
        }
    }
}
