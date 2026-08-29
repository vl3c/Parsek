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
    /// <item>And with that state in the pending slot, the decision predicate USED TO
    /// route to ghost-only, which destroys real spawn-at-end eligibility.</item>
    /// </list>
    ///
    /// <para>Together these made the branch demonstrably NOT dead code at the level
    /// of save state, and the S0.9 (cold) and S0.10 (warm) flights of 2026-08-29 then
    /// measured the shipped product walking it and destroying both snapshots, on a
    /// warm entrance that needs no fault at all.</para>
    ///
    /// <para><b>THE FIX (2026-08-29) lives in this file too.</b> A non-re-fly Limbo /
    /// LimboVesselSwitch tree now commits through the dialog's own
    /// <c>MergeDialog.MergeCommit</c> with its snapshots preserved
    /// (<c>ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity</c>), so cells
    /// 2 and 3 assert the NEW route while cell 4 keeps measuring what the ghost-only
    /// branch costs — the branch is still reached, by the three carve-outs that
    /// justify it (autoMerge off, an active re-fly, MAINMENU). Cells 5 and 6 are the
    /// fix and its leak detector.</para>
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
            // class deliberately does NOT re-suppress afterwards the way 406 other
            // Sequential classes do: leaving the global suppressed makes the NEXT
            // Sequential class's log-capture assertions read an EMPTY list, which is a
            // silent cross-class dependency on ordering. Not hypothetical — adding this
            // class turned AutorunExitTests.PerformAutorunExit_ThrowingQuit_Is-
            // ContainedAsError red in the full suite while it still passed under
            // --filter. This Dispose is the fix template; the population and the reason
            // no sweep is proposed are in docs/dev/todo-and-known-bugs.md ->
            // TEST-HYGIENE — SUPPRESSLOGGING-LEFT-ON-IN-DISPOSE.
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
            AssertRoute(
                ParsekScenario.AutoCommitFidelity.SilentFullFidelity,
                RecordingStore.PendingTreeStateValue);
        }

        // ---------------------------------------------------------------------
        // 2. The isActive marker: NOT re-finalized. This is the half the entry's
        //    argument misses, and it is what keeps the ghost-only branch alive.
        // ---------------------------------------------------------------------

        [Fact]
        public void SavedActiveMarker_RestoresLimboAtSpaceCenter_AndNowPreservesFidelity()
        {
            var tree = MakeTree("tree_disk_active", "Disk Active", "rec_disk_active");
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, tree, marker: "isActive");

            bool restored = ParsekScenario.TryRestoreActiveTreeNode(node);

            Assert.True(restored);
            Assert.True(RecordingStore.HasPendingTree);
            // A NON-Finalized pending tree, restored from disk, at SPACECENTER.
            Assert.Equal(PendingTreeState.Limbo, RecordingStore.PendingTreeStateValue);
            // PRE-FIX this asserted the ghost-only route. The reachability claim the
            // cell was written to make is UNCHANGED and still what matters — this
            // state does come off disk outside FLIGHT — but what the site DOES with
            // it moved on 2026-08-29: it now commits at fidelity instead of nulling
            // every snapshot. The two-way ShouldSilentFullFidelityCommit this cell used
            // to call was deleted with the fix — its `false` would have been true of
            // BOTH the ghost-only branch and the new fidelity-preserving one, so it
            // could no longer distinguish what this cell is about.
            AssertRoute(
                ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                RecordingStore.PendingTreeStateValue);
        }

        [Fact]
        public void SavedActiveMarkerWithoutActiveRecordingId_RestoresLimboVesselSwitch_AlsoPreserved()
        {
            // Bug #266's outsider shape: ActiveRecordingId null on disk routes to
            // LimboVesselSwitch, the OTHER non-Finalized state. Named separately so a
            // future change that only handles one of the two cannot pass this file.
            var tree = MakeTree("tree_disk_outsider", "Disk Outsider", "rec_disk_outsider");
            tree.ActiveRecordingId = null;
            var node = new ConfigNode("PARSEK_SCENARIO");
            AddTreeNode(node, tree, marker: "isActive");

            Assert.True(ParsekScenario.TryRestoreActiveTreeNode(node));
            Assert.Equal(PendingTreeState.LimboVesselSwitch, RecordingStore.PendingTreeStateValue);
            AssertRoute(
                ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                RecordingStore.PendingTreeStateValue);
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
        // 4. What the ghost-only branch actually costs, measured. THE REPRO half of
        //    the 2026-08-29 fix: this is what a Limbo tree used to get, and what
        //    the three remaining carve-outs still get. Deliberately unchanged.
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
        // 5. THE FIX (2026-08-29). The same Limbo tree, committed at fidelity:
        //    the snapshots the stash deliberately captured SURVIVE. Cell 4 above
        //    is the repro this pairs against — it is what the S0.9 (cold) and
        //    S0.10 (warm) flights measured the product doing on 2026-08-29, and
        //    it is kept unchanged as the ghost-only branch's own cost measurement.
        // ---------------------------------------------------------------------

        [Fact]
        public void LimboTreeAtSpaceCenter_CommitsThroughTheDialogDecisions_AndKeepsItsSnapshots()
        {
            var tree = MakeTree("tree_limbo_fidelity", "Limbo Fidelity", "rec_limbo_fidelity_a");
            tree.Recordings["rec_limbo_fidelity_a"].VesselSnapshot = MakeSnapshotNode("A");
            AddRecording(tree, "rec_limbo_fidelity_b", withSnapshot: true);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            // The route, and then EXACTLY what the site does with it: the dialog's
            // own decisions, applied by the dialog's own ApplyVesselDecisions —
            // which is what MergeDialog.MergeCommit runs first.
            AssertRoute(
                ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                RecordingStore.PendingTreeStateValue);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            int released;
            int preserved = ParsekScenario.CountSnapshotsPreservedByDecisions(
                tree, decisions, out released);
            MergeDialog.ApplyVesselDecisions(tree, decisions);

            // THE assertion, and the exact inverse of cell 4's: nothing was destroyed.
            Assert.Equal(2, preserved);
            Assert.Equal(0, released);
            foreach (var rec in tree.Recordings.Values)
                Assert.NotNull(rec.VesselSnapshot);
        }

        [Theory]
        [InlineData("FLYING")]
        [InlineData("ESCAPING")]
        public void LimboCommitStillGhostOnlysAnUnspawnableShape_AndKeepsItsGhostVisual(string sit)
        {
            // The chosen shape is NOT "preserve every snapshot unconditionally". Neither
            // of these can spawn: a mid-flight `FLYING` capture is destroyed by KSP's
            // on-rails aero check, and an `ESCAPING` one would have been stamped
            // SubOrbital by the finalize path. So the dialog's decisions still
            // ghost-only them, and ApplyVesselDecisions copies GhostVisualSnapshot and
            // releases the crew reservation on the way. That is the whole reason this
            // fix reuses MergeCommit rather than skipping the null pass: a blanket
            // preserve would retain a snapshot nothing can use AND leak its crew.
            //
            // ESCAPING is the row the un-finalized situation gate added: before it, a
            // null-terminal ESCAPING leaf read SPAWNABLE here (the IsSpawnableTerminal
            // rejection sits inside `HasValue`, and the older situation check knows only
            // FLYING/SUB_ORBITAL), so this cell would have measured preserved=1.
            var tree = MakeTree("tree_limbo_unsafe", "Limbo Unsafe", "rec_limbo_unsafe");
            tree.Recordings["rec_limbo_unsafe"].VesselSnapshot =
                MakeSnapshotNode("Unspawnable", sit);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            AssertRoute(
                ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                RecordingStore.PendingTreeStateValue);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            int released;
            int preserved = ParsekScenario.CountSnapshotsPreservedByDecisions(
                tree, decisions, out released);
            MergeDialog.ApplyVesselDecisions(tree, decisions);

            Assert.Equal(0, preserved);
            Assert.Equal(1, released);
            var rec = tree.Recordings["rec_limbo_unsafe"];
            Assert.Null(rec.VesselSnapshot);
            // ...but the ghost geometry survives, which the blanket ghost-only
            // branch (AutoCommitTreeGhostOnly) does NOT do.
            Assert.NotNull(rec.GhostVisualSnapshot);
        }

        // ---------------------------------------------------------------------
        // 6. THE LEAK DETECTOR. Re-fly stays ghost-only on purpose
        //    (silent-full-fidelity-autocommit.md §10): a silent MergeCommit there
        //    would run TryCommitReFlySupersede, writing supersede rows and
        //    flipping MergeState with no dialog. This cell reds if the Limbo fix
        //    ever widens into that carve-out.
        // ---------------------------------------------------------------------

        [Theory]
        [InlineData(PendingTreeState.Finalized)]
        [InlineData(PendingTreeState.Limbo)]
        [InlineData(PendingTreeState.LimboVesselSwitch)]
        public void ReFlyActive_StaysGhostOnly_ForEveryTreeStateIncludingTheFixedOnes(
            PendingTreeState state)
        {
            string reason;
            Assert.Equal(
                ParsekScenario.AutoCommitFidelity.GhostOnly,
                ParsekScenario.ClassifyAutoCommitFidelity(
                    isAutoMerge: true,
                    pendingState: state,
                    reFlyActive: true,
                    loadedScene: GameScenes.SPACECENTER,
                    ghostOnlyReason: out reason));
            Assert.Equal("re-fly-active", reason);

            // And the ghost-only commit it falls to still destroys the snapshots,
            // which for a re-fly is the DESIGNED outcome, not the defect.
            var tree = MakeTree("tree_refly_ghost", "ReFly Ghost", "rec_refly_ghost");
            tree.Recordings["rec_refly_ghost"].VesselSnapshot = MakeSnapshotNode("ReFly");
            Assert.Equal(1, ParsekScenario.AutoCommitTreeGhostOnly(tree));
            Assert.Null(tree.Recordings["rec_refly_ghost"].VesselSnapshot);
        }

        // ---------------------------------------------------------------------
        // 7. THE HARNESS FIXTURE'S PREDICTED NUMBERS, machine-checked. S0.9 and
        //    S0.10 carry a PREDICTED post-fix reading pending their confirm
        //    re-fly; this cell derives those same numbers from the SAME tree the
        //    injector writes, so a wrong prediction reds here in a second rather
        //    than after an hour of flight. Keep the literals in step with both
        //    spec headers.
        // ---------------------------------------------------------------------

        [Fact]
        public void PendingLimboTreeFixture_PredictedCommitNumbers_MatchTheSpecs()
        {
            var tree = Generators.ScenarioWriter.MaterializeTree(
                Generators.PendingLimboTreeFixture.BuildBuilders(baseUT: 1000.0),
                activeRecordingId: Generators.PendingLimboTreeFixture.ChildRecordingId);
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            AssertRoute(
                ParsekScenario.AutoCommitFidelity.LimboPreservingFullFidelity,
                RecordingStore.PendingTreeStateValue);

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            int spawnable = 0;
            foreach (var v in decisions.Values)
                if (v) spawnable++;
            int released;
            int preserved = ParsekScenario.CountSnapshotsPreservedByDecisions(
                tree, decisions, out released);

            // The exact tokens the two spec headers predict.
            Assert.Equal(4, tree.Recordings.Count);
            Assert.Equal(3, spawnable);
            Assert.Equal(3, preserved);
            Assert.Equal(1, released);

            // And WHICH leaf lands where, because the totals alone would survive a
            // fixture whose leaves swapped roles. Note all four are LEAVES: the tree
            // carries no BranchPoints and no ChildBranchPointId, so `ParentRecordingId`
            // alone does not make the root a non-leaf — the trap this cell exists to
            // keep out of the specs.
            Assert.True(decisions[Generators.PendingLimboTreeFixture.RootRecordingId],
                "root: null terminal, LANDED snapshot -> Landed -> spawnable");
            Assert.True(decisions[Generators.PendingLimboTreeFixture.ChildRecordingId],
                "child: TERMINAL Orbiting -> spawnable");
            Assert.True(decisions[Generators.PendingLimboTreeFixture.CoastRecordingId],
                "coast: null terminal, ORBITING snapshot -> Orbiting -> spawnable");
            Assert.False(decisions[Generators.PendingLimboTreeFixture.EscapeRecordingId],
                "escape: null terminal, ESCAPING snapshot -> SubOrbital -> NOT spawnable "
                + "(this is the leaf the un-finalized situation gate moves)");
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private static void AssertRoute(
            ParsekScenario.AutoCommitFidelity expected, PendingTreeState state)
        {
            string reason;
            Assert.Equal(expected, ParsekScenario.ClassifyAutoCommitFidelity(
                isAutoMerge: true,
                pendingState: state,
                reFlyActive: false,
                loadedScene: GameScenes.SPACECENTER,
                ghostOnlyReason: out reason));
        }

        /// <summary>
        /// A snapshot node carrying a `sit`, because a real one always does — they are
        /// written by <c>VesselSpawner.TryBackupSnapshot</c> -> <c>ProtoVessel.Save</c>.
        /// The default is ORBITING (stable and spawnable) so a cell that does not care
        /// about the situation still exercises a shape the product actually produces.
        /// The un-finalized spawn gate reads this field, so a sit-less node would
        /// quietly test a fixture that cannot occur.
        /// </summary>
        private static ConfigNode MakeSnapshotNode(string tag, string sit = "ORBITING")
        {
            var snap = new ConfigNode("VESSEL");
            snap.AddValue("name", "Snapshot " + tag);
            snap.AddValue("sit", sit);
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
