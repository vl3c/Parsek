using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The 2026-09-26 Recordings-tab presentation round: the pure decisions behind the
    /// subgroup label, the blank loop-off Period cell and the "Rewind shown once" rule.
    /// Presentation only - nothing here writes a recording or a store.
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTabPresentationRoundTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingsTabPresentationRoundTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ── Item 2: a subgroup drops its parent's prefix when drawn under that parent ──

        [Theory]
        [InlineData("R.1-S.1 / Debris", "R.1-S.1", "Debris")]
        [InlineData("R.1-S.1 / Crew", "R.1-S.1", "Crew")]
        [InlineData("A / B", "A", "B")]
        [InlineData("A / B / C", "A / B", "C")]
        public void DisplayLabel_StripsExactlyTheParentPrefix(string name, string parent, string expected)
        {
            Assert.Equal(expected, GroupPickerPresentation.DisplayLabelUnderParent(name, parent));
        }

        [Theory]
        // catches: a prefix test that forgets the separator, so "R.1-S.10" under "R.1-S.1"
        // would read "0".
        [InlineData("R.1-S.10", "R.1-S.1")]
        // a folder whose name merely CONTAINS the parent's text elsewhere
        [InlineData("Old R.1-S.1 / Debris", "R.1-S.1")]
        // drawn at the root: nothing to strip against
        [InlineData("R.1-S.1 / Debris", null)]
        [InlineData("R.1-S.1 / Debris", "")]
        // case matters: stored names are compared ordinally everywhere else
        [InlineData("r.1-s.1 / Debris", "R.1-S.1")]
        public void DisplayLabel_KeepsTheFullNameWhenItIsNotTheParentsChildName(string name, string parent)
        {
            Assert.Equal(name, GroupPickerPresentation.DisplayLabelUnderParent(name, parent));
        }

        [Fact]
        public void DisplayLabel_AnEmptyOrBlankSuffixKeepsTheFullName()
        {
            Assert.Equal("A / ", GroupPickerPresentation.DisplayLabelUnderParent("A / ", "A"));
            Assert.Equal("A /    ", GroupPickerPresentation.DisplayLabelUnderParent("A /    ", "A"));
            Assert.Equal(string.Empty, GroupPickerPresentation.DisplayLabelUnderParent(null, "A"));
        }

        [Fact]
        public void DisplayLabel_SeparatorIsTheOneTheAutoSubgroupsUse()
        {
            // The auto "/ Debris" and "/ Crew" suffixes are built from this separator; if
            // either drifts the mission's own subgroups stop reading short.
            Assert.StartsWith(GroupPickerPresentation.GroupPathSeparator,
                RecordingGroupStore.DebrisSubgroupSuffix, StringComparison.Ordinal);
            Assert.StartsWith(GroupPickerPresentation.GroupPathSeparator,
                RecordingGroupStore.CrewSubgroupSuffix, StringComparison.Ordinal);
        }

        // ── Item 7: the Period cell is blank while Loop is off, but never a silent hover ──

        [Fact]
        public void BlankPeriodCell_CarriesTheLoopOffReason()
        {
            var manual = new Recording { LoopPlayback = false, LoopTimeUnit = LoopTimeUnit.Sec };
            var auto = new Recording { LoopPlayback = false, LoopTimeUnit = LoopTimeUnit.Auto };

            string manualTip = RecordingsTableUI.LoopPeriodBlankCellTooltip(manual);
            string autoTip = RecordingsTableUI.LoopPeriodBlankCellTooltip(auto);

            Assert.Equal(RecordingsTableUI.LoopPeriodDisabledReason(false, false), manualTip);
            // Loop-off outranks the Auto cause: the fix is the row's own Loop box.
            Assert.Equal(manualTip, autoTip);
            Assert.Contains("Loop", manualTip);
            Assert.DoesNotContain("Settings", autoTip);
            Assert.False(string.IsNullOrEmpty(RecordingsTableUI.LoopPeriodBlankCellTooltip(null)));
        }

        [Fact]
        public void BlankPeriodCell_IsSilentForALoopingRecording()
        {
            var looping = new Recording { LoopPlayback = true };
            Assert.Equal(string.Empty, RecordingsTableUI.LoopPeriodBlankCellTooltip(looping));
        }

        // ── Item 4: Rewind / FF is shown once ──

        [Theory]
        [InlineData(3, 3, true)]
        [InlineData(3, 4, false)]   // a child whose button goes somewhere else keeps it
        [InlineData(3, -1, false)]  // nothing enclosing
        [InlineData(-1, -1, false)] // no own target is never "suppressed"
        [InlineData(-1, 3, false)]
        public void OnlyTheSameTargetIsSuppressed(int own, int enclosing, bool expected)
        {
            Assert.Equal(expected, RecordingsTableUI.IsTimeTargetShownByEnclosingRow(own, enclosing));
        }

        [Fact]
        public void ARowThatDrewFfHandsItsForwardTargetDown()
        {
            RecordingsTableUI.ResolveChildEnclosingTimeTargets(
                ownForwardIdx: 5, ownRewindIdx: -1, ownSuppressed: false,
                inheritedForwardIdx: -1, inheritedRewindIdx: 2,
                out int childF, out int childR);
            Assert.Equal(5, childF);
            Assert.Equal(2, childR);
        }

        [Fact]
        public void ARowThatDrewRHandsItsRewindTargetDown()
        {
            RecordingsTableUI.ResolveChildEnclosingTimeTargets(
                ownForwardIdx: -1, ownRewindIdx: 7, ownSuppressed: false,
                inheritedForwardIdx: 1, inheritedRewindIdx: -1,
                out int childF, out int childR);
            Assert.Equal(1, childF);
            Assert.Equal(7, childR);
        }

        [Fact]
        public void ASuppressedOrEmptyRowPassesItsInheritedTargetsThrough()
        {
            // catches: a blank middle row (a block under its mission folder) resetting the
            // targets, which would let its segments draw the folder's R a second time.
            RecordingsTableUI.ResolveChildEnclosingTimeTargets(
                ownForwardIdx: -1, ownRewindIdx: 7, ownSuppressed: true,
                inheritedForwardIdx: -1, inheritedRewindIdx: 7,
                out int childF, out int childR);
            Assert.Equal(-1, childF);
            Assert.Equal(7, childR);

            RecordingsTableUI.ResolveChildEnclosingTimeTargets(
                ownForwardIdx: -1, ownRewindIdx: -1, ownSuppressed: false,
                inheritedForwardIdx: 4, inheritedRewindIdx: 9,
                out childF, out childR);
            Assert.Equal(4, childF);
            Assert.Equal(9, childR);
        }

        [Fact]
        public void SuppressionCache_LogsOnlyTransitionsAndAFirstSuppressedSighting()
        {
            var cache = new Dictionary<string, bool>();

            // First sighting unsuppressed: the ordinary state, recorded silently.
            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "row:1:r", false));
            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "row:1:r", false));
            Assert.True(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "row:1:r", true));
            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "row:1:r", true));
            Assert.True(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "row:1:r", false));

            // First sighting already suppressed: logged once.
            Assert.True(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "group:G", true));
            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "group:G", true));

            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(cache, "", true));
            Assert.False(RecordingsTableUI.UpdateEnclosingSuppressionCache(null, "k", true));
        }

        [Fact]
        public void SuppressionLog_WritesOneLinePerTransition()
        {
            var ui = new RecordingsTableUI(null);

            ui.LogEnclosingTimeSuppressionTransition("row:4:r", true, "Rewind #4 \"Kerbal X\"");
            ui.LogEnclosingTimeSuppressionTransition("row:4:r", true, "Rewind #4 \"Kerbal X\"");
            ui.LogEnclosingTimeSuppressionTransition("row:4:r", false, "Rewind #4 \"Kerbal X\"");

            Assert.Single(logLines.FindAll(l => l.Contains("[UI]")
                && l.Contains("Rewind #4 \"Kerbal X\": time button suppressed")
                && l.Contains("key=row:4:r")));
            Assert.Single(logLines.FindAll(l => l.Contains("[UI]")
                && l.Contains("no longer suppressed by the enclosing row")));
        }

        // ── Item 5: Info keeps MaxAlt / MaxSpd; Start / End move into the Status hover ──

        private static Recording MakeRec(double startUT, double endUT)
        {
            var rec = new Recording { VesselName = "Test" };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            if (endUT > startUT)
                rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }

        [Fact]
        public void StatusText_DebrisShowsItsEndingWord()
        {
            // catches: the old `!rec.IsDebris` suppression, which made every finished
            // debris row read "past" whatever happened to it.
            var debris = MakeRec(100, 200);
            debris.IsDebris = true;
            debris.TerminalStateValue = TerminalState.Destroyed;

            string text = RecordingsTableUI.ResolveRecordingStatusText(
                debris, 500, out int order, out bool terminal);

            Assert.Equal("Destroyed", text);
            Assert.Equal(2, order);
            Assert.True(terminal);
        }

        [Fact]
        public void StatusText_PastWithoutAnEndingReadsPast_FutureAndActiveCountDown()
        {
            var noEnd = MakeRec(100, 200);
            Assert.Equal("past", RecordingsTableUI.ResolveRecordingStatusText(
                noEnd, 500, out int order, out bool terminal));
            Assert.Equal(2, order);
            Assert.False(terminal);

            var future = MakeRec(600, 700);
            string futureText = RecordingsTableUI.ResolveRecordingStatusText(
                future, 500, out order, out terminal);
            Assert.StartsWith("T-", futureText);
            Assert.Equal(0, order);

            var active = MakeRec(400, 700);
            RecordingsTableUI.ResolveRecordingStatusText(active, 500, out order, out terminal);
            Assert.Equal(1, order);
            Assert.False(terminal);
        }

        [Fact]
        public void FolderStatus_StillIgnoresDebrisWhenPickingItsWord()
        {
            // The leaf change must not leak into the folder word: a mission whose booster
            // was Destroyed after the capsule Landed still reads Landed, and a debris-only
            // folder reads "past".
            var capsule = MakeRec(100, 300);
            capsule.TerminalStateValue = TerminalState.Landed;
            var booster = MakeRec(100, 400);
            booster.IsDebris = true;
            booster.TerminalStateValue = TerminalState.Destroyed;
            var committed = new List<Recording> { capsule, booster };

            RecordingsTableUI.GetGroupStatus(new HashSet<int> { 0, 1 }, committed, 1000,
                out string text, out int order);
            Assert.Equal("Landed", text);
            Assert.Equal(2, order);

            RecordingsTableUI.GetGroupStatus(new HashSet<int> { 1 }, committed, 1000,
                out string debrisOnly, out order);
            Assert.Equal("past", debrisOnly);
        }

        [Fact]
        public void StatusPlace_CarriesTheEvaSourceAndWhereTheFlightEnded()
        {
            string tip = RecordingsTableUI.BuildStatusPlaceTooltip(
                RecordingsTableFormatters.EvaFromPrefix + "Kerbal X",
                "Shores, Kerbin", "Landed");
            Assert.Equal("EVA from Kerbal X - Ends: Shores, Kerbin", tip);
        }

        [Fact]
        public void StatusPlace_DropsALaunchSiteStartTheRowAlreadyShows()
        {
            // A launch-site start is what the Site column (Info) and the Launch column say;
            // only an EVA's source vessel is new information in the hover.
            Assert.Equal("Ends: Orbiting Kerbin", RecordingsTableUI.BuildStatusPlaceTooltip(
                "Launch Pad, Kerbin", "Orbiting Kerbin", "Orbiting"));
        }

        [Theory]
        [InlineData("-")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("Destroyed")]   // would only repeat the status word
        [InlineData("destroyed")]
        public void StatusPlace_DropsAnEndClauseThatSaysNothing(string end)
        {
            Assert.Equal(string.Empty,
                RecordingsTableUI.BuildStatusPlaceTooltip("Launch Pad, Kerbin", end, "Destroyed"));
        }

        [Fact]
        public void StatusPlace_ReadsTheFormatterOutputForADestroyedDebris()
        {
            // End to end over the real formatter: the debris row the old End column read
            // "Destroyed, Kerbin" for keeps that text, now in the Status hover.
            var debris = MakeRec(100, 148);
            debris.IsDebris = true;
            debris.TerminalStateValue = TerminalState.Destroyed;
            debris.StartBodyName = "Kerbin";
            string tip = RecordingsTableUI.BuildStatusPlaceTooltip(
                RecordingsTableUI.FormatStartPosition(debris),
                RecordingsTableUI.FormatEndPosition(debris),
                "Destroyed");
            Assert.Equal("Ends: Destroyed, Kerbin", tip);
        }

        // ── Item 6: Phase and Site live in Info; collapsing Info resets a sort on them ──

        [Theory]
        [InlineData((int)RecordingsTableUI.SortColumn.Phase, true)]
        [InlineData((int)RecordingsTableUI.SortColumn.LaunchSite, true)]
        [InlineData((int)RecordingsTableUI.SortColumn.Index, false)]
        [InlineData((int)RecordingsTableUI.SortColumn.Name, false)]
        [InlineData((int)RecordingsTableUI.SortColumn.LaunchTime, false)]
        [InlineData((int)RecordingsTableUI.SortColumn.Duration, false)]
        [InlineData((int)RecordingsTableUI.SortColumn.Status, false)]
        public void OnlyTheInfoColumnsResetTheSortOnCollapse(int col, bool expected)
        {
            Assert.Equal(expected,
                RecordingsTableUI.ShouldResetSortWhenInfoCollapses((RecordingsTableUI.SortColumn)col));
        }

        [Fact]
        public void CollapsingInfoWhileSortedByPhase_FallsBackToTheDefaultSortAndLogs()
        {
            var ui = new RecordingsTableUI(null);
            ui.ShowExpandedStatsForTesting = true;
            ui.SortColumnIndexForTesting = (int)RecordingsTableUI.SortColumn.Phase;
            ui.SortAscendingForTesting = false;

            ui.SetShowExpandedStats(false, "test");

            Assert.False(ui.ShowExpandedStatsForTesting);
            Assert.Equal((int)RecordingsTableUI.DefaultSortColumn, ui.SortColumnIndexForTesting);
            Assert.Equal(RecordingsTableUI.DefaultSortAscending, ui.SortAscendingForTesting);
            Assert.Contains(logLines, l => l.Contains("[UI]")
                && l.Contains("Recordings sort reset Phase desc -> LaunchTime asc")
                && l.Contains("origin=test"));
        }

        [Fact]
        public void CollapsingInfoWhileSortedByName_KeepsTheSort()
        {
            var ui = new RecordingsTableUI(null);
            ui.ShowExpandedStatsForTesting = true;
            ui.SortColumnIndexForTesting = (int)RecordingsTableUI.SortColumn.Name;
            ui.SortAscendingForTesting = false;

            ui.ShowExpandedStatsForTesting = false;

            Assert.Equal((int)RecordingsTableUI.SortColumn.Name, ui.SortColumnIndexForTesting);
            Assert.False(ui.SortAscendingForTesting);
            Assert.DoesNotContain(logLines, l => l.Contains("Recordings sort reset"));
        }

        [Fact]
        public void TheSeamSetterResetsTheSortTheSameWay()
        {
            var ui = new RecordingsTableUI(null);
            ui.ShowExpandedStatsForTesting = true;
            ui.SortColumnIndexForTesting = (int)RecordingsTableUI.SortColumn.LaunchSite;

            ui.ShowExpandedStatsForTesting = false;

            Assert.Equal((int)RecordingsTableUI.DefaultSortColumn, ui.SortColumnIndexForTesting);
            Assert.Contains(logLines, l => l.Contains("Recordings sort reset LaunchSite")
                && l.Contains("origin=seam"));
        }

        [Theory]
        [InlineData("phase", false, true)]
        [InlineData("site", false, true)]
        [InlineData("phase", true, false)]
        [InlineData("name", false, false)]
        [InlineData("duration", false, false)]
        public void SeamSort_RefusesAnInfoColumnWhileInfoIsShut(string column, bool infoOpen, bool hidden)
        {
            Assert.Equal(hidden, Parsek.TestCommands.TestCommandUiSelectSort.IsSortColumnHidden(
                Parsek.TestCommands.TestCommandUiAction.MissionsWindow,
                Parsek.TestCommands.TestCommandUiSelectSort.RecordingsTabToken,
                column, infoOpen));
        }

        [Fact]
        public void SeamSort_HiddenColumnRuleIsScopedToTheRecordingsTab()
        {
            // The Missions tab has no phase column, and other windows are untouched.
            Assert.False(Parsek.TestCommands.TestCommandUiSelectSort.IsSortColumnHidden(
                Parsek.TestCommands.TestCommandUiAction.MissionsWindow,
                Parsek.TestCommands.TestCommandUiSelectSort.MissionsTabToken, "phase", false));
            Assert.False(Parsek.TestCommands.TestCommandUiSelectSort.IsSortColumnHidden(
                Parsek.TestCommands.TestCommandUiAction.LogisticsWindow,
                Parsek.TestCommands.TestCommandUiSelectSort.RecordingsTabToken, "site", false));
        }

        // ── Item 3: the mission folder draws its launched vessel's segments directly ──

        private static Recording MakeTreeRec(double startUT, double endUT, string name,
            string groupName, string treeId, uint pid, string chainId = null, string id = null)
        {
            var rec = MakeRec(startUT, endUT);
            rec.VesselName = name;
            rec.RecordingId = id ?? Guid.NewGuid().ToString("N");
            rec.TreeId = treeId;
            rec.VesselPersistentId = pid;
            rec.ChainId = chainId;
            rec.RecordingGroups = new List<string> { groupName };
            return rec;
        }

        [Fact]
        public void RootBlockKey_UsesTheTreeVesselIdentity_ThenTheChainFallback()
        {
            var withPid = new Recording { TreeId = "tree-1", VesselPersistentId = 42 };
            Assert.Equal("Kerbal X::treevessel:tree-1:42",
                RecordingsTableUI.ResolveRootVesselBlockKey("Kerbal X", withPid));

            var noPid = new Recording { TreeId = "tree-1", VesselPersistentId = 0, ChainId = "c-7" };
            Assert.Equal("Kerbal X::chain:c-7",
                RecordingsTableUI.ResolveRootVesselBlockKey("Kerbal X", noPid));

            Assert.Null(RecordingsTableUI.ResolveRootVesselBlockKey("Kerbal X",
                new Recording { TreeId = "tree-1" }));
            Assert.Null(RecordingsTableUI.ResolveRootVesselBlockKey("Kerbal X", null));
        }

        [Fact]
        public void TreeRootRecording_IsTheOneTheTreeNames()
        {
            var root = new Recording { RecordingId = "root-1" };
            var other = new Recording { RecordingId = "other-1" };
            var tree = new RecordingTree { RootRecordingId = "root-1" };
            tree.Recordings["root-1"] = root;
            tree.Recordings["other-1"] = other;

            Assert.Same(root, RecordingsTableUI.ResolveTreeRootRecording(tree));
            Assert.Null(RecordingsTableUI.ResolveTreeRootRecording(new RecordingTree()));
            Assert.Null(RecordingsTableUI.ResolveTreeRootRecording(null));
        }

        [Fact]
        public void MissionFolder_AbsorbsOnlyTheLaunchedVesselsBlock()
        {
            // Kerbal X (pid 42, the tree root) flew three segments; a probe (pid 77) that
            // separated from it flew two. Only the root vessel's block is absorbed.
            const string group = "Kerbal X";
            var committed = new List<Recording>
            {
                MakeTreeRec(10, 20, "Kerbal X", group, "tree-kx", 42),
                MakeTreeRec(20, 30, "Kerbal X", group, "tree-kx", 42),
                MakeTreeRec(25, 40, "Kerbal X Probe", group, "tree-kx", 77),
                MakeTreeRec(40, 50, "Kerbal X Probe", group, "tree-kx", 77),
                MakeTreeRec(30, 60, "Kerbal X", group, "tree-kx", 42),
            };
            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                group, new List<int> { 0, 1, 2, 3, 4 }, committed, new Dictionary<string, List<int>>());
            Assert.Equal(2, blocks.Count);

            string key = RecordingsTableUI.ResolveRootVesselBlockKey(group, committed[0]);
            int idx = RecordingsTableUI.FindRootVesselBlockIndex(blocks, key);
            Assert.Equal(0, idx);
            Assert.Equal(new[] { 0, 1, 4 }, blocks[idx].Members);

            var flattened = RecordingsTableUI.FlattenAbsorbedBlock(blocks, idx, group);

            // Three single rows at the block's position, in its member order, then the
            // probe's block untouched.
            Assert.Equal(4, flattened.Count);
            Assert.Equal(new[] { 0 }, flattened[0].Members);
            Assert.Equal(new[] { 1 }, flattened[1].Members);
            Assert.Equal(new[] { 4 }, flattened[2].Members);
            Assert.Equal(new[] { 2, 3 }, flattened[3].Members);
            Assert.Equal("Kerbal X::rec:4", flattened[2].Key);
        }

        [Fact]
        public void MissionFolder_NothingToAbsorbWhenTheRootVesselHasOneSegment()
        {
            const string group = "Hopper";
            var committed = new List<Recording>
            {
                MakeTreeRec(10, 20, "Hopper", group, "tree-h", 5),
                MakeTreeRec(12, 30, "Hopper Debris", group, "tree-h", 6),
                MakeTreeRec(30, 40, "Hopper Debris", group, "tree-h", 6),
            };
            var blocks = RecordingsTableUI.BuildGroupDisplayBlocks(
                group, new List<int> { 0, 1, 2 }, committed, new Dictionary<string, List<int>>());
            string key = RecordingsTableUI.ResolveRootVesselBlockKey(group, committed[0]);

            Assert.Equal(-1, RecordingsTableUI.FindRootVesselBlockIndex(blocks, key));
            Assert.Equal(-1, RecordingsTableUI.FindRootVesselBlockIndex(blocks, null));
            Assert.Same(blocks, RecordingsTableUI.FlattenAbsorbedBlock(blocks, -1, group));
        }

        [Fact]
        public void FlattenAbsorbedBlock_DoesNotDrawASegmentTwice()
        {
            var blocks = new List<RecordingsTableUI.GroupDisplayBlock>
            {
                new RecordingsTableUI.GroupDisplayBlock { Key = "G::rec:3", Members = new List<int> { 3 } },
                new RecordingsTableUI.GroupDisplayBlock { Key = "G::treevessel:t:1", Members = new List<int> { 1, 3, 5 } },
            };
            var flattened = RecordingsTableUI.FlattenAbsorbedBlock(blocks, 1, "G");
            Assert.Equal(3, flattened.Count);
            Assert.Equal(new[] { 3 }, flattened[0].Members);
            Assert.Equal(new[] { 1 }, flattened[1].Members);
            Assert.Equal(new[] { 5 }, flattened[2].Members);
        }

        [Fact]
        public void AbsorbedLoopWrite_RootMembersWithAutoRange_OthersWithout()
        {
            // catches: the mission row writing every descendant the same way, which would
            // either narrow the probe's loop window (it never did under the folder) or stop
            // narrowing the launched vessel's (which its own block's toggle did).
            var scope = new HashSet<int> { 0, 1, 2, 3, 4, 9 };
            RecordingsTableUI.SplitAbsorbedLoopWrite(scope, new List<int> { 0, 1, 4, 9 },
                out List<int> withAuto, out List<int> without);

            Assert.Equal(new[] { 0, 1, 4, 9 }, withAuto);
            without.Sort();
            Assert.Equal(new[] { 2, 3 }, without);
        }

        [Fact]
        public void AbsorbedLoopWrite_EndToEnd_NarrowsOnlyTheLaunchedVessel()
        {
            // Two loopable recordings each with an interesting (propulsive) middle, so the auto
            // range narrows. Written through the split, only the absorbed one is narrowed.
            Recording MakeLoopable(string name)
            {
                var r = new Recording { VesselName = name, RecordingId = name, LaunchSiteName = "LaunchPad" };
                r.Points.Add(new TrajectoryPoint { ut = 0 });
                r.Points.Add(new TrajectoryPoint { ut = 300 });
                r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.SurfaceStationary, startUT = 0, endUT = 100 });
                r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.Atmospheric, startUT = 100, endUT = 200 });
                r.TrackSections.Add(new TrackSection { environment = SegmentEnvironment.ExoBallistic, startUT = 200, endUT = 300 });
                return r;
            }
            var root = MakeLoopable("Root");
            var other = MakeLoopable("Other");
            var committed = new List<Recording> { root, other };
            Assert.True(Recording.IsLoopableRecording(root));

            RecordingsTableUI.SplitAbsorbedLoopWrite(new HashSet<int> { 0, 1 }, new List<int> { 0 },
                out List<int> withAuto, out List<int> without);
            int rootWritten = RecordingsTableUI.BulkSetLoopPlayback(committed, withAuto, true, applyAutoRange: true);
            int otherWritten = RecordingsTableUI.BulkSetLoopPlayback(committed, without, true, applyAutoRange: false);

            Assert.Equal(1, rootWritten);
            Assert.Equal(1, otherWritten);
            Assert.True(root.LoopPlayback);
            Assert.True(other.LoopPlayback);
            Assert.True(double.IsNaN(other.LoopStartUT));
            Assert.Equal(100, root.LoopStartUT);
            Assert.Equal(200, root.LoopEndUT);
        }
    }
}
