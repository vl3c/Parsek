using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Parsek.InGameTests.Spikes;
using Parsek.InGameTests.Spikes.S6;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // R0 spike branch only (spike/mp-r0, never merged). Plan: r0-spikes.md, task S6.

    public class SpikeS6CfgTests
    {
        internal const string MarkHash = "33f1d41c85676b3f"; // also pinned in spikes/r0/S6/s6cfg.py

        private const string MarkText =
            "\tSCENARIO\n\t{\n\t\tname = ParsekRaceSpikeMark\n\t\tscene = 5, 7, 8\n\t\trace\n\t\t{\n" +
            "\t\t\tid = spike-1\n\t\t\tslot = 2\n\t\t}\n\t}\n";

        private static string Sfs(string inner) =>
            "GAME\n{\n\tversion = 1.12.5\n\tSCENARIO\n\t{\n\t\tname = ResearchAndDevelopment\n\t\tscene = 7, 8, 5\n" +
            "\t\tTech\n\t\t{\n\t\t\tid = start\n\t\t}\n\t}\n" + inner + "\tFLIGHTSTATE\n\t{\n\t\tUT = 1\n\t}\n}\n";

        private static string RepoRoot() =>
            Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));

        [Fact]
        public void Mark_CanonicalAndHash_ArePinned()
        {
            var facts = SpikeS6Cfg.ReadMarkFacts(Sfs(MarkText), SpikeS6.MarkName, SpikeS6.ProbeName);
            Assert.True(facts.IsGameSave);
            Assert.Equal(1, facts.MarkCount);
            Assert.Equal("SCENARIO {\nname = ParsekRaceSpikeMark\nscene = 5,7,8\nrace {\nid = spike-1\nslot = 2\n}\n}", facts.Canonical);
            Assert.Equal(MarkHash, facts.Hash);
            Assert.Equal(2, facts.ScenarioCount);
            Assert.Equal(0, facts.ProbeCount);
        }

        [Fact]
        public void Mark_TemplateFile_HashesToThePin()
        {
            string path = Path.Combine(RepoRoot(), "spikes", "r0", "S6", "template", "mark.cfg");
            Assert.True(File.Exists(path), path);
            var facts = SpikeS6Cfg.ReadMarkFacts(Sfs(File.ReadAllText(path)), SpikeS6.MarkName, null);
            Assert.Equal(1, facts.MarkCount);
            Assert.Equal(MarkHash, facts.Hash);
        }

        [Fact]
        public void Mark_BuiltByTheCreateStep_EqualsTheTemplateMark()
        {
            ConfigNode node = SpikeS6.BuildMarkNode();
            var cfg = ToCfg(node);
            Assert.Equal(MarkHash, SpikeS6Cfg.ShortHash(SpikeS6Cfg.Canonical(cfg)));
        }

        private static SpikeS6CfgNode ToCfg(ConfigNode n)
        {
            var c = new SpikeS6CfgNode(n.name);
            foreach (ConfigNode.Value v in n.values) c.Values.Add(new KeyValuePair<string, string>(v.name, v.value));
            foreach (ConfigNode child in n.nodes) c.Nodes.Add(ToCfg(child));
            return c;
        }

        [Fact]
        public void Mark_WhitespaceVariants_HashTheSame()
        {
            string variant = MarkText.Replace("5, 7, 8", "5,7,8").Replace("\t\t\tslot = 2", "  slot\t=   2 ");
            Assert.Equal(MarkHash, SpikeS6Cfg.ReadMarkFacts(Sfs(variant), SpikeS6.MarkName, null).Hash);
            string crlf = Sfs(MarkText).Replace("\n", "\r\n");
            Assert.Equal(MarkHash, SpikeS6Cfg.ReadMarkFacts(crlf, SpikeS6.MarkName, null).Hash);
        }

        [Fact]
        public void Mark_ChangedValue_ChangesTheHash()
        {
            var facts = SpikeS6Cfg.ReadMarkFacts(Sfs(MarkText.Replace("slot = 2", "slot = 3")), SpikeS6.MarkName, null);
            Assert.Equal(1, facts.MarkCount);
            Assert.NotEqual(MarkHash, facts.Hash);
        }

        [Fact]
        public void Mark_MissingDuplicateAndNonGame_AreCounted()
        {
            var none = SpikeS6Cfg.ReadMarkFacts(Sfs(""), SpikeS6.MarkName, null);
            Assert.Equal(0, none.MarkCount);
            Assert.Equal("-", none.Hash);
            Assert.Equal(2, SpikeS6Cfg.ReadMarkFacts(Sfs(MarkText + MarkText), SpikeS6.MarkName, null).MarkCount);
            var bare = SpikeS6Cfg.ReadMarkFacts("PARSEK\n{\n\tx = 1\n}\n", SpikeS6.MarkName, null);
            Assert.False(bare.IsGameSave);
            Assert.Equal(0, bare.MarkCount);
        }

        [Fact]
        public void Mark_NestedScenarioName_IsNotAMark()
        {
            // Only GAME's direct SCENARIO children count; a node named the same deeper down does not.
            string nested = Sfs("\tFOO\n\t{\n" + MarkText + "\t}\n");
            Assert.Equal(0, SpikeS6Cfg.ReadMarkFacts(nested, SpikeS6.MarkName, null).MarkCount);
        }

        [Fact]
        public void Parse_OneLineNodes_CommentsAndStrayBraces()
        {
            var root = SpikeS6Cfg.Parse("A { b = 1 } // c = 2\n}\nC\n{\nd = x = y\n");
            Assert.Equal(2, root.Nodes.Count);
            Assert.Equal("1", root.Nodes[0].GetValue("b"));
            Assert.Null(root.Nodes[0].GetValue("c"));
            Assert.Equal("x = y", root.Nodes[1].GetValue("d"));
        }

        [Fact]
        public void Probe_IsCounted()
        {
            string probe = "\tSCENARIO\n\t{\n\t\tname = SpikeS6ProbeScenario\n\t\tscene = 5, 6, 7, 8\n\t}\n";
            var facts = SpikeS6Cfg.ReadMarkFacts(Sfs(MarkText + probe), SpikeS6.MarkName, SpikeS6.ProbeName);
            Assert.Equal(1, facts.ProbeCount);
            Assert.Equal(3, facts.ScenarioCount);
        }

        [Theory]
        [InlineData("  5 ,  7,8 ", "5,7,8")]
        [InlineData("a\t\tb", "a b")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void NormalizeValue_Collapses(string input, string expected)
        {
            Assert.Equal(expected, SpikeS6Cfg.NormalizeValue(input));
        }
    }

    public class SpikeS6DifficultyTests
    {
        private sealed class FakeDifficulty
        {
            public bool AutoHireCrews = true;
            public bool AllowOtherLaunchSites = true;
            public float ResourceAbundance = 1f;
            public double RespawnTimer = 7200.0;
        }

        private sealed class FakeCareer
        {
            public float FundsGainMultiplier = 1f;
            public float StartingFunds = 25000f;
        }

        private sealed class FakeAdvanced
        {
            public bool GKerbalLimits;
            private bool exp = true;
            public bool EnableKerbalExperience { get { return exp; } set { exp = value; } }
        }

        private sealed class FakeCommNet
        {
            public bool plasmaBlackout;
            public float rangeModifier = 1f;
        }

        [Fact]
        public void Keys_AreFortyInFourParts()
        {
            Assert.Equal(40, SpikeS6Difficulty.Keys.Length);
            Assert.Equal(40, SpikeS6Difficulty.Keys.Select(k => k.Name).Distinct().Count());
            Assert.Equal(11, SpikeS6Difficulty.Keys.Count(k => k.Part == "Difficulty"));
            Assert.Equal(9, SpikeS6Difficulty.Keys.Count(k => k.Part == "Career"));
            Assert.Equal(13, SpikeS6Difficulty.Keys.Count(k => k.Part == "Advanced"));
            Assert.Equal(7, SpikeS6Difficulty.Keys.Count(k => k.Part == "CommNet"));
        }

        [Fact]
        public void Keys_EachNamesAStockMember()
        {
            // Against the KSP 1.12.5 reference assembly: every key is a field or a readable and
            // writable property of its stock type.
            var types = new Dictionary<string, Type>
            {
                { "Difficulty", typeof(GameParameters.DifficultyParams) },
                { "Career", typeof(GameParameters.CareerParams) },
                { "Advanced", typeof(GameParameters.AdvancedParams) },
                { "CommNet", typeof(CommNet.CommNetParams) },
            };
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public;
            foreach (var key in SpikeS6Difficulty.Keys)
            {
                Type t = types[key.Part];
                bool field = t.GetField(key.Member, f) != null;
                PropertyInfo p = t.GetProperty(key.Member, f);
                Assert.True(field || (p != null && p.CanRead && p.CanWrite), key.Name + " is not a public member of " + t.FullName);
            }
        }

        [Fact]
        public void PlannedEdits_OnePerPart()
        {
            Assert.Equal(new[] { "Difficulty", "Career", "Advanced", "CommNet" },
                SpikeS6Difficulty.PlannedEdits.Select(e => e.Key.Split('.')[0]).ToArray());
            foreach (var e in SpikeS6Difficulty.PlannedEdits)
                Assert.Contains(SpikeS6Difficulty.Keys, k => k.Name == e.Key);
        }

        [Fact]
        public void Read_FieldsPropertiesMissingAndNoPart()
        {
            var read = SpikeS6Difficulty.Read(new FakeDifficulty(), new FakeCareer(), new FakeAdvanced(), null);
            var map = read.ToDictionary(kv => kv.Key, kv => kv.Value);
            Assert.Equal(40, read.Count);
            Assert.Equal("True", map["Difficulty.AutoHireCrews"]);
            Assert.Equal("7200", map["Difficulty.RespawnTimer"]);
            Assert.Equal("25000", map["Career.StartingFunds"]);
            Assert.Equal("True", map["Advanced.EnableKerbalExperience"]);
            Assert.Equal("<missing>", map["Advanced.ImmediateLevelUp"]);
            Assert.Equal("<no-part>", map["CommNet.plasmaBlackout"]);
        }

        [Fact]
        public void TrySet_AppliesThePlannedEdits_AndCheckEditsPasses()
        {
            var d = new FakeDifficulty();
            var c = new FakeCareer();
            var a = new FakeAdvanced();
            var n = new FakeCommNet();
            var before = SpikeS6Difficulty.Read(d, c, a, n);
            foreach (var e in SpikeS6Difficulty.PlannedEdits)
            {
                string reason;
                Assert.True(SpikeS6Difficulty.TrySet(d, c, a, n, e.Key, e.Value, out reason), e.Key + " " + reason);
            }
            Assert.False(d.AllowOtherLaunchSites);
            Assert.Equal(0.8f, c.FundsGainMultiplier);
            Assert.True(a.GKerbalLimits);
            Assert.True(n.plasmaBlackout);
            var after = SpikeS6Difficulty.Read(d, c, a, n);
            Assert.Equal(4, SpikeS6Difficulty.Diff(before, after).Count);
            Assert.Empty(SpikeS6Difficulty.CheckEdits(before, after, SpikeS6Difficulty.PlannedEdits));
        }

        [Fact]
        public void CheckEdits_FlagsAMovedKeyAMissedEditAndANoOpDefault()
        {
            var d = new FakeDifficulty();
            var c = new FakeCareer();
            var a = new FakeAdvanced();
            var n = new FakeCommNet();
            var before = SpikeS6Difficulty.Read(d, c, a, n);
            d.AllowOtherLaunchSites = false;
            c.FundsGainMultiplier = 0.8f;
            a.GKerbalLimits = true;
            // plasmaBlackout left false: missed edit
            n.rangeModifier = 2f; // a key that must not move
            var after = SpikeS6Difficulty.Read(d, c, a, n);
            var problems = SpikeS6Difficulty.CheckEdits(before, after, SpikeS6Difficulty.PlannedEdits);
            Assert.Contains(problems, p => p.StartsWith("CommNet.plasmaBlackout is False"));
            Assert.Contains(problems, p => p.StartsWith("CommNet.rangeModifier moved 1 -> 2"));
            Assert.Equal(2, problems.Count);

            var noop = SpikeS6Difficulty.CheckEdits(after, after, SpikeS6Difficulty.PlannedEdits);
            Assert.Contains(noop, p => p.Contains("default already"));
        }

        [Fact]
        public void TrySet_Refusals()
        {
            string reason;
            Assert.False(SpikeS6Difficulty.TrySet(null, null, null, null, "nodot", "1", out reason));
            Assert.Equal("bad-key", reason);
            Assert.False(SpikeS6Difficulty.TrySet(new FakeDifficulty(), null, null, null, "Career.StartingFunds", "1", out reason));
            Assert.Equal("no-part", reason);
            Assert.False(SpikeS6Difficulty.TrySet(new FakeDifficulty(), null, null, null, "Difficulty.Nope", "1", out reason));
            Assert.Equal("no-member", reason);
            Assert.False(SpikeS6Difficulty.TrySet(new FakeDifficulty(), null, null, null, "Difficulty.AutoHireCrews", "maybe", out reason));
            Assert.Equal("parse", reason);
        }

        [Theory]
        [InlineData("0.8", "0.800000011920929", true)]
        [InlineData("True", "true", true)]
        [InlineData("1", "2", false)]
        [InlineData("True", "False", false)]
        public void ValuesEqual_TextOrNumber(string a, string b, bool equal)
        {
            Assert.Equal(equal, SpikeS6Difficulty.ValuesEqual(a, b));
        }

        [Fact]
        public void FormatValue_IsInvariant()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.Equal("0.8", SpikeS6Difficulty.FormatValue(0.8f));
                Assert.Equal("1.5", SpikeS6Difficulty.FormatValue(1.5));
                Assert.Equal("<null>", SpikeS6Difficulty.FormatValue(null));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }

    public class SpikeS6OptionsTests
    {
        private static Func<string, string> Env(params string[] kv)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return k => d.TryGetValue(k, out var v) ? v : null;
        }

        [Fact]
        public void Parse_Defaults()
        {
            var o = SpikeS6Options.Parse(Env());
            Assert.False(o.Auto);
            Assert.Equal(SpikeS6DiffChoice.Edit, o.Diff);
            Assert.False(o.Content);
            Assert.Equal("run", o.Label);
            Assert.Equal("spike-s6-created", o.Save);
            Assert.Equal(200, o.Folder.Length);
            Assert.Equal("CAREER", o.Mode);
            Assert.True(o.Resume);
            Assert.False(o.Quit);
            Assert.True(o.Snapshots);
            Assert.Empty(o.Notes);
        }

        [Fact]
        public void Parse_AllSwitches()
        {
            var o = SpikeS6Options.Parse(Env(
                "PARSEK_SPIKE_S6_AUTO", "1", "PARSEK_SPIKE_S6_DIFF", "Cancel", "PARSEK_SPIKE_S6_CONTENT", "yes",
                "PARSEK_SPIKE_S6_LABEL", "restock plus!", "PARSEK_SPIKE_S6_SAVE", "../evil", "PARSEK_SPIKE_S6_FOLDER", "D:\\x",
                "PARSEK_SPIKE_S6_MODE", "science", "PARSEK_SPIKE_S6_RESUME", "0", "PARSEK_SPIKE_S6_QUIT", "true",
                "PARSEK_SPIKE_S6_SNAPSHOTS", "off"));
            Assert.True(o.Auto);
            Assert.Equal(SpikeS6DiffChoice.Cancel, o.Diff);
            Assert.True(o.Content);
            Assert.Equal("restockplus", o.Label);
            Assert.Equal("evil", o.Save);
            Assert.Equal("D:\\x", o.Folder);
            Assert.Equal("SCIENCE_SANDBOX", o.Mode);
            Assert.False(o.Resume);
            Assert.True(o.Quit);
            Assert.False(o.Snapshots);
        }

        [Fact]
        public void Parse_UnknownValues_AreNoted()
        {
            var o = SpikeS6Options.Parse(Env("PARSEK_SPIKE_S6_DIFF", "maybe", "PARSEK_SPIKE_S6_MODE", "mission"));
            Assert.Equal(SpikeS6DiffChoice.Edit, o.Diff);
            Assert.Equal("CAREER", o.Mode);
            Assert.Equal(2, o.Notes.Count);
        }

        [Theory]
        [InlineData(200)]
        [InlineData(37)]
        [InlineData(9)]
        public void DefaultFolder_ExactLength(int length)
        {
            string f = SpikeS6Options.DefaultFolder(length);
            Assert.Equal(length, f.Length);
            Assert.StartsWith("C:\\Races", f);
            Assert.NotEqual('\\', f[f.Length - 1]);
        }

        [Fact]
        public void PickSaveName_AvoidsExistingFolders()
        {
            Assert.Equal("race", SpikeS6Options.PickSaveName("race", new[] { "other" }));
            Assert.Equal("race-2", SpikeS6Options.PickSaveName("race", new[] { "RACE" }));
            Assert.Equal("race-3", SpikeS6Options.PickSaveName("race", new[] { "race", "race-2" }));
            Assert.Equal("spike-s6-created", SpikeS6Options.PickSaveName("///", null));
        }

        [Fact]
        public void NextNav_AcceptsDownsAndGivesUp()
        {
            Assert.Equal(SpikeS6NavAction.Accept, SpikeS6Options.NextNav(true, 0, 12));
            Assert.Equal(SpikeS6NavAction.Down, SpikeS6Options.NextNav(false, 3, 12));
            Assert.Equal(SpikeS6NavAction.GiveUp, SpikeS6Options.NextNav(false, 12, 12));
        }
    }

    public class SpikeS6ContentTests
    {
        [Fact]
        public void TechLine_SortsParentsAndNormalizesCost()
        {
            Assert.Equal("survivability|15|True|basicScience,engineering101",
                SpikeS6Content.TechLine(" survivability ", "15.0", true, new[] { "engineering101", "basicScience", "" }));
            Assert.Equal("start|0|False|", SpikeS6Content.TechLine("start", "0", false, null));
        }

        [Fact]
        public void TechLinesFromCfg_ReadsTheStockShape()
        {
            string cfg = "TechTree\n{\n\tRDNode\n\t{\n\t\tid = start\n\t\tcost = 0\n\t}\n\tRDNode\n\t{\n" +
                "\t\tid = basicRocketry\n\t\tcost = 5\n\t\tanyToUnlock = False\n\t\tParent\n\t\t{\n" +
                "\t\t\tparentID = start\n\t\t\tlineFrom = RIGHT\n\t\t}\n\t}\n}\n";
            var lines = SpikeS6Content.TechLinesFromCfg(SpikeS6Cfg.Parse(cfg));
            Assert.Equal(new[] { "basicRocketry|5|False|start", "start|0|False|" }, lines.ToArray());
        }

        [Fact]
        public void CompareTech_NamesEachDifference()
        {
            var a = new[] { "start|0|False|", "basicRocketry|5|False|start", "x|1|False|" };
            var b = new[] { "start|0|False|", "basicRocketry|77|False|start", "y|1|False|" };
            var problems = SpikeS6Content.CompareTech(a, b, "mainmenu", "rnd");
            Assert.Equal(3, problems.Count);
            Assert.Contains("differs mainmenu=basicRocketry|5|False|start rnd=basicRocketry|77|False|start", problems);
            Assert.Contains("only-mainmenu x|1|False|", problems);
            Assert.Contains("only-rnd y|1|False|", problems);
            Assert.Empty(SpikeS6Content.CompareTech(a, a, "a", "b"));
        }
    }

    public class SpikeS6RegistrationTests
    {
        [Fact]
        public void S6_IsRegisteredOnceWithPatches()
        {
            var regs = SpikeRegistry.All.Where(r => r.Id == "S6").ToList();
            Assert.Single(regs);
            Assert.NotNull(regs[0].Apply);
        }

        [Fact]
        public void S6_Tag()
        {
            Assert.Equal("Spike.S6", SpikeS6.Tag);
        }
    }
}
