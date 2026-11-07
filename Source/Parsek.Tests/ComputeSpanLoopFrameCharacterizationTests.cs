using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Golden characterization of <see cref="GhostPlaybackLogic.ComputeSpanLoopFrame"/>: for each clock
    /// configuration (schedule with and without the phasing knob, the INV-3 guard, the uniform path, launch
    /// hold aligned and zero-slack with the boundary-overlap secondary, arrival hold with and without the joint
    /// rotation constraint, loiter cuts) a sweep of UTs is run and every output field (doubles as raw bits),
    /// every log line the call writes and every rate-limit key it touches is folded into one FNV-1a digest per
    /// configuration. The rate limiter is reset before each call so every call writes its full log set.
    /// Numbers inside log lines are canonicalized to their double bits, so the digest does not depend on how a
    /// runtime spells a round-trip ("R") number. Inputs are whole or half seconds so the arithmetic is exact.
    /// </summary>
    [Collection("Sequential")]
    public class ComputeSpanLoopFrameCharacterizationTests : IDisposable
    {
        private static readonly Regex NumberToken = new Regex(
            @"-?\d+(\.\d+)?(E[+-]?\d+)?", RegexOptions.CultureInvariant);

        private readonly List<string> logLines = new List<string>();

        public ComputeSpanLoopFrameCharacterizationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            GhostPlaybackLogic.ResetForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            GhostPlaybackLogic.ResetForTesting();
        }

        private sealed class Config
        {
            public double Anchor;
            public double S0;
            public double S1;
            public double Cadence;
            public Func<MissionRelaunchSchedule> Schedule;
            public List<GhostPlaybackLogic.LoopCut> Cuts;
            public double HoldSeconds;
            public double HoldAtUT = double.NaN;
            public double HoldAlign = double.NaN;
            public double RotationPeriod = double.NaN;
            public bool LaunchHold;
            public double SoiExit = double.NaN;
            public double JointPeriod = double.NaN;
            public double JointTolerance = double.NaN;
            public int JointMaxPeriods;
        }

        private static List<GhostPlaybackLogic.LoopCut> Cuts(params double[] startAndLength)
        {
            var list = new List<GhostPlaybackLogic.LoopCut>();
            for (int i = 0; i + 1 < startAndLength.Length; i += 2)
                list.Add(new GhostPlaybackLogic.LoopCut { StartUT = startAndLength[i], LengthSeconds = startAndLength[i + 1] });
            return list;
        }

        private static PhasingKnobConfig KnobConfig()
        {
            return new PhasingKnobConfig
            {
                RunStartUT = 100.0,
                RunEndUT = 1350.0,
                PeriodSeconds = 250.0,
                RecordedRevs = 5,
                StaticCuts = new List<GhostPlaybackLogic.LoopCut>(),
                SpanSeconds = 2000.0,
                ShiftPeriods = new[] { 600.0 },
                ShiftTolerances = new[] { 1.0 },
                ShiftMin = -4,
                ShiftMax = 10,
            };
        }

        private static string Bits(double d)
            => BitConverter.DoubleToInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

        private static string CanonicalizeNumbers(string line)
            => NumberToken.Replace(line, m => Bits(double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture)));

        private static IEnumerable<string> RateLimitKeys()
        {
            var prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add(CanonicalizeNumbers((string)k));
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private string Digest(Config c, out int resolvedCount, out int secondaryCount, out int logCount)
        {
            resolvedCount = 0;
            secondaryCount = 0;
            logCount = 0;
            MissionRelaunchSchedule schedule = c.Schedule != null ? c.Schedule() : null;
            var sb = new StringBuilder();
            for (double ut = -500.0; ut <= 20000.0; ut += 12.5)
            {
                ParsekLog.ResetRateLimitsForTesting();
                logLines.Clear();
                GhostPlaybackLogic.SpanLoopFrame f = GhostPlaybackLogic.ComputeSpanLoopFrame(
                    ut, c.Anchor, c.S0, c.S1, c.Cadence,
                    schedule, c.Cuts, c.HoldSeconds, c.HoldAtUT, c.HoldAlign,
                    c.RotationPeriod, c.LaunchHold, c.SoiExit,
                    c.JointPeriod, c.JointTolerance, c.JointMaxPeriods);
                sb.Append(Bits(ut)).Append('|')
                    .Append(f.Resolved ? '1' : '0').Append('|')
                    .Append(Bits(f.LoopUT)).Append('|')
                    .Append(f.CycleIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(f.IsInInterCycleTail ? '1' : '0').Append('|')
                    .Append(f.HasSecondary ? '1' : '0').Append('|')
                    .Append(Bits(f.SecondaryLoopUT)).Append('|')
                    .Append(f.SecondaryCycleIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
                for (int i = 0; i < logLines.Count; i++)
                    sb.Append("  L ").Append(CanonicalizeNumbers(logLines[i])).Append('\n');
                foreach (string k in RateLimitKeys())
                    sb.Append("  K ").Append(k).Append('\n');
                if (f.Resolved) resolvedCount++;
                if (f.HasSecondary) secondaryCount++;
                logCount += logLines.Count;
            }
            return Fnv1a64(sb.ToString());
        }

        private static string Fnv1a64(string s)
        {
            ulong h = 14695981039346656037UL;
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 1099511628211UL;
            }
            return h.ToString("X16", CultureInfo.InvariantCulture);
        }

        private void AssertGolden(Config c, string expectedDigest, int expectedResolved, int expectedSecondary, int expectedLogs)
        {
            string digest = Digest(c, out int resolved, out int secondary, out int logs);
            string actual = $"digest={digest} resolved={resolved} secondary={secondary} logs={logs}";
            string expected = $"digest={expectedDigest} resolved={expectedResolved} secondary={expectedSecondary} logs={expectedLogs}";
            Assert.True(expected == actual, "golden mismatch\nexpected " + expected + "\nactual   " + actual);
        }

        [Fact]
        public void Golden_DegenerateSpan()
        {
            AssertGolden(new Config { Anchor = 0, S0 = 1000, S1 = 1000, Cadence = 2000 },
                "D9B23D9AFE9AE4D2", 0, 0, 0);
        }

        [Fact]
        public void Golden_UniformNoHold()
        {
            AssertGolden(new Config { Anchor = 300, S0 = 0, S1 = 1000, Cadence = 2000 },
                "28FA318122D56E3C", 1577, 0, 0);
        }

        [Fact]
        public void Golden_UniformCadenceBelowSpan()
        {
            AssertGolden(new Config { Anchor = 300, S0 = 100, S1 = 1600, Cadence = 1000, Cuts = Cuts(400, 200) },
                "A59EAF173BA4501C", 1577, 0, 0);
        }

        [Fact]
        public void Golden_LaunchHoldAligned()
        {
            AssertGolden(new Config
            {
                Anchor = 300, S0 = 0, S1 = 1000, Cadence = 2000,
                RotationPeriod = 700, LaunchHold = true, SoiExit = 600,
            }, "E02C359A5F4304A0", 1577, 0, 1577);
        }

        [Fact]
        public void Golden_LaunchHoldZeroSlackBoundaryOverlap()
        {
            AssertGolden(new Config
            {
                Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1000,
                RotationPeriod = 700, LaunchHold = true, SoiExit = 600,
            }, "696F49BC01AE3179", 1577, 432, 4018);
        }

        [Fact]
        public void Golden_LaunchHoldWithArrivalHoldAndCuts()
        {
            AssertGolden(new Config
            {
                Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1500,
                RotationPeriod = 700, LaunchHold = true, SoiExit = 450,
                Cuts = Cuts(150, 100), HoldSeconds = 60, HoldAtUT = 800, HoldAlign = 100,
            }, "E65780B4F717BFBB", 1577, 96, 3826);
        }

        [Fact]
        public void Golden_ArrivalHoldAligned()
        {
            AssertGolden(new Config
            {
                Anchor = 55, S0 = 0, S1 = 1000, Cadence = 3000,
                Cuts = Cuts(200, 100), HoldSeconds = 45, HoldAtUT = 800, HoldAlign = 100,
            }, "CED4E5E3F4D12CAB", 1596, 0, 1596);
        }

        [Fact]
        public void Golden_ArrivalHoldOverflowsCadence()
        {
            AssertGolden(new Config
            {
                Anchor = 55, S0 = 0, S1 = 1000, Cadence = 1020,
                HoldSeconds = 45, HoldAtUT = 800, HoldAlign = 100,
            }, "83CB540AC645909D", 1596, 0, 1596);
        }

        [Fact]
        public void Golden_ArrivalJointHold()
        {
            AssertGolden(new Config
            {
                Anchor = 55, S0 = 0, S1 = 1000, Cadence = 12345,
                HoldSeconds = 45, HoldAtUT = 800, HoldAlign = 100,
                JointPeriod = 360, JointTolerance = 5, JointMaxPeriods = 64,
            }, "118832B4ED786EA3", 1596, 0, 1596);
        }

        [Fact]
        public void Golden_ArrivalJointHoldOverBudget()
        {
            AssertGolden(new Config
            {
                Anchor = 55, S0 = 0, S1 = 1000, Cadence = 4321,
                HoldSeconds = 45, HoldAtUT = 800, HoldAlign = 100,
                JointPeriod = 360, JointTolerance = 5, JointMaxPeriods = 1,
            }, "AD54359381424808", 1596, 0, 3192);
        }

        [Fact]
        public void Golden_ScheduleWithoutKnob()
        {
            AssertGolden(new Config
            {
                Anchor = 0, S0 = 0, S1 = 2000, Cadence = 2500,
                Schedule = () => new MissionRelaunchSchedule(
                    0.0, 1000.0, new double[0], new double[0], 0.0,
                    MissionPeriodicity.ScheduleLookaheadMultiples, 2500.0),
            }, "2B1CF9A0BC22AF03", 1521, 0, 0);
        }

        [Fact]
        public void Golden_ScheduleWithPhasingKnob()
        {
            AssertGolden(new Config
            {
                Anchor = 0, S0 = 0, S1 = 2000, Cadence = 2000,
                Schedule = () => new MissionRelaunchSchedule(
                    0.0, 1000.0, new double[0], new double[0], 0.0,
                    MissionPeriodicity.ScheduleLookaheadMultiples, 0.0, KnobConfig()),
            }, "907D3E3F117B22EF", 1521, 0, 0);
        }

        [Fact]
        public void Golden_ScheduleWithCutsTripsInv3Guard()
        {
            AssertGolden(new Config
            {
                Anchor = 0, S0 = 0, S1 = 2000, Cadence = 2500,
                Cuts = Cuts(300, 100), HoldSeconds = 30, HoldAtUT = 900,
                Schedule = () => new MissionRelaunchSchedule(
                    0.0, 1000.0, new double[0], new double[0], 0.0,
                    MissionPeriodicity.ScheduleLookaheadMultiples, 2500.0),
            }, "4221A661AF383157", 1521, 0, 1641);
        }
    }
}
