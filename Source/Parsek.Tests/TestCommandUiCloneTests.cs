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
    /// Coverage wave 6 (D11 <c>clone</c> / <c>leg-trim</c>): the pure half of
    /// <c>UiAction op=clone</c> and the interval-checkbox body both the Missions tab and
    /// <c>op=select key=leg:</c> call (<see cref="MissionsWindowUI.ApplyIntervalInclusion"/>).
    ///
    /// <para>The interval body is the production witness the leg-trim lane gates on, so its
    /// log line is pinned verbatim here, and its non-cascading contract is pinned by
    /// excluding the launch interval and asserting the post-separation interval stays
    /// included.</para>
    /// </summary>
    [Collection("Sequential")]
    public class TestCommandUiCloneTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TestCommandUiCloneTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        // ================================================================ op=clone

        [Fact]
        public void CloneOp_ParsesAndEchoesItsToken()
        {
            Assert.True(TestCommandUiAction.TryParseOp("clone", out UiActionOp op,
                                                       out string reason));
            Assert.Null(reason);
            Assert.Equal(UiActionOp.Clone, op);
            Assert.Equal("clone", TestCommandUiAction.OpToken(UiActionOp.Clone));
        }

        /// <summary>Two-phase for the <c>select</c> reason (it changes drawn mission state),
        /// and exempt from the host-visibility gate and the window-open gate for the same
        /// reason <c>select</c> is: it writes MISSION state, which no window owns.</summary>
        [Fact]
        public void CloneOp_IsTwoPhase_NeedsAWindow_AndIsNotAPictureOp()
        {
            Assert.True(TestCommandUiAction.OpIsTwoPhase(UiActionOp.Clone));
            Assert.True(TestCommandUiAction.OpNeedsWindow(UiActionOp.Clone));
            Assert.False(TestCommandUiAction.SettleChecksHostShowUi(UiActionOp.Clone));
            Assert.False(TestCommandUiAction.OpRequiresWindowOpen(UiActionOp.Clone));
        }

        [Theory]
        [InlineData("missions", true)]
        [InlineData("main", false)]
        [InlineData("timeline", false)]
        [InlineData("logistics", false)]
        [InlineData("structure", false)]
        [InlineData(null, false)]
        public void CloneIsDefinedForTheMissionsWindowOnly(string window, bool supported)
        {
            Assert.Equal(supported, TestCommandUiClone.WindowHasCloneAffordance(window));
        }

        [Fact]
        public void CloneableWindowNames_IsTheOneWindow()
        {
            Assert.Equal("missions", TestCommandUiClone.CloneableWindowNames);
        }

        [Fact]
        public void CloneSettled_RequiresTheCopy_ExactlyOneMore_AndTheSourceTree()
        {
            Assert.True(TestCommandUiClone.CloneSettledAsRequested(true, 2, 3, "t1", "t1"));
            // The copy is gone by the settle frame.
            Assert.False(TestCommandUiClone.CloneSettledAsRequested(false, 2, 3, "t1", "t1"));
            // A second writer moved the store in the same frame.
            Assert.False(TestCommandUiClone.CloneSettledAsRequested(true, 2, 4, "t1", "t1"));
            Assert.False(TestCommandUiClone.CloneSettledAsRequested(true, 2, 2, "t1", "t1"));
            // A copy of the wrong mission.
            Assert.False(TestCommandUiClone.CloneSettledAsRequested(true, 2, 3, "t1", "t2"));
        }

        [Fact]
        public void ClonePayload_CarriesTheNineFieldsInOrder()
        {
            List<KeyValuePair<string, string>> p = TestCommandUiClone.BuildClonePayload(
                "missions", "src", "cpy", "Kerbal X copy", 3, 1, 0, true);
            Assert.Equal(
                new[] { "op", "window", "mission", "copy", "name", "missions", "excluded",
                        "links", "loop" },
                p.Select(kv => kv.Key).ToArray());
            Assert.Equal(
                new[] { "clone", "missions", "src", "cpy", "Kerbal X copy", "3", "1", "0",
                        "true" },
                p.Select(kv => kv.Value).ToArray());
        }

        [Fact]
        public void ClonePayloadNumbers_AreInvariant()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                List<KeyValuePair<string, string>> p = TestCommandUiClone.BuildClonePayload(
                    "missions", "a", "b", "n", 12345, 1000, 2000, false);
                Assert.Equal("12345", p.First(kv => kv.Key == "missions").Value);
                Assert.Equal("1000", p.First(kv => kv.Key == "excluded").Value);
                Assert.Equal("false", p.First(kv => kv.Key == "loop").Value);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

        [Fact]
        public void ClonePayload_TreatsNullIdsAsEmptyRatherThanThrowing()
        {
            List<KeyValuePair<string, string>> p = TestCommandUiClone.BuildClonePayload(
                null, null, null, null, 0, 0, 0, false);
            Assert.Equal("", p.First(kv => kv.Key == "window").Value);
            Assert.Equal("", p.First(kv => kv.Key == "copy").Value);
            Assert.Equal("", p.First(kv => kv.Key == "name").Value);
        }

        // ============================================ the interval checkbox's body

        [Fact]
        public void ApplyIntervalInclusion_Exclude_WritesTheKeyStampsAndLogsTheProductionLine()
        {
            var m = new Mission("m1", "t1", "Kerbal X") { SelectionSchemaGeneration = 0 };
            Assert.True(MissionsWindowUI.ApplyIntervalInclusion(m, "rec1", false));
            Assert.Contains("rec1", m.ExcludedIntervalKeys);
            Assert.Equal(Mission.CurrentSelectionSchemaGeneration, m.SelectionSchemaGeneration);
            // The literal the leg-trim lane gates on: a C# bool, so `False`, not `false`.
            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("Mission 'Kerbal X' interval 'rec1' included=False"));
        }

        /// <summary>No cascade: excluding the launch interval leaves the post-separation
        /// interval of the same vessel included (the start-trim), and a child vessel's key is
        /// untouched.</summary>
        [Fact]
        public void ApplyIntervalInclusion_TouchesExactlyTheOneKey()
        {
            var m = new Mission("m1", "t1", "Kerbal X");
            MissionsWindowUI.ApplyIntervalInclusion(m, "rec1", false);
            Assert.Equal(new[] { "rec1" }, m.ExcludedIntervalKeys.ToArray());
            Assert.DoesNotContain("rec1/seg1", m.ExcludedIntervalKeys);
            Assert.DoesNotContain("probe1", m.ExcludedIntervalKeys);
        }

        [Fact]
        public void ApplyIntervalInclusion_Include_RemovesTheKeyAndLogsTrue()
        {
            var m = new Mission("m1", "t1", "Kerbal X");
            m.ExcludedIntervalKeys.Add("rec1/seg1");
            Assert.True(MissionsWindowUI.ApplyIntervalInclusion(m, "rec1/seg1", true));
            Assert.Empty(m.ExcludedIntervalKeys);
            Assert.Contains(logLines, l =>
                l.Contains("Mission 'Kerbal X' interval 'rec1/seg1' included=True"));
        }

        /// <summary>Idempotent: the seam takes an explicit direction, so a request matching
        /// the current state must write, stamp and log nothing.</summary>
        [Fact]
        public void ApplyIntervalInclusion_NoChange_WritesAndLogsNothing()
        {
            var m = new Mission("m1", "t1", "Kerbal X") { SelectionSchemaGeneration = 0 };
            Assert.False(MissionsWindowUI.ApplyIntervalInclusion(m, "rec1", true));
            Assert.Empty(m.ExcludedIntervalKeys);
            Assert.Equal(0, m.SelectionSchemaGeneration);
            Assert.DoesNotContain(logLines, l => l.Contains("interval 'rec1'"));

            m.ExcludedIntervalKeys.Add("rec1");
            logLines.Clear();
            Assert.False(MissionsWindowUI.ApplyIntervalInclusion(m, "rec1", false));
            Assert.Single(m.ExcludedIntervalKeys);
            Assert.DoesNotContain(logLines, l => l.Contains("interval 'rec1'"));
        }

        [Fact]
        public void ApplyIntervalInclusion_NullMissionOrKey_IsANoOp()
        {
            Assert.False(MissionsWindowUI.ApplyIntervalInclusion(null, "rec1", false));
            var m = new Mission("m1", "t1", "Kerbal X");
            Assert.False(MissionsWindowUI.ApplyIntervalInclusion(m, null, false));
            Assert.False(MissionsWindowUI.ApplyIntervalInclusion(m, "", false));
            Assert.Empty(m.ExcludedIntervalKeys);
        }

        /// <summary>A clone is a second include set over the same recordings: it CARRIES the
        /// source's trim at clone time, and a later trim of the source does not reach it.</summary>
        [Fact]
        public void MissionClone_CarriesTheTrim_ThenTheTwoSetsAreIndependent()
        {
            var src = new Mission("m1", "t1", "Kerbal X");
            MissionsWindowUI.ApplyIntervalInclusion(src, "rec1", false);
            Mission copy = src.Clone("m2");
            Assert.Equal("Kerbal X copy", copy.Name);
            Assert.Equal("t1", copy.TreeId);
            Assert.Contains("rec1", copy.ExcludedIntervalKeys);

            MissionsWindowUI.ApplyIntervalInclusion(src, "rec1/seg1", false);
            Assert.DoesNotContain("rec1/seg1", copy.ExcludedIntervalKeys);
        }
    }
}
