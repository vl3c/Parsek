using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>PartParitySweep</c> verb's decision half
    /// (<see cref="TestCommandPartParitySweep"/>): the arg grammar and its refusals, the
    /// stable case order, sharding and limit, the case ids (collisions, length, label
    /// validity), the resume rule, the time box and the OK payload.
    /// </summary>
    public class TestCommandPartParitySweepTests
    {
        private static Dictionary<string, string> Args(params string[] kv)
        {
            var d = new Dictionary<string, string> { { "label", "pp-1" } };
            for (int i = 0; i < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        private static PartParitySweepArgs Parse(params string[] kv)
        {
            Assert.True(TestCommandPartParitySweep.TryParseArgs(Args(kv), out var a, out string reason, out string detail),
                "rejected " + reason + " " + detail);
            return a;
        }

        // ----- args -----

        [Fact]
        public void LabelOnly_TakesEveryDefault()
        {
            var a = Parse();
            Assert.Equal("pp-1", a.Label);
            Assert.Equal(PartParitySweepOp.Run, a.Op);
            Assert.Equal("*", a.Filter);
            Assert.Null(a.Sources);
            Assert.Equal(PartParitySkinSelection.Default, a.Skins);
            Assert.Equal(TestCommandPartParitySweep.RequiredViews, a.Views);
            Assert.Equal(512, a.Tile);
            Assert.Equal(1, a.ShardK);
            Assert.Equal(1, a.ShardN);
            Assert.Equal(0, a.Limit);
            Assert.True(a.Controls);
            Assert.Equal(480, a.MaxSeconds);
        }

        [Fact]
        public void MissingAndInvalidLabel_HaveTheirOwnReasons()
        {
            Assert.False(TestCommandPartParitySweep.TryParseArgs(
                new Dictionary<string, string>(), out var a, out string reason, out _));
            Assert.Null(a);
            Assert.Equal("label-arg-missing", reason);
            Assert.False(TestCommandPartParitySweep.TryParseArgs(
                new Dictionary<string, string> { { "label", "../x" } }, out _, out reason, out string detail));
            Assert.Equal("label-arg-invalid", reason);
            Assert.Equal("label=../x", detail);
            Assert.False(TestCommandPartParitySweep.TryParseArgs(null, out _, out reason, out _));
            Assert.Equal("label-arg-missing", reason);
        }

        [Fact]
        public void EveryArgParsesItsFullForm()
        {
            var a = Parse("op", "reset", "filter", "mk1*", "source", "Squad,ReStock,squad", "skins", "all",
                "views", "top,side-a,top,hero", "tile", "1024", "shard", "3/4", "limit", "20",
                "controls", "false", "maxSeconds", "60");
            Assert.Equal(PartParitySweepOp.Reset, a.Op);
            Assert.Equal("mk1*", a.Filter);
            Assert.Equal(new[] { "Squad", "ReStock" }, a.Sources);
            Assert.Equal(PartParitySkinSelection.All, a.Skins);
            Assert.Equal(new[] { "side-a", "top", "hero" }, a.Views);
            Assert.Equal(1024, a.Tile);
            Assert.Equal(3, a.ShardK);
            Assert.Equal(4, a.ShardN);
            Assert.Equal(20, a.Limit);
            Assert.False(a.Controls);
            Assert.Equal(60, a.MaxSeconds);
        }

        [Fact]
        public void ViewsAll_AddsTheTwoRemainingSidesButNotHero()
        {
            Assert.Equal(new[] { "side-a", "side-b", "top", "bottom", "side-c", "side-d" }, Parse("views", "all").Views);
            Assert.Equal(TestCommandPartParitySweep.RequiredViews, Parse("views", "required").Views);
        }

        [Theory]
        [InlineData("op", "Run", "op-arg-invalid")]
        [InlineData("op", "", "op-arg-invalid")]
        [InlineData("filter", "", "filter-arg-invalid")]
        [InlineData("filter", "a/b", "filter-arg-invalid")]
        [InlineData("filter", "a b", "filter-arg-invalid")]
        [InlineData("source", "", "source-arg-invalid")]
        [InlineData("source", "Squad,,ReStock", "source-arg-invalid")]
        [InlineData("source", "Squad/x", "source-arg-invalid")]
        [InlineData("skins", "some", "skins-arg-invalid")]
        [InlineData("views", "", "views-arg-invalid")]
        [InlineData("views", "front", "views-arg-invalid")]
        [InlineData("views", "side-a,", "views-arg-invalid")]
        [InlineData("tile", "255", "tile-arg-invalid")]
        [InlineData("tile", "1025", "tile-arg-invalid")]
        [InlineData("tile", " 512", "tile-arg-invalid")]
        [InlineData("tile", "+512", "tile-arg-invalid")]
        [InlineData("tile", "512.0", "tile-arg-invalid")]
        [InlineData("shard", "0/1", "shard-arg-invalid")]
        [InlineData("shard", "2/1", "shard-arg-invalid")]
        [InlineData("shard", "1/0", "shard-arg-invalid")]
        [InlineData("shard", "1/65", "shard-arg-invalid")]
        [InlineData("shard", "1-2", "shard-arg-invalid")]
        [InlineData("shard", "1/2/3", "shard-arg-invalid")]
        [InlineData("shard", "/2", "shard-arg-invalid")]
        [InlineData("limit", "0", "limit-arg-invalid")]
        [InlineData("limit", "-1", "limit-arg-invalid")]
        [InlineData("limit", "100001", "limit-arg-invalid")]
        [InlineData("controls", "yes", "controls-arg-invalid")]
        [InlineData("controls", "True", "controls-arg-invalid")]
        [InlineData("maxSeconds", "59", "maxseconds-arg-invalid")]
        [InlineData("maxSeconds", "481", "maxseconds-arg-invalid")]
        public void BadArgs_AreRejectedNeverClamped(string key, string value, string expectedReason)
        {
            Assert.False(TestCommandPartParitySweep.TryParseArgs(Args(key, value), out var a, out string reason, out string detail));
            Assert.Null(a);
            Assert.Equal(expectedReason, reason);
            Assert.Equal(key + "=" + value, detail);
            Assert.Contains(reason, TestCommandPartParitySweep.Reasons);
        }

        [Fact]
        public void Reasons_AreDistinctAndLowerCase()
        {
            var reasons = TestCommandPartParitySweep.Reasons;
            Assert.Equal(reasons.Length, reasons.Distinct().Count());
            Assert.All(reasons, r => Assert.Equal(r.ToLowerInvariant(), r));
        }

        // ----- glob -----

        [Theory]
        [InlineData("*", "mk1pod.v2", true)]
        [InlineData("mk1*", "mk1pod.v2", true)]
        [InlineData("MK1*", "mk1pod.v2", true)]
        [InlineData("*pod*", "mk1pod.v2", true)]
        [InlineData("?k1pod.v2", "mk1pod.v2", true)]
        [InlineData("mk1pod", "mk1pod.v2", false)]
        [InlineData("*.v3", "mk1pod.v2", false)]
        [InlineData("a*b*c", "aXbYc", true)]
        [InlineData("a*b*c", "aXbY", false)]
        [InlineData("", "", true)]
        public void GlobMatch(string pattern, string text, bool expected)
        {
            Assert.Equal(expected, TestCommandPartParitySweep.GlobMatch(pattern, text));
        }

        // ----- order, selection, sharding -----

        private static PartParityCaseKey K(string part, string skin = "default", int ordinal = 0, string source = "Squad")
            => new PartParityCaseKey { Part = part, Skin = skin, SkinOrdinal = ordinal, Source = source };

        private static List<PartParityCaseKey> Install()
        {
            return new List<PartParityCaseKey>
            {
                K("mk1pod.v2", "White", 1),
                K("fuelTank", "default", 0, "ReStock"),
                K("mk1pod.v2", "default", 0),
                K("Decoupler.1", "default", 0),
                K("fuelTank", "Dark", 2, "ReStock"),
                K("fuelTank", "BlackAndWhite", 1, "ReStock"),
                K("liquidEngine", "default", 0),
            };
        }

        [Fact]
        public void SelectCases_OrdersStablyRegardlessOfInputOrder()
        {
            var args = Parse("skins", "all");
            var a = TestCommandPartParitySweep.SelectCases(Install(), args);
            var shuffled = Install();
            shuffled.Reverse();
            var b = TestCommandPartParitySweep.SelectCases(shuffled, args);
            Assert.Equal(a.Select(c => c.CaseId), b.Select(c => c.CaseId));
            Assert.Equal(
                new[] { "Decoupler.1/default", "fuelTank/default", "fuelTank/BlackAndWhite", "fuelTank/Dark",
                        "liquidEngine/default", "mk1pod.v2/default", "mk1pod.v2/White" },
                a.Select(c => c.Key.Part + "/" + c.Key.Skin));
            Assert.Equal(Enumerable.Range(0, 7), a.Select(c => c.Index));
        }

        [Fact]
        public void SkinsDefault_KeepsOnlyTheBaseSkin()
        {
            var cases = TestCommandPartParitySweep.SelectCases(Install(), Parse());
            Assert.Equal(4, cases.Count);
            Assert.All(cases, c => Assert.Equal(0, c.Key.SkinOrdinal));
        }

        [Fact]
        public void FilterAndSource_Narrow()
        {
            var byFilter = TestCommandPartParitySweep.SelectCases(Install(), Parse("filter", "*tank*", "skins", "all"));
            Assert.Equal(3, byFilter.Count);
            var bySource = TestCommandPartParitySweep.SelectCases(Install(), Parse("source", "restock", "skins", "all"));
            Assert.Equal(3, bySource.Count);
            Assert.All(bySource, c => Assert.Equal("ReStock", c.Key.Source));
            var none = TestCommandPartParitySweep.SelectCases(Install(), Parse("source", "Kopernicus"));
            Assert.Empty(none);
        }

        [Fact]
        public void Shards_PartitionTheSameNumbering()
        {
            var all = TestCommandPartParitySweep.SelectCases(Install(), Parse("skins", "all"));
            var seen = new List<int>();
            for (int k = 1; k <= 3; k++)
            {
                var shard = TestCommandPartParitySweep.SelectCases(Install(),
                    Parse("skins", "all", "shard", k.ToString(CultureInfo.InvariantCulture) + "/3"));
                Assert.All(shard, c => Assert.Equal(k - 1, c.Index % 3));
                seen.AddRange(shard.Select(c => c.Index));
            }
            seen.Sort();
            Assert.Equal(all.Select(c => c.Index), seen);
        }

        [Fact]
        public void Limit_AppliesAfterTheShard()
        {
            var cases = TestCommandPartParitySweep.SelectCases(Install(), Parse("skins", "all", "shard", "2/2", "limit", "2"));
            Assert.Equal(new[] { 1, 3 }, cases.Select(c => c.Index));
        }

        [Fact]
        public void SelectCases_ToleratesNulls()
        {
            Assert.Empty(TestCommandPartParitySweep.SelectCases(null, null));
            Assert.Single(TestCommandPartParitySweep.SelectCases(new[] { null, K("a") }, null));
        }

        // ----- case ids -----

        [Fact]
        public void Fnv1a32_KnownVectors()
        {
            Assert.Equal("811c9dc5", TestCommandPartParitySweep.Fnv1a32Hex(""));
            Assert.Equal("e40c292c", TestCommandPartParitySweep.Fnv1a32Hex("a"));
            Assert.Equal("bf9cf968", TestCommandPartParitySweep.Fnv1a32Hex("foobar"));
        }

        [Fact]
        public void CaseId_DefaultStateAndContextAreLeftOut()
        {
            var key = K("mk1pod.v2");
            string id = TestCommandPartParitySweep.CaseId(key);
            Assert.Equal("mk1pod.v2--default--" + TestCommandPartParitySweep.Fnv1a32Hex(TestCommandPartParitySweep.CaseTuple(key)), id);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id));
        }

        [Fact]
        public void CaseId_MapsAndLowerCases()
        {
            string id = TestCommandPartParitySweep.CaseId(K("Size1 Tank", "Black And White", 1));
            Assert.StartsWith("size1-tank--black-and-white--", id);
            Assert.Equal(id.ToLowerInvariant(), id);
        }

        [Fact]
        public void CaseId_StateAndContextKeepTheirPlaces()
        {
            var deployed = K("chute");
            deployed.State = "deployed";
            Assert.StartsWith("chute--default--deployed--", TestCommandPartParitySweep.CaseId(deployed));

            var onTank = K("chute");
            onTank.Context = "top-node";
            Assert.StartsWith("chute--default--stowed--top-node--", TestCommandPartParitySweep.CaseId(onTank));
        }

        [Fact]
        public void CaseIds_ThatMapAlike_StayDistinctOnACaseInsensitiveDisk()
        {
            string a = TestCommandPartParitySweep.CaseId(K("tank", "Dark"));
            string b = TestCommandPartParitySweep.CaseId(K("tank", "dark"));
            string c = TestCommandPartParitySweep.CaseId(K("tank", "da rk"));
            string d = TestCommandPartParitySweep.CaseId(K("tank", "da-rk"));
            var ids = new[] { a, b, c, d };
            Assert.Equal(4, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.StartsWith("tank--dark--", a);
            Assert.StartsWith("tank--dark--", b);
        }

        [Theory]
        [InlineData("_hidden")]
        [InlineData(".dot")]
        [InlineData("")]
        [InlineData("a/b\\c:d*e?f\"g<h>i|j")]
        public void CaseId_IsAlwaysAValidLabel(string part)
        {
            string id = TestCommandPartParitySweep.CaseId(K(part, "Sk_in."));
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id);
        }

        [Fact]
        public void CaseId_NonAsciiPartNameIsAValidLabel()
        {
            string part = ((char)0xE9).ToString() + "clair";
            string id = TestCommandPartParitySweep.CaseId(K(part));
            Assert.StartsWith("x-clair--default--", id);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id), id);
        }

        [Fact]
        public void CaseId_LongNamesAreCutToTheLabelLimitKeepingTheHash()
        {
            var key = K(new string('p', 80), new string('s', 80));
            string id = TestCommandPartParitySweep.CaseId(key);
            Assert.Equal(96, id.Length);
            Assert.True(TestCommandCaptureScreenshot.IsValidLabel(id));
            Assert.EndsWith("--" + TestCommandPartParitySweep.Fnv1a32Hex(TestCommandPartParitySweep.CaseTuple(key)), id);

            var other = K(new string('p', 80), new string('s', 79) + "t");
            Assert.NotEqual(id, TestCommandPartParitySweep.CaseId(other));
        }

        [Fact]
        public void FileNames()
        {
            Assert.Equal("x--default--00000000.part.json", TestCommandPartParitySweep.SidecarFileName("x--default--00000000"));
            Assert.Equal("x.png", TestCommandPartParitySweep.AtlasFileName("x"));
            Assert.Equal("x.thumb.png", TestCommandPartParitySweep.ThumbFileName("x"));
            Assert.Equal("parsek-partparity/pp-1", TestCommandPartParitySweep.RelativeLabelDir("pp-1"));
        }

        // ----- resume -----

        [Theory]
        [InlineData("case-1\n", "case-1")]
        [InlineData("case-1", "case-1")]
        [InlineData("  case-1  \r\nleftover", "case-1")]
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("   \n", null)]
        [InlineData("../escape\n", null)]
        public void ParseInflight(string text, string expected)
        {
            Assert.Equal(expected, TestCommandPartParitySweep.ParseInflight(text));
        }

        [Fact]
        public void FormatInflight_RoundTrips()
        {
            Assert.Equal("a--b--c", TestCommandPartParitySweep.ParseInflight(TestCommandPartParitySweep.FormatInflight("a--b--c")));
        }

        [Theory]
        [InlineData("a", false, null, "Run")]
        [InlineData("a", true, null, "SkipComplete")]
        [InlineData("a", false, "a", "RecordCrashed")]
        [InlineData("a", true, "a", "SkipComplete")]
        [InlineData("a", false, "b", "Run")]
        [InlineData("a", false, "A", "Run")]
        public void DecideCaseResume(string caseId, bool complete, string inflight, string expected)
        {
            Assert.Equal(expected, TestCommandPartParitySweep.DecideCaseResume(caseId, complete, inflight).ToString());
        }

        [Theory]
        [InlineData(0.0, 480, 0.0, true)]
        [InlineData(0.0, 60, 500.0, true)]     // the first wave of a step always starts
        [InlineData(400.0, 480, 60.0, true)]
        [InlineData(400.0, 480, 81.0, false)]
        [InlineData(480.0, 480, 0.0, true)]
        [InlineData(481.0, 480, 0.0, false)]
        public void ShouldStartWave(double elapsed, int max, double lastWave, bool expected)
        {
            Assert.Equal(expected, TestCommandPartParitySweep.ShouldStartWave(elapsed, max, lastWave));
        }

        [Fact]
        public void ControlsState()
        {
            int n = TestCommandPartParitySweep.ControlIds.Length;
            var allOk = Enumerable.Repeat("ok", n).ToList();
            Assert.Equal("ok", TestCommandPartParitySweep.ControlsState(true, allOk));
            Assert.Equal("pending", TestCommandPartParitySweep.ControlsState(true, allOk.Take(n - 1).ToList()));
            Assert.Equal("pending", TestCommandPartParitySweep.ControlsState(true, null));
            var oneBad = new List<string> { "ok", "bad" };
            Assert.Equal("bad", TestCommandPartParitySweep.ControlsState(true, oneBad));
            Assert.Equal("off", TestCommandPartParitySweep.ControlsState(false, allOk));
            Assert.Equal(8, n);
            Assert.Equal(n, TestCommandPartParitySweep.ControlIds.Distinct().Count());
        }

        [Fact]
        public void SetupFaults()
        {
            Assert.False(TestCommandPartParitySweep.IsSetupFault("ok"));
            Assert.False(TestCommandPartParitySweep.IsSetupFault("still-animating"));
            Assert.True(TestCommandPartParitySweep.IsSetupFault("spawn-timeout"));
            Assert.True(TestCommandPartParitySweep.IsSetupFault("crashed"));
            Assert.True(TestCommandPartParitySweep.IsSetupFault(null));
        }

        // ----- admission and payload -----

        [Fact]
        public void HostAdmission()
        {
            Assert.Null(TestCommandPartParitySweep.DecideHostAdmission(true, true));
            Assert.Equal("no-active-vessel", TestCommandPartParitySweep.DecideHostAdmission(false, true));
            Assert.Equal("host-not-orbiting", TestCommandPartParitySweep.DecideHostAdmission(true, false));
        }

        [Fact]
        public void Payload_KeysInOrderAndInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var payload = TestCommandPartParitySweep.BuildPayload("pp-1", false, 42, 900, 41, 1, 3, "ok", 471.26);
                Assert.Equal(
                    new[] { "label", "done", "next", "cases", "complete", "crashed", "setupFaults", "controls", "wall" },
                    payload.Select(p => p.Key));
                Assert.Equal("label=pp-1 done=false next=42 cases=900 complete=41 crashed=1 setupFaults=3 controls=ok wall=471.3",
                    TestCommandPartParitySweep.FormatPayload(payload));
                var nan = TestCommandPartParitySweep.BuildPayload("x", true, 0, 0, 0, 0, 0, null, double.NaN);
                Assert.Equal("0.0", nan.Last().Value);
                Assert.Equal("pending", nan[7].Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ArgKeys_CoverEveryKeyTheParserReads()
        {
            // A parse with every key set must not leave a key in ArgKeys unused: each one,
            // set to an invalid value alone, must be rejected by its own reason.
            foreach (string key in TestCommandPartParitySweep.ArgKeys)
            {
                var d = key == "label"
                    ? new Dictionary<string, string> { { "label", "bad/label" } }
                    : Args(key, "\u0001bad");
                Assert.False(TestCommandPartParitySweep.TryParseArgs(d, out _, out string reason, out _), key);
                Assert.StartsWith(key.ToLowerInvariant(), reason);
            }
        }
    }
}
