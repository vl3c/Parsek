using System;
using System.Collections.Generic;
using Xunit;
using C = Parsek.Tests.RecordingsTableClickHandlerCharacterizationTests;
using G = Parsek.Tests.RecordingsTableGroupTreeCharacterizationTests;
using WatchResult = Parsek.GhostPlaybackLogic.GroupWatchAdvanceResult;

namespace Parsek.Tests
{
    /// <summary>
    /// Direct tests of the decision helpers extracted from the Recordings tab's folder row,
    /// <c>RecordingsTableUI.DrawGroupTree</c>. Every sweep of
    /// <see cref="RecordingsTableGroupTreeCharacterizationTests"/> runs through the shipped
    /// helper and must print the golden the ORIGINAL block (its verbatim oracle) pinned, and
    /// the oracle's own print when both run on fresh stores.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableGroupTreeHelperTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingsTableGroupTreeHelperTests()
        {
            C.ResetAll();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            C.ResetAll();
        }

        private sealed class ShippedUnits : G.IUnits
        {
            public bool AllEnabled(HashSet<int> d, IReadOnlyList<Recording> c, int m)
                => RecordingsTableUI.AreAllGroupMembersPlaybackEnabled(d, c, m);
            public void ApplyEnabled(string g, HashSet<int> d, IReadOnlyList<Recording> c, bool e)
                => RecordingsTableUI.ApplyGroupPlaybackEnabledToggle(g, d, c, e);
            public string Arrow(bool expanded) => RecordingsTableUI.GroupExpandArrow(expanded);
            public bool RenameKeys(RecordingsTableUI ui, string g, bool s, bool x) => ui.HandleGroupRenameKeys(g, s, x);
            public string HeaderLabel(string tc, string a, string l, int m)
                => RecordingsTableUI.BuildGroupHeaderLabel(tc, a, l, m);
            public bool HeaderClick(RecordingsTableUI ui, string g, IReadOnlyList<Recording> c, bool e, int m, float t)
            {
                ui.HandleGroupHeaderClick(g, c, ref e, m, t);
                return e;
            }
            public int ManageCell(bool a, bool d) => (int)RecordingsTableUI.ResolveGroupManageCell(a, d);
            public IEnumerable<int> LoopScope(HashSet<int> d, List<int> a) => RecordingsTableUI.ResolveGroupLoopScope(d, a);
            public bool AnyWatched(int w, HashSet<int> d, IReadOnlyList<Recording> c)
                => RecordingsTableUI.IsAnyGroupDescendantWatched(w, d, c);
            public WatchResult Rotation(HashSet<int> d, IReadOnlyList<Recording> c, Func<int, bool> e, string cur,
                string w, out int n) => RecordingsTableUI.ResolveGroupWatchRotation(d, c, e, cur, w, out n);
            public bool CanWatch(WatchResult r, int n, bool a, out bool t)
                => RecordingsTableUI.ResolveGroupWatchPressModes(r, n, a, out t);
            public bool Transition(Dictionary<string, bool> a, Dictionary<string, string> b, string k, bool cw, string fp)
                => RecordingsTableUI.UpdateGroupWatchTransitionCache(a, b, k, cw, fp);
            public string WatchTooltip(WatchResult r, bool w, int n, string wid, IReadOnlyList<Recording> c)
                => RecordingsTableUI.BuildGroupWatchTooltip(r, w, n, wid, c);
            public string AdvancePressKind(string cur, bool wrap) => RecordingsTableUI.GroupWatchAdvancePressKind(cur, wrap);
            public bool AllHidden(HashSet<int> d, IReadOnlyList<Recording> c, int m)
                => RecordingsTableUI.AreAllGroupMembersHidden(d, c, m);
        }

        private string Run(string name, G.IUnits u)
        {
            C.ResetAll();
            logLines.Clear();
            return G.RunSweep(name, u, logLines);
        }

        [Theory]
        [MemberData(nameof(G.Sweeps), MemberType = typeof(G))]
        public void ShippedHelper_MatchesTheOriginalBlock(string name)
        {
            string oracle = Run(name, new G.OracleUnits());
            string shipped = Run(name, new ShippedUnits());

            Assert.True(G.Goldens[name] == shipped,
                name + ": golden differs; the shipped helper prints\n<<<" + shipped + ">>>");
            Assert.True(oracle == shipped,
                name + ": oracle and shipped differ\n--- oracle\n" + oracle + "\n--- shipped\n" + shipped);
        }

        // The enum's order is the oracle's branch numbering (0 = G + S, 1 = G + X, 2 = G).
        [Fact]
        public void ManageCell_EnumOrderIsTheBranchOrder()
        {
            Assert.Equal(0, (int)RecordingsTableUI.GroupManageCell.GroupAndSegments);
            Assert.Equal(1, (int)RecordingsTableUI.GroupManageCell.GroupAndDisband);
            Assert.Equal(2, (int)RecordingsTableUI.GroupManageCell.GroupOnly);
        }

        // ---- Readable direct checks, one behaviour each ----

        [Fact]
        public void AllEnabled_AnEmptyFolderIsNeverAllEnabled()
        {
            Assert.False(RecordingsTableUI.AreAllGroupMembersPlaybackEnabled(
                new HashSet<int>(), new List<Recording>(), 0));
        }

        [Fact]
        public void ApplyEnabled_WritesEveryDescendantAndLogsOnce()
        {
            var a = C.MakeRec("a", "Alpha");
            var b = C.MakeRec("b", "Beta");
            var outside = C.MakeRec("c", "Outside");
            C.Commit(a, b, outside);

            RecordingsTableUI.ApplyGroupPlaybackEnabledToggle("G", new HashSet<int> { 0, 1 }, C.Committed, false);

            Assert.False(a.PlaybackEnabled);
            Assert.False(b.PlaybackEnabled);
            Assert.True(outside.PlaybackEnabled);
            Assert.Single(logLines.FindAll(l => l.Contains("Set playback enabled for group 'G': enabled=False")));
        }

        [Fact]
        public void HeaderClick_SecondClickInsideTheThresholdArmsRename_AndKeepsExpansion()
        {
            C.Commit(C.MakeRec("a", "A"));
            var ui = C.MakeTable();

            bool expanded = false;
            ui.HandleGroupHeaderClick("G", C.Committed, ref expanded, 1, 5f);
            Assert.True(expanded);
            ui.HandleGroupHeaderClick("G", C.Committed, ref expanded, 1, 5.1f);

            Assert.True(expanded);
            Assert.Equal("G", ui.RenamingGroupForTesting);
            Assert.Equal("G", ui.RenamingGroupTextForTesting);
            Assert.Contains("G", ui.ExpandedGroupsForTesting);
        }

        [Fact]
        public void HeaderClick_SlowSecondClickCollapses()
        {
            C.Commit(C.MakeRec("a", "A"));
            var ui = C.MakeTable();

            bool expanded = false;
            ui.HandleGroupHeaderClick("G", C.Committed, ref expanded, 1, 5f);
            ui.HandleGroupHeaderClick("G", C.Committed, ref expanded, 1, 6f);

            Assert.False(expanded);
            Assert.Null(ui.RenamingGroupForTesting);
            Assert.DoesNotContain("G", ui.ExpandedGroupsForTesting);
            Assert.Contains(logLines, l => l.Contains("[UI]") && l.Contains("Group 'G' collapsed (1 recordings)"));
        }

        [Fact]
        public void RenameKeys_EscapeCancelsWithoutRenaming()
        {
            var a = C.MakeRec("a", "A");
            a.RecordingGroups = new List<string> { "Old" };
            C.Commit(a);
            var ui = C.MakeTable();
            Assert.True(ui.TryBeginGroupRenameForTesting("Old", "New"));

            Assert.True(ui.HandleGroupRenameKeys("Old", false, true));

            Assert.Null(ui.RenamingGroupForTesting);
            Assert.Equal(new List<string> { "Old" }, a.RecordingGroups);
        }

        [Fact]
        public void RenameKeys_NoKeyIsNotConsumed()
        {
            C.Commit(C.MakeRec("a", "A"));
            var ui = C.MakeTable();

            Assert.False(ui.HandleGroupRenameKeys("G", false, false));
        }

        [Fact]
        public void LoopScope_WithoutAbsorptionIsTheDescendantSetItself()
        {
            var d = new HashSet<int> { 1, 2 };
            Assert.Same(d, RecordingsTableUI.ResolveGroupLoopScope(d, null));
        }

        [Fact]
        public void LoopScope_WithAbsorptionIsANewUnion_DescendantsUntouched()
        {
            var d = new HashSet<int> { 2 };
            var scope = RecordingsTableUI.ResolveGroupLoopScope(d, new List<int> { 0, 2 });

            Assert.NotSame(d, scope);
            Assert.Equal(new[] { 0, 2 }, new SortedSet<int>(scope));
            Assert.Equal(new[] { 2 }, new SortedSet<int>(d));
        }

        [Fact]
        public void Rotation_MissingRecordingReadsAsEmpty()
        {
            var list = new List<Recording> { C.MakeRec("a", "A") };
            int next;
            WatchResult r = RecordingsTableUI.ResolveGroupWatchRotation(
                new HashSet<int> { 0 }, list, idx => { list[0] = C.MakeRec("b", "B"); return true; },
                null, null, out next);

            Assert.Equal(-1, next);
            Assert.Null(r.NextRecordingId);
            Assert.Equal(0, r.TotalEligible);
        }

        [Fact]
        public void PressModes_ToggleOffNeedsAWatchedDescendant()
        {
            var off = new WatchResult("r", 1, 1, true, false);
            bool toggleOff;

            Assert.False(RecordingsTableUI.ResolveGroupWatchPressModes(off, 0, false, out toggleOff));
            Assert.False(toggleOff);
            Assert.False(RecordingsTableUI.ResolveGroupWatchPressModes(off, 0, true, out toggleOff));
            Assert.True(toggleOff);
        }

        [Fact]
        public void TransitionCache_SilentWhileNothingMoves()
        {
            var can = new Dictionary<string, bool>();
            var fp = new Dictionary<string, string>();

            Assert.True(RecordingsTableUI.UpdateGroupWatchTransitionCache(can, fp, "G/a", true, "a,b"));
            Assert.False(RecordingsTableUI.UpdateGroupWatchTransitionCache(can, fp, "G/a", true, "a,b"));
            Assert.True(RecordingsTableUI.UpdateGroupWatchTransitionCache(can, fp, "G/a", true, "a"));
        }

        [Fact]
        public void WatchTooltip_SwitchNamesTheNextVessel()
        {
            var list = new List<Recording> { C.MakeRec("a", "Kerbal X"), C.MakeRec("b", "Probe") };
            string tip = RecordingsTableUI.BuildGroupWatchTooltip(
                new WatchResult("b", 2, 2, false, false), true, 1, "a", list);

            Assert.Equal("switch to Probe", tip);
        }

        [Fact]
        public void AdvancePressKind_NoCursorIsAnEnter()
        {
            Assert.Equal("enter", RecordingsTableUI.GroupWatchAdvancePressKind(null, true));
            Assert.Equal("wrap", RecordingsTableUI.GroupWatchAdvancePressKind("a", true));
            Assert.Equal("advance", RecordingsTableUI.GroupWatchAdvancePressKind("a", false));
        }

        // The folder W hover's fixed texts sit in the single-line Recordings help strip
        // (the vessel-named ones overflow into the marquee by design).
        [Fact]
        public void WatchTooltip_FixedTextsFitTheRecordingsStrip()
        {
            int budget = TooltipEchoBox.BudgetChars(1355f, TooltipEchoBox.SingleLine);
            var list = new List<Recording> { C.MakeRec("a", "A") };
            foreach (string tip in new[]
                     {
                         RecordingsTableUI.BuildGroupWatchTooltip(WatchResult.Empty, false, -1, null, list),
                         RecordingsTableUI.BuildGroupWatchTooltip(new WatchResult("a", 1, 1, true, false), true, 0, "a", list),
                         RecordingsTableUI.BuildGroupWatchTooltip(new WatchResult("a", 1, 1, false, false), false, -1, null, list),
                     })
            {
                Assert.DoesNotContain("\n", tip);
                Assert.True(tip.Length <= budget, tip.Length + " > " + budget + ": " + tip);
            }
        }
    }
}
