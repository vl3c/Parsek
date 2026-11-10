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
        public void Golden_LaunchHoldAlignedRegionBFlip()
        {
            // Slack 300 s: the aligned loop's early launch of N+1 flips the single output in region B.
            AssertGolden(new Config
            {
                Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1300,
                RotationPeriod = 700, LaunchHold = true, SoiExit = 600,
            }, "78F680AF5B762B58", 1577, 144, 2697);
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
        /// <summary>
        /// Which launch-advance diagnostic branches the launch-hold goldens reach: the line kinds written and the
        /// region / boundary-overlap / secondary / capped tokens of the per-loop line, collected over every
        /// launch-hold configuration above plus a cadence whose region B flips the primary on an aligned loop.
        /// A golden digest proves a branch unchanged only when some configuration reaches it. The fourth
        /// configuration (cadence 1300, Golden_LaunchHoldAlignedRegionBFlip) adds the aligned loop's region-B flip.
        /// </summary>
        [Fact]
        public void LaunchAdvanceDiagnostics_BranchesReachedByTheGoldens()
        {
            var configs = new[]
            {
                new Config { Anchor = 300, S0 = 0, S1 = 1000, Cadence = 2000, RotationPeriod = 700, LaunchHold = true, SoiExit = 600 },
                new Config { Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1000, RotationPeriod = 700, LaunchHold = true, SoiExit = 600 },
                new Config
                {
                    Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1500,
                    RotationPeriod = 700, LaunchHold = true, SoiExit = 450,
                    Cuts = Cuts(150, 100), HoldSeconds = 60, HoldAtUT = 800, HoldAlign = 100,
                },
                new Config { Anchor = 300, S0 = 0, S1 = 1000, Cadence = 1300, RotationPeriod = 700, LaunchHold = true, SoiExit = 600 },
            };
            var reached = new SortedSet<string>(StringComparer.Ordinal);
            var tokenPattern = new Regex(
                @"(region=\S+|boundaryOverlap=\S+|secondaryActive=\S+|capped=\S+)", RegexOptions.CultureInvariant);
            foreach (Config c in configs)
            {
                for (double ut = -500.0; ut <= 20000.0; ut += 12.5)
                {
                    ParsekLog.ResetRateLimitsForTesting();
                    logLines.Clear();
                    GhostPlaybackLogic.ComputeSpanLoopFrame(
                        ut, c.Anchor, c.S0, c.S1, c.Cadence,
                        null, c.Cuts, c.HoldSeconds, c.HoldAtUT, c.HoldAlign,
                        c.RotationPeriod, c.LaunchHold, c.SoiExit);
                    foreach (string line in logLines)
                    {
                        int colon = line.IndexOf(':', line.IndexOf("[Reaim]", StringComparison.Ordinal) + 1);
                        string kind = line.Substring(0, colon < 0 ? line.Length : colon);
                        reached.Add("line " + kind);
                        if (line.Contains("per-loop launch advance:"))
                            foreach (Match m in tokenPattern.Matches(line))
                                reached.Add("token " + m.Value);
                    }
                }
            }
            // Not reachable through the frame: the CAPPED warning and capped=True. The frame's advance is the full
            // raw delta whenever that is positive (ComputeBoundaryOverlapAdvanceSeconds), and an aligned flip's
            // advNext equals its raw delta, so the rendered raw delta never exceeds the advance used. The direct
            // test of the diagnostic unit drives that branch instead.
            string actual = string.Join("\n", reached);
            string expected = string.Join("\n", new[]
            {
                "line [Parsek][VERBOSE][Reaim] boundary-overlap engaged",
                "line [Parsek][VERBOSE][Reaim] boundary-overlap secondary clock-launch",
                "line [Parsek][VERBOSE][Reaim] launch instant",
                "line [Parsek][VERBOSE][Reaim] per-loop arrival hold",
                "line [Parsek][VERBOSE][Reaim] per-loop launch advance",
                "token boundaryOverlap=False",
                "token boundaryOverlap=True",
                "token capped=False",
                "token region=A-current",
                "token region=B-earlyNext",
                "token secondaryActive=False",
                "token secondaryActive=True",
            });
            Assert.True(actual == expected, "reached:\n" + actual);
        }

        // ------------------------------------------------------------------
        // The three extracted pieces, called directly.
        // ------------------------------------------------------------------

        [Fact]
        public void ScheduledFrame_MatchesTheFullFrame_AndParksInTheGap()
        {
            var forHelper = new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 2500.0);
            var forFrame = new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 2500.0);
            double first = forHelper.FirstLaunchUT;
            Assert.False(double.IsNaN(first));

            GhostPlaybackLogic.SpanLoopFrame before = GhostPlaybackLogic.ComputeScheduledSpanLoopFrame(
                first - 10.0, 0.0, 0.0, 2000.0, forHelper, null, 0.0);
            Assert.False(before.Resolved);

            GhostPlaybackLogic.SpanLoopFrame inSpan = GhostPlaybackLogic.ComputeScheduledSpanLoopFrame(
                first + 500.0, 0.0, 0.0, 2000.0, forHelper, null, 0.0);
            Assert.True(inSpan.Resolved);
            Assert.Equal(500.0, inSpan.LoopUT);
            Assert.False(inSpan.IsInInterCycleTail);
            Assert.False(inSpan.HasSecondary);

            GhostPlaybackLogic.SpanLoopFrame gap = GhostPlaybackLogic.ComputeScheduledSpanLoopFrame(
                first + 2200.0, 0.0, 0.0, 2000.0, forHelper, null, 0.0);
            Assert.True(gap.Resolved);
            Assert.Equal(2000.0, gap.LoopUT);
            Assert.True(gap.IsInInterCycleTail);

            for (double ut = first - 100.0; ut < first + 9000.0; ut += 250.0)
            {
                GhostPlaybackLogic.SpanLoopFrame a = GhostPlaybackLogic.ComputeScheduledSpanLoopFrame(
                    ut, 0.0, 0.0, 2000.0, forHelper, null, 0.0);
                GhostPlaybackLogic.SpanLoopFrame b = GhostPlaybackLogic.ComputeSpanLoopFrame(
                    ut, 0.0, 0.0, 2000.0, 2500.0, forFrame);
                Assert.Equal(b.Resolved, a.Resolved);
                Assert.Equal(b.LoopUT, a.LoopUT);
                Assert.Equal(b.CycleIndex, a.CycleIndex);
                Assert.Equal(b.IsInInterCycleTail, a.IsInInterCycleTail);
            }
        }

        [Fact]
        public void ScheduledFrame_WithCuts_WritesTheInv3Line()
        {
            var schedule = new MissionRelaunchSchedule(
                0.0, 1000.0, new double[0], new double[0], 0.0,
                MissionPeriodicity.ScheduleLookaheadMultiples, 2500.0);
            GhostPlaybackLogic.ComputeScheduledSpanLoopFrame(
                schedule.FirstLaunchUT + 1.0, 0.0, 0.0, 2000.0, schedule, Cuts(300, 100), 0.0);
            Assert.Contains(logLines, l => l.Contains("[MissionPeriodicity]")
                && l.Contains("INV-3 contract violation: a scheduled unit carries loiterCuts=1 arrivalHoldSeconds=0"));
        }

        [Fact]
        public void ArrivalHold_NoAlignPeriod_ReturnsBaseHoldSilently()
        {
            double w = GhostPlaybackLogic.ResolveSpanLoopFrameArrivalHold(
                45.0, 3, 3030.0, double.NaN, 55.0, 0.0, null, 800.0, double.NaN, double.NaN, 0);
            Assert.Equal(45.0, w);
            Assert.Empty(logLines);
        }

        [Fact]
        public void ArrivalHold_AlignPeriod_ReturnsPerLoopHoldAndLogsIt()
        {
            double w = GhostPlaybackLogic.ResolveSpanLoopFrameArrivalHold(
                45.0, 3, 3030.0, 100.0, 55.0, 0.0, null, 800.0, double.NaN, double.NaN, 0);
            // (45 - 3 * (3030 mod 100)) mod 100 = -45 -> 55
            Assert.Equal(55.0, w);
            Assert.Equal(GhostPlaybackLogic.ComputePerLoopArrivalHoldSeconds(45.0, 3, 3030.0, 100.0), w);
            Assert.Single(logLines);
            Assert.Contains("[Parsek][VERBOSE][Reaim] per-loop arrival hold: cycleIndex=3 W0=45s cadence=3030s "
                + "Talign=100s joint=False WN=55s", logLines[0]);
        }

        [Fact]
        public void ArrivalHold_JointOverBudget_WarnsThenLogsTheHold()
        {
            double w = GhostPlaybackLogic.ResolveSpanLoopFrameArrivalHold(
                45.0, 0, 4321.0, 100.0, 55.0, 0.0, null, 800.0, 360.0, 5.0, 1);
            Assert.True(w > 0.0);
            Assert.Equal(2, logLines.Count);
            Assert.StartsWith("[Parsek][WARN][Reaim] per-loop JOINT arrival hold exceeded its budget", logLines[0]);
            Assert.StartsWith("[Parsek][VERBOSE][Reaim] per-loop arrival hold: cycleIndex=0 W0=45s", logLines[1]);
            Assert.Contains("joint=True", logLines[1]);
        }

        [Fact]
        public void Secondary_RunsTheBorrowPhaseThroughTheSoiRepay()
        {
            // borrow window: phaseInCycle 900, cadence 1000, advNext 300 -> secondary phase 200.
            Assert.Equal(200.0, GhostPlaybackLogic.ComputeBoundaryOverlapSecondaryLoopUT(
                900.0, 1000.0, 300.0, 600.0, null, 0.0, 1000.0, 0.0, double.NaN));
            // SOI exit at 100: phase 200 sits inside the 300 s repay window -> held at the exit.
            Assert.Equal(100.0, GhostPlaybackLogic.ComputeBoundaryOverlapSecondaryLoopUT(
                900.0, 1000.0, 300.0, 100.0, null, 0.0, 1000.0, 0.0, double.NaN));
            // Span start offset and a cut before the phase: decompression adds the cut back.
            Assert.Equal(5000.0 + 200.0 + 50.0, GhostPlaybackLogic.ComputeBoundaryOverlapSecondaryLoopUT(
                900.0, 1000.0, 300.0, 5000.0 + 900.0, Cuts(5100.0, 50.0), 5000.0, 950.0, 0.0, double.NaN));
        }

        [Fact]
        public void Secondary_MatchesTheFrameSecondaryOnTheZeroSlackLoop()
        {
            int checkedCount = 0;
            for (double ut = 300.0; ut < 12000.0; ut += 12.5)
            {
                GhostPlaybackLogic.SpanLoopFrame f = GhostPlaybackLogic.ComputeSpanLoopFrame(
                    ut, 300.0, 0.0, 1000.0, 1000.0,
                    launchBodyRotationPeriod: 700.0, launchHoldEngaged: true, soiExitAtUT: 600.0);
                if (!f.HasSecondary)
                    continue;
                long n = f.SecondaryCycleIndex;
                double advNext = GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                    300.0, 0.0, 1000.0, 1000.0, n, 700.0, null, 0.0, double.NaN);
                double elapsed = ut - 300.0;
                double phaseInCycle = elapsed - ((long)(elapsed / 1000.0)) * 1000.0;
                Assert.Equal(f.SecondaryLoopUT, GhostPlaybackLogic.ComputeBoundaryOverlapSecondaryLoopUT(
                    phaseInCycle, 1000.0, advNext, 600.0, null, 0.0, 1000.0, 0.0, double.NaN));
                checkedCount++;
            }
            Assert.True(checkedCount > 10, "the zero-slack fixture must exercise the secondary");
        }

        // ------------------------------------------------------------------
        // The W9a-3 units, called directly.
        // ------------------------------------------------------------------

        [Fact]
        public void LaunchAdvances_NotEngaged_AreAllZero_AndSilent()
        {
            double adv = GhostPlaybackLogic.ResolveSpanLoopLaunchAdvances(
                false, 300.0, 0.0, 1000.0, 1000.0, 4, 700.0, null, 0.0, double.NaN, double.NaN,
                double.NaN, double.NaN, 0, out double raw, out double capped);
            Assert.Equal(0.0, adv);
            Assert.Equal(0.0, raw);
            Assert.Equal(0.0, capped);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LaunchAdvances_Engaged_AreTheThreeAdvanceHelpersForTheWindow()
        {
            // Zero-slack loop (cadence == span) and a slack loop with cuts and an arrival hold.
            var cuts = Cuts(150, 100);
            foreach (double cadence in new[] { 1000.0, 1500.0 })
            {
                for (long window = 0; window < 12; window++)
                {
                    double adv = GhostPlaybackLogic.ResolveSpanLoopLaunchAdvances(
                        true, 300.0, 0.0, 1000.0, cadence, window, 700.0, cuts, 60.0, 100.0, 800.0,
                        double.NaN, double.NaN, 0, out double raw, out double capped);
                    Assert.Equal(GhostPlaybackLogic.ComputePerLoopLaunchAdvanceSeconds(
                        300.0, 0.0, window, cadence, 700.0), raw);
                    Assert.Equal(GhostPlaybackLogic.ComputeCappedLaunchAdvanceSeconds(
                        300.0, 0.0, 1000.0, cadence, window, 700.0, cuts, 60.0, 100.0, 800.0,
                        double.NaN, double.NaN, 0), capped);
                    Assert.Equal(GhostPlaybackLogic.ComputeBoundaryOverlapAdvanceSeconds(
                        300.0, 0.0, 1000.0, cadence, window, 700.0, cuts, 60.0, 100.0, 800.0,
                        double.NaN, double.NaN, 0), adv);
                }
            }
        }

        [Fact]
        public void HoldsAndSlack_NoCutNoHold_SlackIsTheIdleGap_FlooredAtZero()
        {
            double slack = GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                1000.0, 1300.0, 3, 0.0, 0.0, null, 0.0, double.NaN, double.NaN,
                double.NaN, double.NaN, 0, out double compressed, out double hold);
            Assert.Equal(1000.0, compressed);
            Assert.Equal(0.0, hold);
            Assert.Equal(300.0, slack);

            slack = GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                1000.0, 800.0, 3, 0.0, 0.0, null, 0.0, double.NaN, double.NaN,
                double.NaN, double.NaN, 0, out compressed, out hold);
            Assert.Equal(0.0, slack);
            Assert.Empty(logLines);
        }

        [Fact]
        public void HoldsAndSlack_CutsCompressTheSpan_UnlessTheyCoverIt()
        {
            GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                1000.0, 2000.0, 0, 0.0, 0.0, Cuts(150, 100, 400, 50), 0.0, double.NaN, double.NaN,
                double.NaN, double.NaN, 0, out double compressed, out double hold);
            Assert.Equal(850.0, compressed);
            GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                100.0, 2000.0, 0, 0.0, 0.0, Cuts(10, 100), 0.0, double.NaN, double.NaN,
                double.NaN, double.NaN, 0, out compressed, out hold);
            Assert.Equal(100.0, compressed);
        }

        [Fact]
        public void HoldsAndSlack_HoldIsTheResolvedPerLoopHold_AndAnInfiniteHoldIsNone()
        {
            for (long cycle = 0; cycle < 6; cycle++)
            {
                double slack = GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                    1000.0, 3000.0, cycle, 55.0, 0.0, Cuts(200, 100), 45.0, 800.0, 100.0,
                    double.NaN, double.NaN, 0, out double compressed, out double hold);
                double expectedHold = GhostPlaybackLogic.ResolveSpanLoopFrameArrivalHold(
                    45.0, cycle, 3000.0, 100.0, 55.0, 0.0, Cuts(200, 100), 800.0, double.NaN, double.NaN, 0);
                Assert.Equal(900.0, compressed);
                Assert.Equal(expectedHold, hold);
                Assert.Equal(3000.0 - 900.0 - expectedHold, slack);
            }
            logLines.Clear();
            GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                1000.0, 3000.0, 0, 55.0, 0.0, null, double.PositiveInfinity, 800.0, 100.0,
                double.NaN, double.NaN, 0, out double c2, out double h2);
            Assert.Equal(0.0, h2);
            Assert.Empty(logLines);
        }

        [Fact]
        public void HoldsAndSlack_HoldPastTheCadence_IsClampedAfterTheSlackFloor()
        {
            // span 1000, cadence 1020, no align period: W_N stays the base 45 s, which overflows by 25 s.
            double slack = GhostPlaybackLogic.ResolveSpanLoopHoldsAndSlack(
                1000.0, 1020.0, 2, 55.0, 0.0, null, 45.0, 800.0, double.NaN,
                double.NaN, double.NaN, 0, out double compressed, out double hold);
            Assert.Equal(1000.0, compressed);
            Assert.Equal(20.0, hold);
            Assert.Equal(0.0, slack);
        }

        private static List<string> RawRateLimitKeys()
        {
            var prop = typeof(ParsekLog).GetProperty(
                "rateLimitStateByKey", BindingFlags.Static | BindingFlags.NonPublic);
            var dict = (IDictionary)prop.GetValue(null);
            var keys = new List<string>();
            foreach (object k in dict.Keys)
                keys.Add((string)k);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static List<string> LaunchAdvanceLines(
            bool earlyNextFlip, double rawLaunchAdvance, double cappedLaunchAdvance, double effectiveLaunchAdvance,
            bool engagedA, double rawAdvNext, double cappedAdvNext, double advNext, bool engagedNext,
            bool hasSecondary, double tSid = 700.0, List<string> sink = null)
        {
            GhostPlaybackLogic.LogSpanLoopLaunchAdvance(
                4321.5, 300.0, 0.0, tSid, 4, 1000.0, 900.0, 40.0, 0.0, double.NaN, earlyNextFlip,
                rawLaunchAdvance, cappedLaunchAdvance, effectiveLaunchAdvance, engagedA,
                rawAdvNext, cappedAdvNext, advNext, engagedNext,
                hasSecondary, 125.0, 5);
            return sink;
        }

        [Fact]
        public void LaunchAdvanceLog_DegenerateRotationPeriod_WritesNothing()
        {
            foreach (double tSid in new[] { double.NaN, 0.0, double.PositiveInfinity })
                LaunchAdvanceLines(false, 10, 10, 10, false, 20, 20, 20, false, false, tSid);
            Assert.Empty(logLines);
        }

        [Fact]
        public void LaunchAdvanceLog_RegionA_WritesOnlyThePerLoopLine_WithTheLaunchingCycleSlack()
        {
            // Region A, aligned: no hold, so slack_{N-1} = cadence - compressedSpan = 100.
            LaunchAdvanceLines(false, 10, 10, 10, false, 20, 20, 20, false, false);
            Assert.Equal(new List<string>
            {
                "[Parsek][VERBOSE][Reaim] per-loop launch advance: cycleIndex=4 Tsid=700s cadence=1000s deltaN=10s "
                    + "slack=100s cappedAdv=10s boundaryOverlap=False region=A-current secondaryActive=False "
                    + "secondaryLoopUT=(none) capped=False rawDeltaN=10s residualDeg=0",
            }, logLines);
            Assert.Equal(new[] { "Reaim|perloop-launch-advance.300.0" }, RawRateLimitKeys());
        }

        [Fact]
        public void LaunchAdvanceLog_RegionBFlip_ReportsTheNextInstance_WithThisCycleSlack()
        {
            // The flip renders N+1 with advNext as its advance; its launching cycle is this one (slack_N = 40).
            LaunchAdvanceLines(true, 10, 10, 20, false, 20, 20, 20, false, false);
            Assert.Single(logLines);
            Assert.Contains("deltaN=20s slack=40s cappedAdv=20s boundaryOverlap=False region=B-earlyNext", logLines[0]);
            Assert.EndsWith("capped=False rawDeltaN=20s residualDeg=0", logLines[0]);
        }

        [Fact]
        public void LaunchAdvanceLog_CappedEngagedAndSecondary_WriteTheirLinesInOrder()
        {
            LaunchAdvanceLines(false, 30, 10, 10, true, 20, 5, 20, true, true);
            Assert.Equal(5, logLines.Count);
            Assert.StartsWith("[Parsek][VERBOSE][Reaim] per-loop launch advance: cycleIndex=4", logLines[0]);
            Assert.Contains("boundaryOverlap=True region=A-current secondaryActive=True secondaryLoopUT=125 capped=True", logLines[0]);
            Assert.Equal("[Parsek][WARN][Reaim] per-loop launch advance still CAPPED (boundary overlap could not use the "
                + "full raw delta - genuinely uncloseable seam); cycleIndex=4 cadence=1000s rawDeltaN=30s "
                + "residualDeg=" + (20.0 / 700.0 * 360.0).ToString("R", CultureInfo.InvariantCulture), logLines[1]);
            Assert.StartsWith("[Parsek][VERBOSE][Reaim] boundary-overlap engaged: ", logLines[2]);
            Assert.Equal("[Parsek][VERBOSE][Reaim] launch instant: secondary instance N=5 launches at UT=5280 "
                + "(nominal L_N=5300 advance=20s)", logLines[3]);
            Assert.Equal("[Parsek][VERBOSE][Reaim] boundary-overlap secondary clock-launch: secondaryCycle=5 "
                + "currentUT=4321.5 secondaryLoopUT=125 spanStart=0", logLines[4]);
            Assert.Equal(new[]
            {
                "Reaim|boundary-overlap-engaged.300.0",
                "Reaim|boundary-overlap-secondary-clock-launch.300.0",
                "Reaim|launch-instant.300.0",
                "Reaim|perloop-launch-advance.300.0",
                "W|Reaim|launch-advance-capped.300.0",
            }, RawRateLimitKeys());
        }
    }
}
