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
    }
}
