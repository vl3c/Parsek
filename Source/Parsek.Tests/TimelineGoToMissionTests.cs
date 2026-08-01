using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Timeline GoTo cross-link targets the MISSIONS tab
    /// (`docs/dev/design-ui-basic-advanced.md` section 4.1a).
    ///
    /// <para>The regression these cells exist for: GoTo used to open the raw Recordings tab,
    /// which Basic hides, so the button had to be hidden in Basic too and a Basic player had
    /// no way to get from a Timeline row to the flight it belongs to. Re-pointing it at a
    /// surface Basic hides would strand them again.</para>
    ///
    /// <para>Touches the RecordingStore / MissionStore / ParsekLog statics, hence
    /// [Collection("Sequential")].</para>
    /// </summary>
    [Collection("Sequential")]
    public class TimelineGoToMissionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public TimelineGoToMissionTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            // Three cells below assert on Verbose lines, and every cell constructs a ParsekUI -
            // which writes the static activeInstance and re-seeds the static applied-mode latch.
            // Reset both ends so a leaked ParsekSettings from an earlier class cannot drop those
            // lines, and so this class cannot leave a live instance for the next one.
            ParsekUI.ResetUiComplexityModeForTesting();
        }

        public void Dispose()
        {
            ParsekUI.ResetUiComplexityModeForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MissionStore.SuppressLogging = true;
            MissionStore.HideArchived = false;
            MissionStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private static Recording MakeRec(string id, string treeId, string name = "GoToVessel")
        {
            var rec = new Recording { RecordingId = id, TreeId = treeId, VesselName = name };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0 });
            return rec;
        }

        // Commits a one-recording tree and seeds its default (original) mission, the same way
        // the Missions tab does on its first draw.
        private static Mission CommitTreeWithMission(
            string treeId, string treeName, string recordingId)
        {
            var rec = MakeRec(recordingId, treeId, treeName);
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;
            RecordingStore.CommitTree(tree);
            MissionStore.EnsureDefaultsForTrees(RecordingStore.CommittedTrees);
            return MissionStore.FindOriginalMission(treeId);
        }

        // --- The happy path ---

        // The whole point of the change: one click lands the player on a tab that exists in
        // BOTH modes, on the mission that owns the row they clicked.
        [Fact]
        public void GoToOpensTheMissionsTabAndSchedulesTheOwningMission()
        {
            Mission mission = CommitTreeWithMission("tree-1", "Munshot", "rec-1");

            var ui = new ParsekUI(UIMode.KSC);
            var table = ui.GetRecordingsTableUI();
            Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);

            table.ShowMissionForRecording("rec-1");

            Assert.True(table.IsOpen);
            Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);
            Assert.Equal(mission.Id, ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: reveal requested for mission"));
        }

        // The Recordings tab is still reachable by hand in Advanced, so a GoTo click that
        // arrives while the player sits on it must move them to Missions - otherwise the
        // reveal is scheduled against a tab that never draws it.
        [Fact]
        public void GoToMovesTheSelectionOffTheRecordingsTab()
        {
            CommitTreeWithMission("tree-1", "Munshot", "rec-1");

            var ui = new ParsekUI(UIMode.KSC);
            var table = ui.GetRecordingsTableUI();
            table.ScrollToRecording("rec-1");
            Assert.Equal(RecordingsTableUI.TabRecordings, table.SelectedTabForTesting);

            table.ShowMissionForRecording("rec-1");

            Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: selecting Missions tab"));
        }

        // A tree can carry several missions (clones). The pick must be the deterministic
        // original - the same one MissionGroupLink name-links to the tree's root group - not
        // "whichever clone happened to sort first".
        [Fact]
        public void GoToPicksTheTreesOriginalMissionNotAClone()
        {
            Mission original = CommitTreeWithMission("tree-1", "Munshot", "rec-1");
            Mission clone = MissionStore.Clone(original);
            Assert.NotEqual(original.Id, clone.Id);

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.Equal(original.Id, ui.GetMissionsUI().PendingRevealMissionIdForTesting);
        }

        // --- The Archive filter ---

        // The Archive filter drops an archived mission's whole block from the list, so a
        // reveal aimed at one would scroll to a row that is never drawn. The clear is QUEUED,
        // never written here: the click lands mid-frame inside the Timeline's handler, and this
        // filter decides how many mission blocks the Missions tab draws - writing it now would
        // desync that frame's Layout and Repaint control counts and throw. The draw applies it
        // on its own Layout pass. Clearing the GLOBAL filter is reversible with one click on
        // the tab's own checkbox.
        [Fact]
        public void GoToQueuesTheArchiveFilterClearWhenItWouldHideTheTarget()
        {
            Mission mission = CommitTreeWithMission("tree-1", "Munshot", "rec-1");
            mission.Archived = true;
            MissionStore.HideArchived = true;

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.True(ui.GetMissionsUI().PendingClearArchiveFilterForTesting);
            Assert.True(MissionStore.HideArchived);   // the draw clears it, not the click
            Assert.Equal(mission.Id, ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[UI]") && l.Contains("Cross-link: queued an Archive-filter clear"));
        }

        // ...but the mission's own Archived flag is a player decision. Navigating to a mission
        // must never un-archive it.
        [Fact]
        public void GoToNeverUnarchivesTheMission()
        {
            Mission mission = CommitTreeWithMission("tree-1", "Munshot", "rec-1");
            mission.Archived = true;
            MissionStore.HideArchived = true;

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.True(mission.Archived);
        }

        // A filter that is not hiding the target is left exactly as the player set it - not
        // even queued for clearing.
        [Fact]
        public void GoToLeavesTheArchiveFilterAloneForANonArchivedTarget()
        {
            CommitTreeWithMission("tree-1", "Munshot", "rec-1");
            MissionStore.HideArchived = true;

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.True(MissionStore.HideArchived);
            Assert.False(ui.GetMissionsUI().PendingClearArchiveFilterForTesting);
        }

        // --- Failure paths: land on the tab, warn, schedule nothing ---

        [Fact]
        public void GoToWarnsAndSchedulesNothingWhenTheRecordingIsNotEffective()
        {
            var ui = new ParsekUI(UIMode.KSC);
            var table = ui.GetRecordingsTableUI();

            table.ShowMissionForRecording("no-such-recording");

            Assert.True(table.IsOpen);
            Assert.Equal(RecordingsTableUI.TabMissions, table.SelectedTabForTesting);
            Assert.Null(ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("not found in the effective recording set"));
        }

        [Fact]
        public void GoToWarnsAndSchedulesNothingWhenTheRecordingHasNoTree()
        {
            RecordingStore.AddCommittedInternal(MakeRec("rec-1", null));

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.Null(ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("belongs to no mission"));
        }

        // Backstop: the recording names a tree that is not committed, so there is nothing to
        // seed a mission from and nothing to scroll to.
        [Fact]
        public void GoToWarnsAndSchedulesNothingWhenTheTreeIsNotCommitted()
        {
            RecordingStore.AddCommittedInternal(MakeRec("rec-1", "tree-1"));

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Assert.Null(ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]") && l.Contains("has no mission for recording"));
        }

        // A tree committed mid-scene (post-revert merge, rapid-switch commit) carries no Mission
        // until the Missions tab's own draw seeds one - and the freshest flight is exactly the
        // row a player is most likely to click GoTo on. The reveal seeds the default itself so
        // the common case lands on the mission instead of on the backstop warn.
        [Fact]
        public void GoToSeedsTheDefaultMissionForATreeCommittedMidScene()
        {
            var rec = MakeRec("rec-1", "tree-1", "Munshot");
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Munshot",
                RootRecordingId = rec.RecordingId
            };
            tree.Recordings[rec.RecordingId] = rec;
            RecordingStore.CommitTree(tree);
            Assert.Null(MissionStore.FindOriginalMission("tree-1"));

            var ui = new ParsekUI(UIMode.KSC);
            ui.GetRecordingsTableUI().ShowMissionForRecording("rec-1");

            Mission seeded = MissionStore.FindOriginalMission("tree-1");
            Assert.NotNull(seeded);
            Assert.Equal(seeded.Id, ui.GetMissionsUI().PendingRevealMissionIdForTesting);
            // Specifically not the backstop path. (Scoped to cross-link warns: committing a
            // tree emits unrelated merger logging.)
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[WARN]") && l.Contains("Cross-link:"));
        }

        // A second click must not leave the first target armed behind it.
        [Fact]
        public void AFailedRevealClearsAPreviouslyScheduledTarget()
        {
            CommitTreeWithMission("tree-1", "Munshot", "rec-1");

            var ui = new ParsekUI(UIMode.KSC);
            var table = ui.GetRecordingsTableUI();
            table.ShowMissionForRecording("rec-1");
            Assert.NotNull(ui.GetMissionsUI().PendingRevealMissionIdForTesting);

            table.ShowMissionForRecording("no-such-recording");

            Assert.Null(ui.GetMissionsUI().PendingRevealMissionIdForTesting);
        }

        // --- Rows that have no mission to go to ---

        // Missions are keyed on recording TREES, and manual Gloops (ghost-only) recordings are
        // committed WITHOUT one - yet they do produce timeline rows. Before this predicate the
        // button was live on those rows and did nothing when clicked, which is the same
        // dead-affordance failure the whole change exists to remove.
        [Fact]
        public void GoToIsDisabledForARecordingThatBelongsToNoMission()
        {
            var gloops = MakeRec("gloops-1", null, "Gloops Recording");

            Assert.False(TimelineWindowUI.CanGoToMission(gloops));
            Assert.Equal("This recording is not part of a mission",
                TimelineWindowUI.GetGoToMissionTooltip(gloops));

            var tree = MakeRec("rec-1", "tree-1");
            Assert.True(TimelineWindowUI.CanGoToMission(tree));
            Assert.Equal("Show this recording's mission",
                TimelineWindowUI.GetGoToMissionTooltip(tree));

            Assert.False(TimelineWindowUI.CanGoToMission(null));
        }

        // Source-text gate for the same thing: the predicate only protects anyone if the button
        // actually reads it. Whitespace-insensitive so a reformat cannot red it.
        [Fact]
        public void TimelineDisablesBothGoToButtonsOnTheCanGoToPredicate()
        {
            string dense = Regex.Replace(ReadTimelineWindowSource(), @"\s+", string.Empty);

            Assert.Equal(2, Regex.Matches(dense, Regex.Escape("GUI.enabled=CanGoToMission(rec);")).Count);
            Assert.Equal(2, Regex.Matches(
                dense, Regex.Escape("newGUIContent(\"GoTo\",GetGoToMissionTooltip(rec))")).Count);
        }

        // --- The mode -> wording bridge (design 9.1) ---

        // The one link between the UI mode and the proximity message. Re-key it to ANY
        // Basic-visible surface and Basic silently goes back to telling the player to open a
        // window it has hidden the launcher for - with every other cell still green, because
        // the formatter cells pass the bool in directly and cannot see this.
        [Fact]
        public void SpawnControlReachabilityFollowsTheModeGate()
        {
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
            try
            {
                ParsekUI.SetUiComplexityMode(UiComplexityMode.Advanced);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
                Assert.True(ParsekUI.IsSpawnControlReachable);

                ParsekUI.SetUiComplexityMode(UiComplexityMode.Basic);
                ParsekUI.ApplyPendingUiComplexityModeIfAny();
                Assert.False(ParsekUI.IsSpawnControlReachable);
            }
            finally
            {
                ParsekSettings.CurrentOverrideForTesting = null;
                ParsekUI.ResetUiComplexityModeForTesting();
            }
        }

        // ...and that the proximity call site actually consults it, rather than passing a
        // literal. Source-text, for the same reason as the GoTo gate: the call sits in a
        // per-frame flight path with no headless seam.
        [Fact]
        public void ProximityNotificationAsksTheUiWhetherSpawnControlIsReachable()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "ParsekFlight.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "ParsekFlight.cs");
            Assert.True(File.Exists(path), $"ParsekFlight.cs not found at {path}");

            string dense = Regex.Replace(File.ReadAllText(path), @"\s+", string.Empty);

            Assert.Contains(
                "SelectiveSpawnUI.FormatProximityNotification(cand,currentUT,ParsekUI.IsSpawnControlReachable)",
                dense);
        }

        // --- The gate key (design 4.1a) ---

        // The button is gated by its TARGET surface's key. That only protects anyone if the
        // target is a surface Basic keeps: pointing GoTo at a hidden surface must hide the
        // button rather than strand the player on a destination that never draws.
        [Fact]
        public void TheGoToGateKeyResolvesVisibleInBothModes()
        {
            Assert.True(UiSurfaceVisibility.IsVisible(
                UiSurface.TabMissions, UiComplexityMode.Basic));
            Assert.True(UiSurfaceVisibility.IsVisible(
                UiSurface.TabMissions, UiComplexityMode.Advanced));
        }

        // Source-text gate: `DrawEntryRow` is an IMGUI callback with no headless seam, so the
        // wiring (which key the two GoTo buttons read, that they read the FRAME-LATCHED mode,
        // and that both route to the missions entry point) can only be pinned by reading the
        // source. Same pattern as SettingsSectionGateWiringTests. Catches a silent revert to
        // UiSurface.TabRecordings, and a row flavour losing its retarget.
        [Fact]
        public void TimelineGatesBothGoToButtonsOnTheMissionsSurface()
        {
            string src = ReadTimelineWindowSource();
            // Whitespace-insensitive: a line-wrap change to any of these calls is a reformat,
            // not a regression, and must not red this cell.
            string dense = Regex.Replace(src, @"\s+", string.Empty);

            Assert.Contains(
                "UiSurfaceVisibility.IsVisible(UiSurface.TabMissions,ParsekUI.AppliedUiComplexityMode)",
                dense);

            // The Recordings tab must not come back as this file's gate key.
            Assert.DoesNotContain("UiSurface.TabRecordings", dense);

            // Both row flavours (RecordingStart and separation) carry the button, and both
            // route through the missions entry point rather than the Recordings-tab one.
            Assert.Equal(2, Regex.Matches(dense, Regex.Escape("if(showMissionCrossLink)")).Count);
            Assert.Equal(2, Regex.Matches(
                dense, Regex.Escape("tableUI.ShowMissionForRecording(entry.RecordingId)")).Count);
            Assert.DoesNotContain("tableUI.ScrollToRecording", dense);
        }

        private static string ReadTimelineWindowSource()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "UI", "TimelineWindowUI.cs");
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", "UI", "TimelineWindowUI.cs");
            Assert.True(File.Exists(path), $"TimelineWindowUI.cs not found at {path}");
            return File.ReadAllText(path);
        }
    }
}
