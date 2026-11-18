using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Guards for QUICKLOAD-OVER-COMMITTED-RESTORE-OVERLAP-DELETES-TREE-ON-SAVE
    /// (found 2026-08-28 by the H39/H40 isolated censuses), which had two halves.
    ///
    /// <para><b>The invariant (fix B): a save must never DELETE a tree it cannot
    /// serialize.</b> <c>SaveTreeRecordings</c> skipped the <c>AddNode</c> for any tree
    /// with an unwritable recording. The skip condition is STICKY (a recording whose
    /// .prec is gone stays that way), so one unserializable recording permanently
    /// dropped a whole mission's metadata from persistent.sfs on that save and every
    /// save after it. The tree is now written from memory, and only an unwritable
    /// recording takes its last-known-good RECORDING node from the on-disk save (carrying
    /// the whole disk node froze every recording added since; todo
    /// SAVE-FREEZE-ONE-UNWRITABLE-SIDECAR).</para>
    ///
    /// <para><b>The trigger (fix C): the committed-overlap discard guard was answered
    /// against an empty store.</b> <c>DiscardPendingTree</c> refuses to delete sidecars
    /// whose recording id is still committed (#431) - the guard that makes the
    /// copy-on-write committed-tree restore (which clones a committed tree into the
    /// ACTIVE slot with ids PRESERVED) safe to throw away. On a COLD load
    /// <c>DiscardStalePendingState</c> runs BEFORE <c>LoadRecordingTrees</c>, so every
    /// id read as not-committed and the discard deleted the COMMITTED tree's sidecars.
    /// The save node's own committed ids now answer the guard in that window.</para>
    /// </summary>
    [Collection("Sequential")]
    public class UnserializableTreeCarryForwardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> cleanupFiles = new List<string>();
        private readonly List<string> cleanupRoots = new List<string>();
        private readonly string originalSaveFolder;
        private readonly GameScenes originalScene;

        public UnserializableTreeCarryForwardTests()
        {
            originalSaveFolder = HighLogic.SaveFolder;
            originalScene = HighLogic.LoadedScene;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            HighLogic.LoadedScene = GameScenes.SPACECENTER;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            HighLogic.LoadedScene = originalScene;
            ParsekScenario.PersistentSavePathOverrideForTesting = null;
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            RecordingStore.ClearDurableCommittedRecordingIdHint();
            for (int i = 0; i < cleanupFiles.Count; i++)
            {
                try { if (File.Exists(cleanupFiles[i])) File.Delete(cleanupFiles[i]); }
                catch { }
            }
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try { if (Directory.Exists(cleanupRoots[i])) Directory.Delete(cleanupRoots[i], true); }
                catch { }
            }
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ==================================================================
        // Fix B - the save-must-not-delete invariant
        // ==================================================================

        // THE INVARIANT, end to end through the real SaveTreeRecordings: a committed
        // tree whose recording cannot be written (SidecarLoadFailed + empty in-memory
        // state, the shape the bug-#585 guard refuses to clobber) keeps its node in the
        // written save, carried forward from the on-disk one.
        [Fact]
        public void SaveTreeRecordings_UnserializableTree_KeepsItsNodeFromTheOnDiskSave()
        {
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            PrepareSaveFolder("carry-forward");

            RecordingTree tree = MakeUnserializableTree("tree-x", "Mun Program", "rec-x");
            RecordingStore.AddCommittedTreeForTesting(tree);
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-x", "Mun Program", "rec-x"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode[] treeNodes = node.GetNodes("RECORDING_TREE");
            Assert.Single(treeNodes);
            Assert.Equal("tree-x", treeNodes[0].GetValue("id"));
            // Carried forward WITH its payload, not as a hollow stub.
            Assert.Equal("Mun Program", treeNodes[0].GetValue("treeName"));
            Assert.Single(treeNodes[0].GetNodes("RECORDING"));
            Assert.Equal("rec-x", treeNodes[0].GetNodes("RECORDING")[0].GetValue("recordingId"));
            // The disk node (recordingId only), not memory's.
            Assert.Null(treeNodes[0].GetNodes("RECORDING")[0].GetValue("vesselName"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("carried forward last-known-good"));
            // THE H5 REGRESSION (2026-09-06): rec-x is the ROOT and has no sidecar on disk
            // (it was never written - the exact shape of the synthetic corpus's 34
            // metadata-only tree recordings, and of any real recording whose .prec is
            // lost). Reconciliation used to require the sidecar, drop rec-x, refuse the
            // carry for its dangling root and delete the tree at Error. The tree now comes
            // from memory and rec-x takes its on-disk node, through the real predicate with
            // no testing seam: there is no sidecar override left to declare.
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
            Assert.DoesNotContain(logLines, l => l.Contains("resurrect deleted data"));
        }

        // Nothing on disk to carry forward (the tree was never saved, or the save was
        // copied without it). The tree used to be dropped from the save at Error; it is
        // now written from memory, the unwritable recording with its memory metadata.
        [Fact]
        public void SaveTreeRecordings_UnserializableTreeWithNoDiskNode_KeepsItFromMemory()
        {
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            PrepareSaveFolder("carry-forward-none");

            RecordingStore.AddCommittedTreeForTesting(
                MakeUnserializableTree("tree-y", "Minmus Program", "rec-y"));
            // On-disk save carries a DIFFERENT tree, so there is no node for tree-y.
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-other", "Other", "rec-other"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode written = Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.Equal("tree-y", written.GetValue("id"));
            ConfigNode rec = Assert.Single(written.GetNodes("RECORDING"));
            Assert.Equal("rec-y", rec.GetValue("recordingId"));
            Assert.Equal("Minmus Program Vessel", rec.GetValue("vesselName"));
            Assert.Single(logLines, l =>
                l.Contains("[WARN][Scenario]") && l.Contains("no last-known-good node on disk")
                && l.Contains("'Minmus Program'") && l.Contains("unwritable=[rec-y]")
                && l.Contains("spliced=0") && l.Contains("unwritableKeptFromMemory=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
        }

        // A tree that CAN be serialized is untouched by the new branch (no carry-forward,
        // no duplicate node).
        [Fact]
        public void SaveTreeRecordings_SerializableTree_WritesFromMemoryOnce()
        {
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            PrepareSaveFolder("carry-forward-healthy");

            RecordingStore.AddCommittedTreeForTesting(
                MakeUnserializableTree("tree-z", "Healthy", "rec-z"));
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-z", "Healthy", "rec-z"));

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.DoesNotContain(logLines, l => l.Contains("carried forward last-known-good"));
        }

        // catches: SAVE-FREEZE-ONE-UNWRITABLE-SIDECAR. The tree used to be carried forward
        // WHOLE from the on-disk save whenever one member could not be written, keeping
        // only the recordings that old node already listed. Anything added to the tree in
        // memory after the disk save (a Re-Fly fork, a continuation segment, a new branch)
        // was missing from this save and every later one while the fault lasted, and the
        // next cold load quarantined its sidecars. The tree is now written from memory and
        // only the unwritable member keeps its last-known-good RECORDING node.
        [Fact]
        public void SaveTreeRecordings_UnwritableMember_StillWritesRecordingsAddedSinceTheDiskSave()
        {
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            string recordingsDir = PrepareSaveFolder("carry-forward-new-member");
            RecordingPaths.SaveRootOverrideForTesting = Directory.GetParent(
                Directory.GetParent(recordingsDir).FullName).FullName;

            // rec-root: loaded with its .prec missing (the save was copied without its
            // Parsek folder), so it cannot be written. rec-new: flown in memory after the
            // disk save, healthy, linked to the root by a branch point.
            RecordingTree tree = MakeUnserializableTree("tree-f", "Duna Program", "rec-root");
            var fresh = new Recording
            {
                RecordingId = "rec-new",
                VesselName = "Duna Lander",
                TreeId = "tree-f",
                ParentBranchPointId = "bp-new",
            };
            fresh.Points.Add(new TrajectoryPoint { ut = 120.0, bodyName = "Kerbin" });
            fresh.Points.Add(new TrajectoryPoint { ut = 121.0, bodyName = "Kerbin" });
            fresh.MarkFilesDirty();
            tree.AddOrReplaceRecording(fresh);
            var bp = new BranchPoint { Id = "bp-new", UT = 115.0, Type = BranchPointType.Undock };
            bp.ParentRecordingIds.Add("rec-root");
            bp.ChildRecordingIds.Add("rec-new");
            tree.BranchPoints.Add(bp);
            RecordingStore.AddCommittedTreeForTesting(tree);

            // The on-disk save predates rec-new and holds rec-root's last-known-good node.
            var diskTree = new ConfigNode("RECORDING_TREE");
            diskTree.AddValue("id", "tree-f");
            diskTree.AddValue("treeName", "Duna Program");
            diskTree.AddValue("rootRecordingId", "rec-root");
            ConfigNode diskRoot = diskTree.AddNode("RECORDING");
            diskRoot.AddValue("recordingId", "rec-root");
            diskRoot.AddValue("vesselName", "Last Known Good");
            diskRoot.AddValue("sidecarEpoch", "3");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfsTree(diskTree);

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode written = Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.Equal("tree-f", written.GetValue("id"));
            ConfigNode[] recs = written.GetNodes("RECORDING");
            ConfigNode newRec = Array.Find(recs, r => r.GetValue("recordingId") == "rec-new");
            Assert.True(newRec != null, "the recording added after the disk save was dropped from the save");
            Assert.Equal("Duna Lander", newRec.GetValue("vesselName"));
            Assert.Equal("2", newRec.GetValue("pointCount"));
            Assert.True(File.Exists(Path.Combine(recordingsDir, "rec-new.prec")));
            // The unwritable member keeps its last-known-good node, which names the
            // sidecar epoch the files on disk actually carry.
            ConfigNode rootRec = Assert.Single(recs, r => r.GetValue("recordingId") == "rec-root");
            Assert.Equal("Last Known Good", rootRec.GetValue("vesselName"));
            Assert.Equal("3", rootRec.GetValue("sidecarEpoch"));
            Assert.Equal(2, recs.Length);
            ConfigNode writtenBp = Assert.Single(written.GetNodes("BRANCH_POINT"));
            Assert.Equal("bp-new", writtenBp.GetValue("id"));

            Assert.Single(logLines, l =>
                l.Contains("[WARN][Scenario]") && l.Contains("carried forward last-known-good")
                && l.Contains("'Duna Program'") && l.Contains("unwritable=[rec-root]")
                && l.Contains("fromMemory=1") && l.Contains("spliced=1"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
        }

        [Fact]
        public void FindCommittedTreeNodeById_MatchesCommitted_AndSkipsActiveOrPending()
        {
            var parsek = new ConfigNode("ParsekScenario");
            parsek.AddNode("RECORDING_TREE").AddValue("id", "committed-1");
            ConfigNode activeNode = parsek.AddNode("RECORDING_TREE");
            activeNode.AddValue("id", "parked-1");
            activeNode.AddValue("isActive", "True");
            ConfigNode pendingNode = parsek.AddNode("RECORDING_TREE");
            pendingNode.AddValue("id", "parked-2");
            pendingNode.AddValue("isPending", "True");

            Assert.NotNull(ParsekScenario.FindCommittedTreeNodeById(parsek, "committed-1"));
            // A parked node must never be carried forward as committed history.
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "parked-1"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "parked-2"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, "absent"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(null, "committed-1"));
            Assert.Null(ParsekScenario.FindCommittedTreeNodeById(parsek, null));
        }

        [Fact]
        public void Splice_NullOrEmptyInputs_ReturnZeroAndLeaveTheNodeAlone()
        {
            ConfigNode written = BuildTreeNode("tree-n", "rec-a");
            ConfigNode disk = BuildTreeNode("tree-n", "rec-a");
            var ids = new List<string> { "rec-a" };
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(null, disk, ids));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, null, ids));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, disk, null));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, disk, new List<string>()));
            Assert.Single(written.GetNodes("RECORDING"));
        }

        // Only the unwritable member takes its disk node; the others and their order stay
        // memory's.
        [Fact]
        public void Splice_ReplacesOnlyTheUnwritableMembers_AndKeepsOrder()
        {
            ConfigNode written = BuildTreeNode("tree-s", "rec-a", "rec-b", "rec-c");
            foreach (ConfigNode r in written.GetNodes("RECORDING"))
                r.AddValue("vesselName", "memory");
            written.AddNode("BRANCH_POINT").AddValue("id", "bp-1");
            ConfigNode disk = BuildTreeNode("tree-s", "rec-a", "rec-b");
            foreach (ConfigNode r in disk.GetNodes("RECORDING"))
                r.AddValue("vesselName", "disk");

            Assert.Equal(1, ParsekScenario.SpliceLastKnownGoodRecordingNodes(
                written, disk, new List<string> { "rec-b" }));

            ConfigNode[] recs = written.GetNodes("RECORDING");
            Assert.Equal(new[] { "rec-a", "rec-b", "rec-c" },
                Array.ConvertAll(recs, r => r.GetValue("recordingId")));
            Assert.Equal(new[] { "memory", "disk", "memory" },
                Array.ConvertAll(recs, r => r.GetValue("vesselName")));
            Assert.Single(written.GetNodes("BRANCH_POINT"));
        }

        // catches: THE RESURRECTION. The on-disk node is older than memory and can still
        // list a recording the player has since deleted. The splice walks memory's nodes,
        // so a disk-only id never comes back.
        [Fact]
        public void Splice_NeverAddsARecordingTheLiveTreeNoLongerHas()
        {
            ConfigNode written = BuildTreeNode("tree-r", "rec-live");
            ConfigNode disk = BuildTreeNode("tree-r", "rec-live", "rec-deleted");

            Assert.Equal(1, ParsekScenario.SpliceLastKnownGoodRecordingNodes(
                written, disk, new List<string> { "rec-live", "rec-deleted" }));

            ConfigNode recNode = Assert.Single(written.GetNodes("RECORDING"));
            Assert.Equal("rec-live", recNode.GetValue("recordingId"));
        }

        // An unwritable recording the disk save does not hold (flown since that save) keeps
        // memory's metadata rather than vanishing.
        [Fact]
        public void Splice_UnwritableMemberMissingFromDisk_KeepsMemorysNode()
        {
            ConfigNode written = BuildTreeNode("tree-m", "rec-old", "rec-new");
            ConfigNode disk = BuildTreeNode("tree-m", "rec-old");

            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(
                written, disk, new List<string> { "rec-new" }));

            Assert.Equal(new[] { "rec-old", "rec-new" },
                Array.ConvertAll(written.GetNodes("RECORDING"), r => r.GetValue("recordingId")));
        }


        // C1: the carry-forward source is ALWAYS the campaign persistent.sfs. The earlier
        // "target file name hint" was never set by any production call site, so this pins
        // the branch that actually ships.
        [Fact]
        public void CarryForwardSource_IsTheCampaignSave()
        {
            ParsekScenario.PersistentSavePathOverrideForTesting =
                WriteTempPersistentSfs(("tree-src", "Source", "rec-src"));

            ConfigNode parsek = ParsekScenario.TryLoadCarryForwardSourceParsekNode(out string sourcePath);

            Assert.NotNull(parsek);
            Assert.Equal(ParsekScenario.PersistentSavePathOverrideForTesting, sourcePath);
            Assert.Equal("tree-src", parsek.GetNodes("RECORDING_TREE")[0].GetValue("id"));
        }

        [Fact]
        public void CarryForwardSource_MissingFile_ReturnsNullAndNeverThrows()
        {
            ParsekScenario.PersistentSavePathOverrideForTesting = Path.Combine(
                Path.GetTempPath(), "parsek-absent-" + Guid.NewGuid().ToString("N") + ".sfs");
            Assert.Null(ParsekScenario.TryLoadCarryForwardSourceParsekNode(out _));
        }

        // ==================================================================
        // Fix C - the committed-overlap discard guard's durable input
        // ==================================================================

        // Finding 5: the parked (isActive / isPending) nodes' recordings MUST be in the
        // hint. TryRestoreActiveTreeNode / TryRestorePendingTreeNode restore exactly those
        // nodes later in the SAME OnLoad, so their sidecars are about to be hydrated -
        // deleting them in the discard window loses data the load was seconds from
        // bringing back. The tree actually being discarded is the in-memory stale pending
        // tree from a PREVIOUS save, which is not in this node at all.
        [Fact]
        public void CollectCommittedRecordingIds_IncludesParkedNodesAboutToBeRestored()
        {
            var parsek = new ConfigNode("ParsekScenario");
            ConfigNode committed = parsek.AddNode("RECORDING_TREE");
            committed.AddValue("id", "tree-a");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-1");
            committed.AddNode("RECORDING").AddValue("recordingId", "rec-2");

            ConfigNode active = parsek.AddNode("RECORDING_TREE");
            active.AddValue("id", "tree-b");
            active.AddValue("isActive", "True");
            active.AddNode("RECORDING").AddValue("recordingId", "rec-active");
            ConfigNode pending = parsek.AddNode("RECORDING_TREE");
            pending.AddValue("id", "tree-c");
            pending.AddValue("isPending", "True");
            pending.AddNode("RECORDING").AddValue("recordingId", "rec-pending");

            HashSet<string> ids = ParsekScenario.CollectCommittedRecordingIdsFromScenarioNode(parsek);

            Assert.Equal(4, ids.Count);
            Assert.Contains("rec-1", ids);
            Assert.Contains("rec-2", ids);
            Assert.Contains("rec-active", ids);
            Assert.Contains("rec-pending", ids);
            Assert.Empty(ParsekScenario.CollectCommittedRecordingIdsFromScenarioNode(null));
        }

        // The pure decision: an id committed in memory OR declared committed by the save
        // file keeps its events and sidecars. Only a pending-owned id may be destroyed.
        [Theory]
        [InlineData(false, false, false)] // pending-only -> purge/delete
        [InlineData(true, false, true)]   // committed in memory (the pre-existing #431 guard)
        [InlineData(false, true, true)]   // THE FIX: store not loaded yet, save says committed
        [InlineData(true, true, true)]
        public void ShouldPreserveCommittedOverlapOnPendingDiscard_Matrix(
            bool committedInMemory, bool durableHint, bool expected)
        {
            Assert.Equal(expected, TreeDiscardService.ShouldPreserveCommittedOverlapOnPendingDiscard(
                committedInMemory, durableHint));
        }

        [Fact]
        public void DurableCommittedRecordingIdHint_RoundTripsAndClears()
        {
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));

            RecordingStore.SetDurableCommittedRecordingIdHint(
                new HashSet<string>(StringComparer.Ordinal) { "rec-1", "rec-2" });
            Assert.True(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
            Assert.True(RecordingStore.IsDurableCommittedRecordingIdHint("rec-2"));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-3"));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint(null));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint(""));

            RecordingStore.ClearDurableCommittedRecordingIdHint();
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));

            // An empty set is stored as "no hint" so the guard cannot be armed vacuously.
            RecordingStore.SetDurableCommittedRecordingIdHint(new HashSet<string>(StringComparer.Ordinal));
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
            RecordingStore.SetDurableCommittedRecordingIdHint(null);
            Assert.False(RecordingStore.IsDurableCommittedRecordingIdHint("rec-1"));
        }

        // THE TRIGGER, at the decision level: a pending tree that is a copy-on-write
        // clone of a committed tree (ids PRESERVED) is discarded while the committed
        // store is still empty - exactly the cold-load ordering. With the save's ids
        // published, the clone's discard must route those ids to the PRESERVE branch
        // instead of the delete branch.
        //
        // The assertion is on the decision (the counters the discard logs), not on the
        // file system: DeleteRecordingFiles resolves its paths through
        // KSPUtil.ApplicationRootPath, which throws outside KSP, so a file-existence
        // assertion here would pass without the delete branch ever being reachable -
        // i.e. it would be vacuous rather than a guard.
        [Fact]
        public void DiscardPendingTree_ColdLoadOverlap_RoutesOverlapIdsToPreserveViaDurableHint()
        {
            PendingTreeSlot.StashPendingTree(
                MakeUnserializableTree("tree-overlap", "Overlap", "rec-overlap"),
                PendingTreeState.Finalized);
            Assert.Empty(RecordingStore.CommittedTrees); // cold load: trees not loaded yet

            logLines.Clear();
            RecordingStore.SetDurableCommittedRecordingIdHint(
                new HashSet<string>(StringComparer.Ordinal) { "rec-overlap" });
            try
            {
                TreeDiscardService.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);
            }
            finally
            {
                RecordingStore.ClearDurableCommittedRecordingIdHint();
            }

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("skipped deleting 1 recording sidecar set(s)")
                && l.Contains("viaDurableSaveHint=1"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("committed-overlap sidecars preserved by durable save hint"));
            // The destructive event/milestone purge is scoped the same way.
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("skipped destructive event/milestone purge for 1 committed-overlap")
                && l.Contains("viaDurableSaveHint=1"));
        }

        // The negative control: with no hint (and no committed store) the same discard
        // treats the id as pending-owned and takes the delete branch - the pre-fix
        // behaviour, and the data loss itself.
        [Fact]
        public void DiscardPendingTree_ColdLoadOverlap_WithoutHint_TakesTheDeleteBranch()
        {
            PendingTreeSlot.StashPendingTree(
                MakeUnserializableTree("tree-control", "Control", "rec-control"),
                PendingTreeState.Finalized);
            RecordingStore.ClearDurableCommittedRecordingIdHint();

            logLines.Clear();
            TreeDiscardService.DiscardPendingTree(preserveIrreversibleLiveGameplay: false);

            Assert.DoesNotContain(logLines, l => l.Contains("skipped deleting"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("committed-overlap sidecars preserved by durable save hint"));
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// A tree whose single recording is in the unserializable shape the bug-#585
        /// guard refuses to clobber: marked SidecarLoadFailed with completely empty
        /// in-memory state, so SaveRecordingFiles skips and the recording stays unsafe.
        /// </summary>
        private static RecordingTree MakeUnserializableTree(
            string treeId, string treeName, string recordingId)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeName,
                RootRecordingId = recordingId,
            };
            var rec = new Recording
            {
                RecordingId = recordingId,
                VesselName = treeName + " Vessel",
                TreeId = treeId,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 110.0,
            };
            RecordingSidecarStore.MarkSidecarLoadFailure(rec, "trajectory-missing");
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private string PrepareSaveFolder(string label)
        {
            string saveFolder = "parsek-test-carry-forward-" + label + "-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.Combine(Path.GetTempPath(), saveFolder);
            string recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            RecordingFileJanitor.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;
            cleanupRoots.Add(root);
            return recordingsDir;
        }

        private static ConfigNode BuildTreeNode(string treeId, params string[] recordingIds)
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", treeId);
            for (int i = 0; i < recordingIds.Length; i++)
                treeNode.AddNode("RECORDING").AddValue("recordingId", recordingIds[i]);
            return treeNode;
        }

        /// <summary>
        /// A minimal KSP-shaped .sfs holding one committed RECORDING_TREE with one
        /// RECORDING, behind a decoy SCENARIO so the finder must match by name.
        /// </summary>
        private string WriteTempPersistentSfsTree(ConfigNode treeNode)
        {
            var root = new ConfigNode();
            ConfigNode scenario = root.AddNode("GAME").AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            scenario.AddNode(treeNode);
            string path = Path.Combine(Path.GetTempPath(),
                "parsek-test-carry-sfs-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(path);
            cleanupFiles.Add(path);
            return path;
        }

        private string WriteTempPersistentSfs((string treeId, string treeName, string recordingId) tree)
        {
            var root = new ConfigNode();
            ConfigNode game = root.AddNode("GAME");
            game.AddNode("SCENARIO").AddValue("name", "ContractSystem"); // decoy
            ConfigNode scenario = game.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            ConfigNode treeNode = scenario.AddNode("RECORDING_TREE");
            treeNode.AddValue("id", tree.treeId);
            treeNode.AddValue("treeName", tree.treeName);
            treeNode.AddNode("RECORDING").AddValue("recordingId", tree.recordingId);

            string path = Path.Combine(Path.GetTempPath(),
                "parsek-test-carry-sfs-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(path);
            cleanupFiles.Add(path);
            return path;
        }
    }
}
