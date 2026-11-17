using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Parsek.InGameTests.Spikes;
using Xunit;

namespace Parsek.Tests.Spikes
{
    // Spike branch only (spike/mp-r0-s9). Pure parts of spike S9: the line and CSV
    // formats, the poll diff, the debug scope stack, and the hook table resolved
    // against the reference Assembly-CSharp.
    public class SpikeS9FormatTests : IDisposable
    {
        private readonly CultureInfo savedCulture;

        public SpikeS9FormatTests()
        {
            savedCulture = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        }

        public void Dispose() => Thread.CurrentThread.CurrentCulture = savedCulture;

        private static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);

        [Fact]
        public void FormatLine_KindSeqFieldsThenDetail()
        {
            string line = SpikeS9Format.FormatLine("observed", 12,
                new[] { KV("tool", "max-tech"), KV("layer", "ui"), KV("ut", "1.5") }, "args=()");
            Assert.Equal("observed: seq=12 tool=max-tech layer=ui ut=1.5 detail=args=()", line);
        }

        [Fact]
        public void FormatLine_FieldValuesAreSingleTokens_DetailKeepsSpacesButNotNewlines()
        {
            string line = SpikeS9Format.FormatLine("event", 1,
                new[] { KV("entry", "a b"), KV("scope", null), KV("scene", "") }, "x y\r\nz\nw");
            Assert.Equal("event: seq=1 entry=a_b scope=- scene=- detail=x y z w", line);
        }

        [Fact]
        public void FormatArgs_InvariantNumbersQuotedStringsEnumNames()
        {
            string s = SpikeS9Format.FormatArgs(new object[]
            {
                1.5, 2.25f, "1000000", true, TransactionReasons.Cheating, null, 7, new object(),
            });
            Assert.Equal("(1.5, 2.25, \"1000000\", true, Cheating, null, 7, <Object>)", s);
            Assert.Equal("()", SpikeS9Format.FormatArgs(null));
            Assert.Equal("()", SpikeS9Format.FormatArgs(new object[0]));
        }

        [Fact]
        public void IsCheatingReason_OnlyTheCheatReason()
        {
            Assert.True(SpikeS9Format.IsCheatingReason(TransactionReasons.Cheating));
            Assert.False(SpikeS9Format.IsCheatingReason(TransactionReasons.None));
            Assert.False(SpikeS9Format.IsCheatingReason(TransactionReasons.ContractReward));
            Assert.False(SpikeS9Format.IsCheatingReason(TransactionReasons.Cheating | TransactionReasons.ContractReward));
        }

        [Theory]
        [InlineData("KSP.UI.Screens.DebugToolbar.Screens.Cheats.CheatsCareer", "OnMaxTechClick", "CheatsCareer.OnMaxTechClick")]
        [InlineData("ConsoleCommands", "OnBadassCommand", "ConsoleCommands.OnBadassCommand")]
        [InlineData("Outer+Inner", "M", "Inner.M")]
        [InlineData(null, null, "?.?")]
        public void EntryLabel_ShortTypeNameDotMethod(string type, string method, string expected)
        {
            Assert.Equal(expected, SpikeS9Format.EntryLabel(type, method));
        }

        [Fact]
        public void Diff_ReportsChangedAddedAndRemovedKeys_Sorted()
        {
            var a = new Dictionary<string, string> { ["b"] = "false", ["a"] = "1", ["gone"] = "x" };
            var b = new Dictionary<string, string> { ["b"] = "true", ["a"] = "1", ["new"] = "y" };
            var d = SpikeS9Format.Diff(a, b);
            Assert.Equal(new[] { "b", "gone", "new" }, d.Select(c => c.Key).ToArray());
            Assert.Equal("false", d[0].Old);
            Assert.Equal("true", d[0].New);
            Assert.Equal("absent", d[1].New);
            Assert.Equal("absent", d[2].Old);
            Assert.Equal("b:false->true,gone:x->absent,new:absent->y", SpikeS9Format.FormatChanges(d));
        }

        [Fact]
        public void Diff_EqualOrEmpty_NoChanges()
        {
            var a = new Dictionary<string, string> { ["k"] = "1" };
            Assert.Empty(SpikeS9Format.Diff(a, new Dictionary<string, string> { ["k"] = "1" }));
            Assert.Empty(SpikeS9Format.Diff(null, null));
            Assert.Equal("none", SpikeS9Format.FormatChanges(new List<SpikeS9Format.PollChange>()));
        }

        [Fact]
        public void FormatSnapshot_SortedTokens()
        {
            var s = new Dictionary<string, string> { ["z"] = "1", ["a"] = "x y" };
            Assert.Equal("a=x_y z=1", SpikeS9Format.FormatSnapshot(s));
            Assert.Equal("", SpikeS9Format.FormatSnapshot(null));
        }

        [Fact]
        public void ShouldRebaseline_OnGameOrSceneChangeOnly()
        {
            object g1 = new object(), g2 = new object();
            Assert.False(SpikeS9Format.ShouldRebaseline(g1, g1, "FLIGHT", "FLIGHT"));
            Assert.True(SpikeS9Format.ShouldRebaseline(g1, g2, "FLIGHT", "FLIGHT"));
            Assert.True(SpikeS9Format.ShouldRebaseline(g1, g1, "SPACECENTER", "FLIGHT"));
            Assert.True(SpikeS9Format.ShouldRebaseline(null, g1, "MAINMENU", "MAINMENU"));
            Assert.False(SpikeS9Format.ShouldRebaseline(null, null, "MAINMENU", "MAINMENU"));
        }

        [Fact]
        public void Scope_RootIsOutermostTool_PopOnlyOwnTop()
        {
            var s = new SpikeS9Scope();
            object ui = "ui", effect = "effect", other = "other";
            Assert.Equal("none", s.Root);
            Assert.Equal(1, s.Push(ui, "max-progress"));
            Assert.Equal(2, s.Push(effect, "CheatProgression"));
            Assert.Equal("max-progress", s.Root);
            Assert.False(s.PopIfTop(other));   // a call that never pushed pops nothing
            Assert.False(s.PopIfTop(ui));      // not the top
            Assert.True(s.PopIfTop(effect));
            Assert.True(s.PopIfTop(ui));
            Assert.Equal(0, s.Depth);
            Assert.Equal("none", s.Root);
            Assert.False(s.PopIfTop(ui));
        }

        [Fact]
        public void CsvHeader_HasNoDuplicates()
        {
            Assert.Equal(SpikeS9Format.CsvHeader.Length, SpikeS9Format.CsvHeader.Distinct().Count());
        }
    }

    public class SpikeS9HooksTests
    {
        [Fact]
        public void EveryHook_ResolvesToADeclaredMethodOfTheReferenceAssembly()
        {
            var missing = new List<string>();
            foreach (SpikeS9Hook h in SpikeS9Hooks.All())
            {
                MethodInfo m = null;
                try
                {
                    m = h.Args == null
                        ? AccessTools.DeclaredMethod(h.Type, h.Method)
                        : AccessTools.DeclaredMethod(h.Type, h.Method, h.Args);
                }
                catch (Exception ex)
                {
                    missing.Add(h.Entry + " (" + ex.GetType().Name + ")");
                    continue;
                }
                if (m == null || m.DeclaringType != h.Type) missing.Add(h.Entry);
            }
            Assert.Empty(missing);
        }

        [Fact]
        public void Tools_AreUnique_LayersKnown()
        {
            var hooks = SpikeS9Hooks.All();
            Assert.Equal(hooks.Count, hooks.Select(h => h.Tool).Distinct().Count());
            var layers = new[]
            {
                SpikeS9Hooks.LayerUi, SpikeS9Hooks.LayerToggle, SpikeS9Hooks.LayerConsole,
                SpikeS9Hooks.LayerLegacy, SpikeS9Hooks.LayerEffect, SpikeS9Hooks.LayerCurrency,
            };
            Assert.All(hooks, h => Assert.Contains(h.Layer, layers));
            Assert.Equal(85, hooks.Count);
        }

        [Fact]
        public void CurrencyHooks_CarryAReasonArgument_OthersDoNot()
        {
            foreach (SpikeS9Hook h in SpikeS9Hooks.All())
            {
                if (h.CheatReasonOnly) Assert.True(SpikeS9Hooks.ReasonArgIndex(h) >= 0, h.Entry);
                else Assert.Equal(-1, SpikeS9Hooks.ReasonArgIndex(h));
            }
        }

        [Fact]
        public void Registry_HasExactlyOneS9LineWithPatches()
        {
            var s9 = SpikeRegistry.All.Where(r => string.Equals(r.Id, "S9", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(s9);
            Assert.NotNull(s9[0].Apply);
        }

        [Fact]
        public void NoS9Type_CarriesHarmonyPatchAttribute()
        {
            var types = new[] { typeof(SpikeS9Cheats), typeof(SpikeS9Hooks), typeof(SpikeS9Format), typeof(SpikeS9Scope), typeof(SpikeS9Hook) };
            Assert.All(types, t => Assert.Empty(t.GetCustomAttributes(typeof(HarmonyPatch), false)));
        }
    }
}
