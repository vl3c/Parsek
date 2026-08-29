using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The HEADLESS half of the AUTOMERGE-ON-BY-DEFAULT question
    /// (docs/dev/todo-and-known-bugs.md): with <c>autoMerge</c> now forced ON for
    /// every player on every load, can a non-<see cref="PendingTreeState.Finalized"/>
    /// pending tree reach <c>ParsekScenario.AutoCommitPendingTreeOutsideFlight</c>'s
    /// ghost-only branch — the branch that nulls EVERY <c>VesselSnapshot</c> where
    /// the pre-flip dialog used to ask?
    ///
    /// <para>The entry's own reasoning was "a saved pending tree restores as
    /// Finalized (TryRestorePendingTreeNode), so the branch may be dead code". These
    /// cells pin what that argument gets RIGHT and what it MISSES:</para>
    ///
    /// <list type="number">
    /// <item>The <c>isPending</c> on-disk marker really is structurally
    /// re-finalized — <c>RecordingStore.RestorePendingTreeFromSave</c> hard-sets
    /// <see cref="PendingTreeState.Finalized"/>, so a tree that round-trips through
    /// THAT marker can never reach the ghost-only branch.</item>
    /// <item>But it is not the only marker. A Limbo / LimboVesselSwitch pending tree
    /// is serialized under the <c>isActive</c> marker by
    /// <c>ParsekScenario.SavePendingTreeIfAny</c> — which carries NO scene guard —
    /// and <c>TryRestoreActiveTreeNode</c> reads it straight back as Limbo. So a
    /// non-Finalized tree DOES exist on disk and DOES restore as non-Finalized,
    /// including at a non-FLIGHT scene.</item>
    /// <item>And with that state in the pending slot, the decision predicate routes
    /// to ghost-only, which destroys real spawn-at-end eligibility.</item>
    /// </list>
    ///
    /// <para>Together these make the branch demonstrably NOT dead code at the level
    /// of save state. What they deliberately do NOT settle — and what only a live
    /// run can — is whether a real KSP session ever leaves a Limbo tree parked when
    /// an OnLoad lands outside FLIGHT (the resume window is normally consumed by
    /// <c>ParsekFlight.RestoreActiveTreeFromPending</c> on the next
    /// <c>onFlightReady</c>). Do not read these cells as a defect report.</para>
    /// </summary>
    [Collection("Sequential")]
    public class AutoMergeGhostOnlyReachabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly GameScenes originalScene;

        public AutoMergeGhostOnlyReachabilityTests()
        {
            originalScene = HighLogic.LoadedScene;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            // Every cell here asks its question at a NON-FLIGHT scene on purpose:
            // that is the precondition the auto-commit block itself gates on
            // (`HighLogic.LoadedScene != GameScenes.FLIGHT`).
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
        }

        public void Dispose()
        {
            HighLogic.LoadedScene = originalScene;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            // ResetTestOverrides already restores SuppressLogging to false, and this
            // class deliberately does NOT re-suppress afterwards the way some older
            // Sequential classes do: leaving the global suppressed makes the NEXT
            // Sequential class's log-capture assertions read an empty list, which is a
            // silent cross-class dependency on alphabetical ordering.
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = false;
        }

        // ---------------------------------------------------------------------
        // 1. The isPending marker: structurally re-finalized. The entry's
        //    argument is CORRECT for this marker, and this cell is what pins it.
        // ---------------------------------------------------------------------

        [Fact]
        public void SavedPendingMarker_RestoresFinalized_SoItAlwaysQualifiesForFullFidelity()
        {
            var tree = MakeTree("tree_disk_pending", "Disk Pending", "rec_disk_pending");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, tree, marker: "isPending");

            bool restored = ParsekScenario.TryRestorePendingTreeNode(node);

            Assert.True(restored);
            Assert.Equal(PendingTreeState.Finalized, RecordingStore.PendingTreeStateValue);
            // The whole point: at a non-FLIGHT scene, autoMerge ON, no re-fly, this
            // state takes the FULL-FIDELITY branch, not the ghost-only one.
            Assert.True(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: RecordingStore.PendingTreeStateValue,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER));
        }

        // ---------------------------------------------------------------------
        // 2. The isActive marker: NOT re-finalized. This is the half the entry's
        //    argument misses, and it is what keeps the ghost-only branch alive.
        // ---------------------------------------------------------------------

        [Fact]
        public void SavedActiveMarker_RestoresLimboAtSpaceCenter_AndRoutesToGhostOnly()
        {
            var tree = MakeTree("tree_disk_active", "Disk Active", "rec_disk_active");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, tree, marker: "isActive");

            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);

            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            // A NON-Finalized pending tree, restored from disk, at SPACECENTER.
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: RecordingStore.PendingTreeStateValue,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER));
        }

        [Fact]
        public void SavedActiveMarkerWithoutActiveRecordingId_RestoresLimboVesselSwitch_AlsoGhostOnly()
        {
            // Bug #266's outsider shape: ActiveRecordingId null on disk routes to
            // LimboVesselSwitch, which is the OTHER non-Finalized state the
            // decision predicate rejects. Named separately so a future change that
            // only re-finalized one of the two cannot pass this file.
            var tree = MakeTree("tree_disk_outsider", "Disk Outsider", "rec_disk_outsider");
            tree.ActiveRecordingId = null;
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, tree, marker: "isActive");

            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            Assert.Equal(PendingTreeState.LimboVesselSwitch, RecordingStore.PendingTreeStateValue);
            Assert.False(ParsekScenario.ShouldSilentFullFidelityCommit(
                isAutoMerge: true,
                pendingState: RecordingStore.PendingTreeStateValue,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER));
        }

        // ---------------------------------------------------------------------
        // 3. The loop closes: a Limbo tree WRITES the isActive marker, and it does
        //    so from a non-FLIGHT scene because SavePendingTreeIfAny has no scene
        //    guard (SaveActiveTreeIfAny, the other isActive writer, does).
        // ---------------------------------------------------------------------

        [Fact]
        public void LimboPendingTree_WritesTheActiveMarkerFromANonFlightScene_AndReloadsAsLimbo()
        {
            Assert.NotEqual(GameScenes.FLIGHT, HighLogic.LoadedScene);

            var tree = MakeTree("tree_limbo_roundtrip", "Limbo Roundtrip", "rec_limbo_roundtrip");
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var saved = new ConfigNode("PARSEK_SCENARIO");
            ParsekScenario.SaveTreeRecordings(saved);

            ConfigNode[] treeNodes = saved.GetNodes("RECORDING_TREE");
            ConfigNode activeNode = Array.Find(treeNodes, ParsekScenario.IsActiveTreeNode);
            Assert.NotNull(activeNode);
            Assert.Equal("tree_limbo_roundtrip", activeNode.GetValue("id"));
            Assert.DoesNotContain(treeNodes, ParsekScenario.IsPendingTreeNode);

            // Now read the SAME node back on a fresh store, still outside FLIGHT.
            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(saved));
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
        }

        // ---------------------------------------------------------------------
        // 4. What the ghost-only branch actually costs, measured.
        // ---------------------------------------------------------------------

        [Fact]
        public void GhostOnlyAutoCommit_NullsEverySnapshot_AndReportsHowManyItDestroyed()
        {
            var tree = MakeTree("tree_ghost_cost", "Ghost Cost", "rec_ghost_cost_a");
            AddRecording(tree, "rec_ghost_cost_b", withSnapshot: true);
            AddRecording(tree, "rec_ghost_cost_c", withSnapshot: false);
            tree.Recordings["rec_ghost_cost_a"].VesselSnapshot = MakeSnapshotNode("A");

            int nulled = ParsekScenario.AutoCommitTreeGhostOnly(tree);

            Assert.Equal(2, nulled);
            foreach (var rec in tree.Recordings.Values)
                Assert.Null(rec.VesselSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("Auto-commit tree ghost-only")
                && l.Contains("2 snapshot(s) nulled"));
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static ConfigNode MakeSnapshotNode(string tag)
        {
            var snap = new ConfigNode("VESSEL");
            snap.AddValue("name", "Snapshot " + tag);
            return snap;
        }

        private static ConfigNode AddTreeNode(ConfigNode scenarioNode, RecordingTree tree, string marker)
        {
            ConfigNode treeNode = scenarioNode.AddNode("RECORDING_TREE");
            tree.Save(treeNode);
            treeNode.AddValue(marker, "True");
            return treeNode;
        }

        private static Recording AddRecording(RecordingTree tree, string recordingId, bool withSnapshot)
        {
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = recordingId,
                TreeId = tree.Id,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 110.0,
                VesselPersistentId = 12345,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 110.0 });
            if (withSnapshot)
                rec.VesselSnapshot = MakeSnapshotNode(recordingId);
            tree.AddOrReplaceRecording(rec);
            return rec;
        }

        private static RecordingTree MakeTree(string treeId, string treeName, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = recordingId,
                ActiveRecordingId = recordingId,
            };
            AddRecording(tree, recordingId, withSnapshot: false);
            return tree;
        }
    }
}
