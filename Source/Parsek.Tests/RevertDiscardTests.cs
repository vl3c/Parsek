using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for #434: on revert, the pending tree is soft-unstashed (slot cleared) but
    /// sidecar files and tagged events are preserved so that a flight quicksave can still
    /// be F9'd back into. Contrast with merge-dialog Discard, which runs the full #431
    /// purge via <see cref="RecordingStore.DiscardPendingTree"/>.
    /// </summary>
    [Collection("Sequential")]
    public class RevertDiscardTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RevertDiscardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static RecordingTree MakeTreeWithOneRec(string treeName, string recId)
        {
            var tree = new RecordingTree
            {
                Id = System.Guid.NewGuid().ToString("N"),
                TreeName = treeName,
                RootRecordingId = recId,
                ActiveRecordingId = recId,
            };
            tree.Recordings[recId] = new Recording
            {
                RecordingId = recId,
                VesselName = "TestVessel",
                TreeId = tree.Id,
            };
            return tree;
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_ClearsSlot_PreservesFilesAndEvents()
        {
            var tree = MakeTreeWithOneRec("Mun Lander", "rec-mun");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "guid-mun",
                recordingId = "rec-mun",
            });
            Assert.True(RecordingStore.HasPendingTree);
            Assert.Single(GameStateStore.Events);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            // Events NOT purged — MilestoneStore.CurrentEpoch bump in ParsekScenario.OnLoad
            // filters them from ledger walks; the file/event data survives for F9-resume.
            Assert.Single(GameStateStore.Events);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO]") && l.Contains("Unstashed pending tree 'Mun Lander'")
                && l.Contains("sidecar files preserved"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_FromLimboState_WorksToo()
        {
            var tree = MakeTreeWithOneRec("F5 mid-mission", "rec-limbo");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("Unstashed pending tree 'F5 mid-mission'")
                && l.Contains("was state=Limbo"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_FromLimboVesselSwitchState_WorksToo()
        {
            var tree = MakeTreeWithOneRec("Vessel switch", "rec-switch");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            RecordingStore.SetPendingTreeStateForTesting(PendingTreeState.LimboVesselSwitch);

            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            Assert.Contains(logLines, l =>
                l.Contains("was state=LimboVesselSwitch"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_NoPendingTree_IsNoop()
        {
            RecordingStore.UnstashPendingTreeOnRevert();

            Assert.False(RecordingStore.HasPendingTree);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE]") && l.Contains("UnstashPendingTreeOnRevert called with no pending tree"));
        }

        [Fact]
        public void UnstashPendingTreeOnRevert_DifferenceFromDiscardPendingTree()
        {
            // #434 vs #431 semantic contrast:
            //   UnstashPendingTreeOnRevert  -> soft clear, files + events stay
            //   DiscardPendingTree          -> hard clear, files + events purged
            var tree = MakeTreeWithOneRec("Contrast test", "rec-contrast");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Finalized);
            GameStateStore.AddEvent(new GameStateEvent
            {
                ut = 50.0,
                eventType = GameStateEventType.TechResearched,
                key = "node-contrast",
                recordingId = "rec-contrast",
            });

            RecordingStore.UnstashPendingTreeOnRevert();
            Assert.Single(GameStateStore.Events); // tagged event retained on unstash

            // Restash and discard the second path — events should be purged.
            var tree2 = MakeTreeWithOneRec("Discard path", "rec-contrast");
            RecordingStore.StashPendingTree(tree2, PendingTreeState.Finalized);
            RecordingStore.DiscardPendingTree();
            Assert.Empty(GameStateStore.Events); // purge confirmed
        }
    }
}
