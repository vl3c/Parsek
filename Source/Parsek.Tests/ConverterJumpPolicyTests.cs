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
            ParsekLog.ResetTestOverrides();
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
