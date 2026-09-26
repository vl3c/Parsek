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
    }
}
