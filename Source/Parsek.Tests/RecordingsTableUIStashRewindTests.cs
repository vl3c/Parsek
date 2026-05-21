using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class RecordingsTableUIStashRewindTests : IDisposable
    {
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public RecordingsTableUIStashRewindTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        [Fact]
        public void SeparateColumns_StashableLegacyOwner_ShowsRewindAndStashSeal()
        {
            var rec = StableLeaf("rec_owner", "bp_1", rewindSave: "parsek_rw_owner");
            InstallScenario(Rp("rp_1", "bp_1", focusSlot: 0, Slot(0, rec.RecordingId)));

            bool showRewind = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 200.0);
            var reFlyAction = RecordingsTableUI.ResolveReFlyColumnAction(rec);

            Assert.True(showRewind);
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.StashSeal, reFlyAction);
        }

        [Fact]
        public void SeparateColumns_StashableTreeBranchNonOwner_ShowsOnlyStashSeal()
        {
            var root = StableLeaf("rec_root", null, rewindSave: "parsek_rw_root");
            root.TreeId = "tree_1";
            var branch = StableLeaf("rec_branch", "bp_1");
            branch.TreeId = "tree_1";
            var tree = new RecordingTree
            {
                Id = "tree_1",
                RootRecordingId = root.RecordingId,
                TreeName = "TreeWithRootRewind",
            };
            tree.AddOrReplaceRecording(root);
            tree.AddOrReplaceRecording(branch);
            RecordingStore.AddCommittedTreeForTesting(tree);
            InstallScenario(Rp("rp_1", "bp_1", focusSlot: 0, Slot(0, branch.RecordingId)));

            Assert.True(RecordingsTableUI.TryResolveStashableUnfinishedFlightRewindPoint(
                branch, out _, out _));

            bool showRewind = RecordingsTableUI.ShouldShowLegacyRewindButton(branch, now: 200.0);
            var reFlyAction = RecordingsTableUI.ResolveReFlyColumnAction(branch);

            Assert.False(showRewind);
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.StashSeal, reFlyAction);
        }

        [Fact]
        public void SeparateColumns_NotStashableLegacyOwner_ShowsOnlyRewind()
        {
            var rec = StableLeaf("rec_no_bp", null, rewindSave: "parsek_rw_owner");

            bool showRewind = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 200.0);
            var reFlyAction = RecordingsTableUI.ResolveReFlyColumnAction(rec);

            Assert.True(showRewind);
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.None, reFlyAction);
        }

        [Fact]
        public void SeparateColumns_FutureRecording_StillUsesForwardColumn()
        {
            var rec = StableLeaf("rec_future", "bp_1", rewindSave: "parsek_rw_future");
            InstallScenario(Rp("rp_1", "bp_1", focusSlot: 0, Slot(0, rec.RecordingId)));

            bool showForward = RecordingsTableUI.ShouldShowForwardButton(rec, now: 50.0);
            bool showRewind = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 50.0);

            Assert.True(showForward);
            Assert.False(showRewind);
        }

        [Fact]
        public void SeparateColumns_UnfinishedFlightRow_ShowsFlySealOnly()
        {
            // An OPEN crashed Unfinished Flight: its effective tip is
            // CommittedProvisional (open) after promotion, so IsUnfinishedFlight
            // is true (collapse-seal-into-mergestate).
            var rec = StableLeaf("rec_crashed", "bp_1", rewindSave: "parsek_rw_crashed",
                terminal: TerminalState.Destroyed,
                mergeState: MergeState.CommittedProvisional);
            RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_crashed");
            InstallScenario(Rp("rp_1", "bp_1", focusSlot: 0, Slot(0, rec.RecordingId)));

            Assert.True(EffectiveState.IsUnfinishedFlight(rec));

            bool showRewind = RecordingsTableUI.ShouldShowLegacyRewindButton(rec, now: 200.0);
            var reFlyAction = RecordingsTableUI.ResolveReFlyColumnAction(rec);

            Assert.False(showRewind);
            Assert.Equal(RecordingsTableUI.ReFlyColumnAction.FlySeal, reFlyAction);
        }

        private static Recording StableLeaf(
            string id,
            string parentBranchPointId,
            string rewindSave = null,
            TerminalState terminal = TerminalState.Landed,
            MergeState mergeState = MergeState.Immutable)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = mergeState,
                ParentBranchPointId = parentBranchPointId,
                TerminalStateValue = terminal,
                RewindSaveFileName = rewindSave,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 150.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 150.0 });
            return rec;
        }

        private static ChildSlot Slot(int slotIndex, string recordingId)
        {
            return new ChildSlot
            {
                SlotIndex = slotIndex,
                OriginChildRecordingId = recordingId,
                Controllable = true,
            };
        }

        private static RewindPoint Rp(
            string rpId,
            string bpId,
            int focusSlot,
            params ChildSlot[] slots)
        {
            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                FocusSlotIndex = focusSlot,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>(slots ?? Array.Empty<ChildSlot>()),
            };
        }

        private static void InstallScenario(params RewindPoint[] rps)
        {
            var scenario = new ParsekScenario
            {
                RewindPoints = new List<RewindPoint>(rps ?? Array.Empty<RewindPoint>()),
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
        }
    }
}
