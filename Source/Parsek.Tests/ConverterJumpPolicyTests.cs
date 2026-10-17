using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TIMEJUMP-CONVERTER-POLICY-DIFFERS-BY-JUMP-KIND (TA-6): one converter policy for both
    /// Parsek jump kinds, loaded and unloaded vessels alike.
    /// </summary>
    [Collection("Sequential")]
    public class ConverterJumpPolicyTests : IDisposable
    {
        private static readonly CultureInfo ic = CultureInfo.InvariantCulture;

        public ConverterJumpPolicyTests()
        {
            ParsekLog.TestSinkForTesting = line => { };
        }

        public void Dispose()
        {
            ConverterJumpPolicy.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static List<KeyValuePair<double, double>> Spans(params double[] prePost)
        {
            var list = new List<KeyValuePair<double, double>>();
            for (int i = 0; i + 1 < prePost.Length; i += 2)
                list.Add(new KeyValuePair<double, double>(prePost[i], prePost[i + 1]));
            return list;
        }

        // A vessel that spawns after a jump crossed its recording end arrives with its end
        // snapshot (owner ruling 2026-10-17, MINE-9: the rover came up with 4.3 Ore it never mined).

        [Fact]
        public void SpawnSnapshot_EndInsideTheJump_ArrivesAtTheJumpTarget()
        {
            // MINE-9: recorded end 1829.9, TimeJump 60 -> 5500, spawn at 5500.
            var outcome = ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                1829.9, Spans(60.0, 5500.0), currentUT: 5500.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(5500.0, newTime, 6);
        }

        [Fact]
        public void SpawnSnapshot_EndBeforeTheJump_KeepsTheTimeThatReallyPassed()
        {
            // End 1000, played on to 2000, jumped 2000 -> 9000, spawned later at 9100:
            // 1000 s before the jump and 100 s after it are real; 7000 s are removed.
            var outcome = ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                1000.0, Spans(2000.0, 9000.0), currentUT: 9100.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(8000.0, newTime, 6);
            Assert.Equal(1100.0, 9100.0 - newTime, 6);
        }

        [Fact]
        public void SpawnSnapshot_SeveralJumps_RemovesEachJumpedPartAfterTheClock()
        {
            var outcome = ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                1500.0, Spans(100.0, 1000.0, 1200.0, 3000.0, 3500.0, 6000.0), currentUT: 6000.0,
                out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            // 1500..3000 and 3500..6000 removed; 3000..3500 really passed.
            Assert.Equal(5500.0, newTime, 6);
        }

        [Fact]
        public void SpawnSnapshot_TakenAfterTheJump_OrNoJump_OrNeverRan_IsUntouched()
        {
            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NoJump,
                ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                    6000.0, Spans(60.0, 5500.0), currentUT: 6100.0, out double after));
            Assert.Equal(6000.0, after);
            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NoJump,
                ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                    1829.9, Spans(), currentUT: 1830.0, out double none));
            Assert.Equal(1829.9, none);
            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NeverRan,
                ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                    0.0, Spans(60.0, 5500.0), currentUT: 5500.0, out double never));
            Assert.Equal(0.0, never);
        }

        [Fact]
        public void SpawnSnapshot_SpanFromAFutureTheClockLeft_IsIgnored()
        {
            // Jumped 60 -> 5500, then a rewind took the clock back to 30; a spawn at 1830
            // during normal play must not lose the abandoned jump.
            var outcome = ConverterJumpPolicy.ComputeSpawnSnapshotTimestamp(
                1829.9, Spans(60.0, 5500.0), currentUT: 1830.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NoJump, outcome);
            Assert.Equal(1829.9, newTime);
        }

        [Fact]
        public void SpawnSnapshotModules_ShiftsOnlyConverterClocks()
        {
            var vessel = new ConfigNode("VESSEL");
            var drill = vessel.AddNode("PART");
            var harvester = drill.AddNode("MODULE");
            harvester.AddValue("name", "ModuleResourceHarvester");
            harvester.AddValue("lastUpdateTime", "1829.9");
            var anim = drill.AddNode("MODULE");
            anim.AddValue("name", "ModuleAnimationGroup");
            anim.AddValue("lastUpdateTime", "1829.9");
            var isru = vessel.AddNode("PART").AddNode("MODULE");
            isru.AddValue("name", "ModuleResourceConverter");
            isru.AddValue("lastUpdateTime", "1829.9");

            int shifted = ConverterJumpPolicy.ShiftSpawnSnapshotModules(
                vessel, Spans(60.0, 5500.0), 5500.0,
                n => n == "ModuleResourceHarvester" || n == "ModuleResourceConverter",
                out double maxRemoved);

            Assert.Equal(2, shifted);
            Assert.Equal(3670.1, maxRemoved, 6);
            Assert.Equal(5500.0, double.Parse(harvester.GetValue("lastUpdateTime"), ic), 6);
            Assert.Equal(5500.0, double.Parse(isru.GetValue("lastUpdateTime"), ic), 6);
            Assert.Equal("1829.9", anim.GetValue("lastUpdateTime"));
        }

        [Fact]
        public void JumpedSpans_AreNotedCappedAndCleared()
        {
            ConverterJumpPolicy.ResetForTesting();
            ConverterJumpPolicy.NoteJumpedSpan(60.0, 5500.0);
            ConverterJumpPolicy.NoteJumpedSpan(5500.0, 5500.0); // not a jump
            Assert.Equal(1, ConverterJumpPolicy.JumpedSpanCount);
            for (int i = 0; i < ConverterJumpPolicy.MaxJumpedSpans + 5; i++)
                ConverterJumpPolicy.NoteJumpedSpan(10000.0 + i * 10, 10005.0 + i * 10);
            Assert.Equal(ConverterJumpPolicy.MaxJumpedSpans, ConverterJumpPolicy.JumpedSpanCount);
            ConverterJumpPolicy.ClearJumpedSpans("test");
            Assert.Equal(0, ConverterJumpPolicy.JumpedSpanCount);
        }

        [Fact]
        public void LoadedConverter_AtJumpStart_EndsAtJumpTarget()
        {
            var outcome = ConverterJumpPolicy.ComputeShiftedTimestamp(
                oldTime: 1000.0, preJumpUT: 1000.0, postJumpUT: 51000.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(51000.0, newTime);
        }

        [Fact]
        public void UnloadedConverter_KeepsTheCatchUpItOwedBeforeTheJump()
        {
            // An unloaded miner last ran at 400 (600 s owed before the jump); the jump adds
            // 50000 s that must not be production time. After the jump it still owes 600 s.
            var outcome = ConverterJumpPolicy.ComputeShiftedTimestamp(
                oldTime: 400.0, preJumpUT: 1000.0, postJumpUT: 51000.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(50400.0, newTime);
            Assert.Equal(600.0, 51000.0 - newTime);
        }

        [Fact]
        public void TimestampAheadOfTheClock_IsClampedToThePostJumpUT()
        {
            var outcome = ConverterJumpPolicy.ComputeShiftedTimestamp(
                oldTime: 1200.0, preJumpUT: 1000.0, postJumpUT: 2000.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(2000.0, newTime);
        }

        [Fact]
        public void NeverRanSentinel_IsLeftAlone()
        {
            var outcome = ConverterJumpPolicy.ComputeShiftedTimestamp(
                oldTime: 0.0, preJumpUT: 1000.0, postJumpUT: 2000.0, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NeverRan, outcome);
            Assert.Equal(0.0, newTime);
        }

        [Theory]
        [InlineData(1000.0, 1000.0)]
        [InlineData(1000.0, 900.0)]
        public void NoForwardJump_ChangesNothing(double pre, double post)
        {
            var outcome = ConverterJumpPolicy.ComputeShiftedTimestamp(
                oldTime: 500.0, preJumpUT: pre, postJumpUT: post, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NoJump, outcome);
            Assert.Equal(500.0, newTime);
        }

        [Fact]
        public void ModuleSnapshot_WritesTheShiftedValueBackInvariantly()
        {
            var values = new ConfigNode("MODULE");
            values.AddValue("name", "ModuleResourceHarvester");
            values.AddValue("lastUpdateTime", "400.25");

            var outcome = ConverterJumpPolicy.ShiftModuleSnapshot(
                values, 1000.0, 51000.0, out double oldTime, out double newTime);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.Shifted, outcome);
            Assert.Equal(400.25, oldTime);
            Assert.Equal(50400.25, newTime);
            Assert.Equal("50400.25", values.GetValue("lastUpdateTime"));
        }

        [Fact]
        public void ModuleSnapshot_WithoutATimestamp_IsReportedNotInvented()
        {
            var values = new ConfigNode("MODULE");
            values.AddValue("name", "ModuleResourceConverter");

            var outcome = ConverterJumpPolicy.ShiftModuleSnapshot(
                values, 1000.0, 2000.0, out _, out _);

            Assert.Equal(ConverterJumpPolicy.ShiftOutcome.NoValue, outcome);
            Assert.False(values.HasValue("lastUpdateTime"));
        }

        [Fact]
        public void UnloadedVessel_ShiftsOnlyConverterModules()
        {
            var drill = new ConfigNode("MODULE");
            drill.AddValue("lastUpdateTime", "900");
            var lab = new ConfigNode("MODULE");
            lab.AddValue("lastUpdateTime", "950");
            // A non-converter that happens to persist a field of the same name must be untouched.
            var other = new ConfigNode("MODULE");
            other.AddValue("lastUpdateTime", "800");
            var neverRan = new ConfigNode("MODULE");
            neverRan.AddValue("lastUpdateTime", "0");

            var modules = new List<KeyValuePair<string, ConfigNode>>
            {
                new KeyValuePair<string, ConfigNode>("ModuleResourceHarvester", drill),
                new KeyValuePair<string, ConfigNode>("ModuleScienceConverter", lab),
                new KeyValuePair<string, ConfigNode>("SomeModdedTimer", other),
                new KeyValuePair<string, ConfigNode>("ModuleResourceConverter", neverRan),
            };
            var converters = new HashSet<string>
            {
                "ModuleResourceHarvester", "ModuleScienceConverter", "ModuleResourceConverter"
            };
            var tally = new ConverterJumpPolicy.JumpTally();

            ConverterJumpPolicy.ShiftUnloadedVesselModules(
                modules, converters.Contains, 1000.0, 11000.0, ref tally);

            Assert.Equal("10900", drill.GetValue("lastUpdateTime"));
            Assert.Equal("10950", lab.GetValue("lastUpdateTime"));
            Assert.Equal("800", other.GetValue("lastUpdateTime"));
            Assert.Equal("0", neverRan.GetValue("lastUpdateTime"));
            Assert.Equal(2, tally.UnloadedShifted);
            Assert.Equal(0, tally.LoadedShifted);
            Assert.Equal(1, tally.NeverRan);
        }

        [Fact]
        public void BothJumpEntryPoints_ApplyThePolicy_BeforeAnyChainTipSpawns()
        {
            string raw = System.IO.File.ReadAllText(FindParsekSourceFile("TimeJumpManager.cs"))
                .Replace("\r\n", "\n");
            string code = SourceScanText.StripCSharpComments(raw);

            string epochShift = MethodBody(code, "internal static void ExecuteJump(");
            string forward = MethodBody(code, "internal static void ExecuteForwardJump(");

            int epochApply = epochShift.IndexOf("ConverterJumpPolicy.Apply(", StringComparison.Ordinal);
            int epochClock = epochShift.IndexOf("Planetarium.SetUniversalTime(", StringComparison.Ordinal);
            int epochSpawn = epochShift.IndexOf("SpawnCrossedChainTips(", StringComparison.Ordinal);
            Assert.True(epochApply > 0, "ExecuteJump (Real Spawn Control warp) must apply the converter policy");
            Assert.True(epochApply > epochClock, "the policy runs after the clock moves");
            Assert.True(epochApply < epochSpawn, "the policy runs before chain tips spawn");

            int fwdApply = forward.IndexOf("ConverterJumpPolicy.Apply(", StringComparison.Ordinal);
            int fwdClock = forward.IndexOf("Planetarium.SetUniversalTime(", StringComparison.Ordinal);
            Assert.True(fwdApply > fwdClock, "ExecuteForwardJump must apply the converter policy after the clock moves");
        }

        private static string MethodBody(string code, string signature)
        {
            int start = code.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(start >= 0, "missing " + signature);
            int open = code.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                else if (code[i] == '}' && --depth == 0)
                    return code.Substring(open, i - open + 1);
            }
            return code.Substring(open);
        }

        private static string FindParsekSourceFile(string fileName)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = System.IO.Path.Combine(dir, "Source", "Parsek", fileName);
                if (System.IO.File.Exists(candidate))
                    return candidate;
                var parent = System.IO.Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new System.IO.FileNotFoundException(fileName);
        }
    }
}
