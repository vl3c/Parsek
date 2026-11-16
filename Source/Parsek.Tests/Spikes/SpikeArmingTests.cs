using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Parsek.InGameTests.Spikes;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // Spike branch only (spike/mp-r0). Pure parts of the S0 host: env parsing, the
    // armed line, the registry, and the CSV formatting and path rules.
    [Collection("Sequential")]
    public class SpikeArmingTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(",")]
        [InlineData(" , ,, ")]
        public void ParseList_UnsetOrSeparatorsOnly_IsEmpty(string raw)
        {
            Assert.Empty(SpikeArming.ParseList(raw));
        }

        [Fact]
        public void ParseList_TrimsDropsEmptiesAndDuplicates_KeepsFirstSpelling()
        {
            var ids = SpikeArming.ParseList(" S4a , S9,,s4A,S4a-turnback ,S9 ");
            Assert.Equal(new[] { "S4a", "S9", "S4a-turnback" }, ids.ToArray());
        }

        [Fact]
        public void IsArmed_IsCaseInsensitiveAndExact()
        {
            var ids = SpikeArming.ParseList("S4a,S0");
            Assert.True(SpikeArming.IsArmed(ids, "S4a"));
            Assert.True(SpikeArming.IsArmed(ids, "s4a"));
            Assert.True(SpikeArming.IsArmed(ids, " S0 "));
            // A prefix is not an arm: S4a must not arm S4a-turnback or S4.
            Assert.False(SpikeArming.IsArmed(ids, "S4a-turnback"));
            Assert.False(SpikeArming.IsArmed(ids, "S4"));
            Assert.False(SpikeArming.IsArmed(ids, null));
            Assert.False(SpikeArming.IsArmed(null, "S0"));
        }

        [Fact]
        public void ArmedMessage_CarriesTheListAndTheBuildMarker()
        {
            string msg = SpikeArming.FormatArmedMessage(SpikeArming.ParseList("S4,S5"));
            Assert.Equal("armed: S4,S5 build=PARSEK-SPIKE-R0", msg);
            Assert.Equal("PARSEK-SPIKE-R0", SpikeArming.BuildMarker);
            Assert.Equal("com.parsek.spike", SpikeArming.HarmonyId);
            Assert.Equal("PARSEK_SPIKE", SpikeArming.EnvVarName);
        }

        [Fact]
        public void ArmedLine_RendersWithTheSpikeTag()
        {
            var lines = new System.Collections.Generic.List<string>();
            ParsekLog.TestSinkForTesting = l => lines.Add(l);
            try
            {
                ParsekLog.Info(SpikeArming.Tag, SpikeArming.FormatArmedMessage(SpikeArming.ParseList("S0")));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
            Assert.Contains(lines, l => l.Contains("[Parsek][INFO][Spike] armed: S0 build=PARSEK-SPIKE-R0"));
        }

        [Fact]
        public void UnknownIds_NamesOnlyUnregisteredIds()
        {
            var unknown = SpikeArming.UnknownIds(SpikeArming.ParseList("S0,S99,s0"), new[] { "S0", "S4a" });
            Assert.Equal(new[] { "S99" }, unknown.ToArray());
        }

        [Fact]
        public void SpikeTag_IsSpikeDotId()
        {
            Assert.Equal("Spike.S4a", SpikeArming.SpikeTag("S4a"));
            Assert.Equal("Spike", SpikeArming.SpikeTag(null));
        }

        [Fact]
        public void Registry_HasS0AndUniqueIds()
        {
            var ids = SpikeRegistry.Ids().ToList();
            Assert.Contains("S0", ids);
            Assert.Equal(ids.Count, ids.Select(i => i.ToLowerInvariant()).Distinct().Count());
            Assert.All(ids, id => Assert.Null(SpikeCsv.ValidateSegment(id)));
        }

        // ParsekHarmony applies every [HarmonyPatch] type in the assembly in every
        // session, so a spike patch carrying the attribute would run unarmed.
        [Fact]
        public void NoSpikeType_CarriesHarmonyPatchAttribute()
        {
            var offenders = typeof(SpikeHost).Assembly.GetTypes()
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("Parsek.InGameTests.Spikes", StringComparison.Ordinal))
                .Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0)
                .Select(t => t.FullName)
                .ToList();
            Assert.Empty(offenders);
        }
    }

    public class SpikeCsvTests : IDisposable
    {
        private readonly string tempRoot;
        private readonly CultureInfo savedCulture;

        public SpikeCsvTests()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "parsek-spike-csv-" + Guid.NewGuid().ToString("N"));
            savedCulture = Thread.CurrentThread.CurrentCulture;
        }

        public void Dispose()
        {
            Thread.CurrentThread.CurrentCulture = savedCulture;
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (IOException) { }
        }

        [Fact]
        public void FormatRow_IsInvariantUnderACommaDecimalCulture()
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string row = SpikeCsv.FormatRow(1.5, 0.1f, 1234567.25m, 42, -3L, true, null, "x");
            Assert.Equal("1.5,0.1,1234567.25,42,-3,true,,x", row);
        }

        [Fact]
        public void FormatField_DoubleRoundTrips()
        {
            double v = 0.1 + 0.2;
            Assert.Equal(v, double.Parse(SpikeCsv.FormatField(v), CultureInfo.InvariantCulture));
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("a,b", "\"a,b\"")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        [InlineData("two\nlines", "\"two\nlines\"")]
        [InlineData("", "")]
        public void FormatField_QuotesOnlyWhenNeeded(string input, string expected)
        {
            Assert.Equal(expected, SpikeCsv.FormatField(input));
        }

        [Fact]
        public void FormatRow_EmptyOrNull_IsEmpty()
        {
            Assert.Equal("", SpikeCsv.FormatRow());
            Assert.Equal("", SpikeCsv.FormatRow(null));
        }

        [Fact]
        public void BuildPath_IsRootSpikeFolderSpikeIdName()
        {
            string p = SpikeCsv.BuildPath("ksp", "S4a", "ksc_state");
            Assert.Equal(Path.Combine("ksp", "parsek-spike", "S4a", "ksc_state.csv"), p);
            Assert.Equal(Path.Combine("ksp", "parsek-spike", "S9", "observed.csv"),
                SpikeCsv.BuildPath("ksp", "S9", "observed.csv"));
        }

        [Theory]
        [InlineData("..", "x")]
        [InlineData("S4/..", "x")]
        [InlineData("S4", "..\\x")]
        [InlineData("S4", "a/b")]
        [InlineData("S4", "c:x")]
        [InlineData("", "x")]
        [InlineData("S4", "")]
        [InlineData("S4", " x")]
        [InlineData("S4", "x?")]
        public void BuildPath_RefusesUnsafeSegments(string id, string name)
        {
            Assert.Throws<ArgumentException>(() => SpikeCsv.BuildPath("ksp", id, name));
        }

        [Fact]
        public void AppendRowAtPath_WritesHeaderOnceThenRows()
        {
            string path = SpikeCsv.BuildPath(tempRoot, "S0", "probe");
            SpikeCsv.AppendRowAtPath(path, new[] { "k", "v" }, "a", 1.25);
            SpikeCsv.AppendRowAtPath(path, new[] { "k", "v" }, "b", 2);
            Assert.Equal("k,v\na,1.25\nb,2\n", File.ReadAllText(path));
        }

        [Fact]
        public void Writer_AppendsToAnExistingFileWithoutASecondHeader()
        {
            string path = SpikeCsv.BuildPath(tempRoot, "S11", "frames");
            using (var w = SpikeCsvWriter.OpenAtPath(path, "frame", "ms"))
            {
                w.WriteRow(1, 0.5);
                w.WriteRow(2, 0.25);
                Assert.Equal(2, w.RowsWritten);
            }
            using (var w = SpikeCsvWriter.OpenAtPath(path, "frame", "ms"))
                w.WriteRow(3, 1.0);
            Assert.Equal("frame,ms\n1,0.5\n2,0.25\n3,1\n", File.ReadAllText(path));
        }
    }
}
