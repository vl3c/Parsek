using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// <see cref="RecordingsTableUI.ScrollToRecording"/> - the Recordings tab's navigation API.
    ///
    /// <para>Why this class exists. The method's only production caller (the Timeline GoTo
    /// button) moved to the Missions tab in design 4.1a, and the decision was to KEEP the method
    /// as the Recordings tab's own navigation API. Three pre-existing cells already call it, but
    /// every one of them uses it purely as a lever to reach <c>TabRecordings</c> and asserts
    /// nothing past its first three lines - so the resolve, the un-archive, the group expansion
    /// and the scheduled scroll had no coverage at all while carrying `[Fact]` names that read
    /// like they did. That is the exact shape in which a callerless API rots: change the row
    /// counter or the row-height constant the draw pairs with it and nothing reds.</para>
    ///
    /// <para>Touches the RecordingStore / GroupHierarchyStore / ParsekLog statics, hence
    /// [Collection("Sequential")].</para>
    /// </summary>
    [Collection("Sequential")]
    public class RecordingsTableApiTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecordingsTableApiTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording MakeRec(string id, string name = "NavVessel", params string[] groups)
        {
            var rec = new Recording { RecordingId = id, VesselName = name };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            if (groups != null && groups.Length > 0)
                rec.RecordingGroups = new List<string>(groups);
            return rec;
        }

        private static RecordingsTableUI Commit(Recording rec)
        {
            RecordingStore.AddCommittedInternal(rec);
            EffectiveState.ResetCachesForTesting();
            return new RecordingsTableUI(null);
        }

        // --- What the method is FOR: scheduling the scroll ---

        // The scroll id is the whole point of the call. The row draw consumes it two passes
        // later, so scheduling it is the only outcome a headless test can see - and it was
        // previously asserted by nothing.
        [Fact]
        public void SchedulesTheScrollForTheTargetRecording()
        {
            var ui = Commit(MakeRec("nav-1"));

            ui.ScrollToRecording("nav-1");

            Assert.Equal("nav-1", ui.PendingScrollToRecordingIdForTesting);
            Assert.True(ui.IsOpen);
            Assert.Equal(RecordingsTableUI.TabRecordings, ui.SelectedTabForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: scroll requested for"));
        }

        // ERS-routed: a recording outside the effective set is not navigable, and the method
        // must not leave a scroll armed for a row that will never be drawn.
        [Fact]
        public void SchedulesNothingForARecordingOutsideTheEffectiveSet()
        {
            var ui = new RecordingsTableUI(null);

            ui.ScrollToRecording("no-such-recording");

            Assert.Null(ui.PendingScrollToRecordingIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("not found in effective recording set"));
        }

        // --- Making the destination visible ---

        // An archived recording is filtered out of the tab, so scrolling to it without clearing
        // its flag would scroll to a row that is not drawn.
        [Fact]
        public void UnarchivesAnArchivedTarget()
        {
            var rec = MakeRec("nav-1");
            rec.Hidden = true;
            var ui = Commit(rec);

            ui.ScrollToRecording("nav-1");

            Assert.False(rec.Hidden);
            Assert.Equal("nav-1", ui.PendingScrollToRecordingIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: unhid recording"));
        }

        // Clearing the recording's own flag is sufficient, so the global filter is left exactly
        // as the player set it. (A branch that also cleared it was dead code - it tested the flag
        // after the un-archive above had cleared it - and was removed rather than repaired.)
        [Fact]
        public void LeavesTheGlobalArchiveFilterAlone()
        {
            var rec = MakeRec("nav-1");
            rec.Hidden = true;
            GroupHierarchyStore.HideActive = true;
            var ui = Commit(rec);

            ui.ScrollToRecording("nav-1");

            Assert.True(GroupHierarchyStore.HideActive);
            Assert.False(rec.Hidden);
            Assert.DoesNotContain(logLines, l => l.Contains("disabled HideActive"));
        }

        // A collapsed ancestor hides the row as surely as the archive flag does, so the whole
        // chain from the recording's group up to the root must be expanded, not just the leaf.
        [Fact]
        public void ExpandsTheTargetsGroupAndEveryAncestor()
        {
            GroupHierarchyStore.groupParents["Munshot / Stage 1"] = "Munshot";
            GroupHierarchyStore.groupParents["Munshot"] = "Career";
            var ui = Commit(MakeRec("nav-1", "NavVessel", "Munshot / Stage 1"));

            ui.ScrollToRecording("nav-1");

            Assert.Contains("Munshot / Stage 1", ui.ExpandedGroupsForTesting);
            Assert.Contains("Munshot", ui.ExpandedGroupsForTesting);
            Assert.Contains("Career", ui.ExpandedGroupsForTesting);
            Assert.Contains(logLines, l => l.Contains("Cross-link: expanded ancestor group"));
        }

        // A hidden GROUP is the one case where the filter genuinely has to move: the flag is on
        // the group, not the recording, so un-archiving the row cannot reveal it.
        [Fact]
        public void UnhidesTheTargetsHiddenGroupWhenTheFilterIsOn()
        {
            GroupHierarchyStore.HideActive = true;
            GroupHierarchyStore.AddHiddenGroup("Munshot");
            var ui = Commit(MakeRec("nav-1", "NavVessel", "Munshot"));

            ui.ScrollToRecording("nav-1");

            Assert.False(GroupHierarchyStore.IsGroupHidden("Munshot"));
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: unhid group"));
        }

        // ...but with the filter off, a hidden group is not hiding anything, so the player's
        // hidden-group set is left untouched.
        [Fact]
        public void LeavesHiddenGroupsAloneWhenTheFilterIsOff()
        {
            GroupHierarchyStore.HideActive = false;
            GroupHierarchyStore.AddHiddenGroup("Munshot");
            var ui = Commit(MakeRec("nav-1", "NavVessel", "Munshot"));

            ui.ScrollToRecording("nav-1");

            Assert.True(GroupHierarchyStore.IsGroupHidden("Munshot"));
            Assert.Equal("nav-1", ui.PendingScrollToRecordingIdForTesting);
        }

        // A recording with no groups is the common standalone case and must not throw.
        [Fact]
        public void HandlesATargetWithNoGroups()
        {
            var ui = Commit(MakeRec("nav-1"));

            ui.ScrollToRecording("nav-1");

            Assert.Equal("nav-1", ui.PendingScrollToRecordingIdForTesting);
            Assert.Empty(ui.ExpandedGroupsForTesting);
        }
    }
}
