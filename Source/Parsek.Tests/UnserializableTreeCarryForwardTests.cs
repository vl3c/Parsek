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
    /// SAVE-FREEZE-ON-MISSING-SIDECAR).</para>
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
            RecordingFileJanitor.CleanOrphanFilesDirectoryOverrideForTesting = null;
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
        // written save, with the unwritable recording's sidecar keys taken from disk.
        [Fact]
        public void SaveTreeRecordings_UnserializableTree_StaysInTheSave()
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
            Assert.Equal("Mun Program", treeNodes[0].GetValue("treeName"));
            ConfigNode rec = Assert.Single(treeNodes[0].GetNodes("RECORDING"));
            Assert.Equal("rec-x", rec.GetValue("recordingId"));
            // Memory's metadata, not a hollow stub.
            Assert.Equal("Mun Program Vessel", rec.GetValue("vesselName"));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN][Scenario]") && l.Contains("id=tree-x written from memory")
                && l.Contains("[rec-x]") && l.Contains("splicedFromDisk=1"));
            // THE H5 REGRESSION (2026-09-06): rec-x is the ROOT and has no sidecar on disk
            // (the shape of the synthetic corpus's metadata-only tree recordings, and of any
            // real recording whose .prec is lost). The tree must never be dropped at Error.
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
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
                l.Contains("[WARN][Scenario]") && l.Contains("'Minmus Program' id=tree-y written from memory")
                && l.Contains("[rec-y]") && l.Contains("splicedFromDisk=0")
                && l.Contains("unwritableWithoutDiskNode=1") && l.Contains("diskTreeNode=False"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
        }

        // A tree that CAN be serialized is untouched by the new branch (no carry-forward,
        // no duplicate node, no warning).
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
            Assert.DoesNotContain(logLines, l => l.Contains("unwritable recording(s)"));
        }

        // catches: SAVE-FREEZE-ON-MISSING-SIDECAR. The tree used to be carried forward
        // WHOLE from the on-disk save whenever one member could not be written, keeping
        // only the recordings that old node already listed, with their old metadata.
        // Anything added to the tree after the disk save (a Re-Fly fork, a continuation
        // segment, a new branch) and every edit to a healthy member was missing from this
        // save and every later one while the fault lasted. The rec-new and rec-b
        // assertions both failed on the pre-fix code.
        [Fact]
        public void SaveTreeRecordings_UnwritableMember_DoesNotFreezeTheRestOfTheTree()
        {
            string recordingsDir = BuildFrozenTreeScenario(out ConfigNode diskTree);
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfsTree(diskTree);

            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);

            ConfigNode written = Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.Equal("tree-f", written.GetValue("id"));
            ConfigNode[] recs = written.GetNodes("RECORDING");
            Assert.Equal(3, recs.Length);

            // A recording added after the disk save is written, sidecar included.
            ConfigNode newRec = Array.Find(recs, r => r.GetValue("recordingId") == "rec-new");
            Assert.True(newRec != null, "the recording added after the disk save was dropped from the save");
            Assert.Equal("Duna Lander", newRec.GetValue("vesselName"));
            Assert.Equal("2", newRec.GetValue("pointCount"));
            Assert.True(File.Exists(Path.Combine(recordingsDir, "rec-new.prec")));

            // An edit to a healthy member the disk save already held is written.
            ConfigNode healthy = Assert.Single(recs, r => r.GetValue("recordingId") == "rec-b");
            Assert.Equal("Renamed In Memory", healthy.GetValue("vesselName"));

            // The unwritable member: memory's metadata and topology, disk's sidecar keys.
            ConfigNode rootRec = Assert.Single(recs, r => r.GetValue("recordingId") == "rec-root");
            Assert.Equal("Duna Program Vessel", rootRec.GetValue("vesselName"));
            Assert.Equal("bp-new", rootRec.GetValue("childBranchPointId"));
            Assert.Equal("3", rootRec.GetValue("sidecarEpoch"));
            Assert.Equal("disk-prec-hash", rootRec.GetValue("precHash"));
            Assert.Equal("7", rootRec.GetValue("pointCount"));
            // Memory named a vessel hash the disk node does not: the disk's absence wins.
            Assert.Null(rootRec.GetValue("vesselHash"));
            Assert.Single(rootRec.GetValues("sidecarEpoch"));

            ConfigNode writtenBp = Assert.Single(written.GetNodes("BRANCH_POINT"));
            Assert.Equal("bp-new", writtenBp.GetValue("id"));

            Assert.Single(logLines, l =>
                l.Contains("[WARN][Scenario]") && l.Contains("'Duna Program' id=tree-f written from memory")
                && l.Contains("1 unwritable recording(s) [rec-root]") && l.Contains("recordings=3")
                && l.Contains("splicedFromDisk=1") && l.Contains("unwritableWithoutDiskNode=0"));
            Assert.Single(logLines, l =>
                l.Contains("[INFO][Scenario]") && l.Contains("1 committed tree(s) held 1 unwritable recording(s)")
                && l.Contains("1 kept their on-disk sidecar keys"));
            Assert.DoesNotContain(logLines, l => l.Contains("[ERROR]"));
        }

        // The mirror direction: the node the save wrote must load back as the same tree.
        // The spliced root still has no .prec, so it hydrates as trajectory-missing with
        // no points (the metadata-only shape the load keeps) and keeps its branch-point
        // link, so it does not read as a leaf; the next save takes the same path again.
        [Fact]
        public void SaveTreeRecordings_UnwritableMember_LoadsBackAsTheSameTree()
        {
            BuildFrozenTreeScenario(out ConfigNode diskTree);
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfsTree(diskTree);
            var node = new ConfigNode("ParsekScenario");
            ParsekScenario.SaveTreeRecordings(node);
            ConfigNode written = Assert.Single(node.GetNodes("RECORDING_TREE"));

            RecordingTree loaded = RecordingTree.Load(written);
            Assert.Equal(3, loaded.Recordings.Count);
            Recording root = loaded.Recordings["rec-root"];
            Assert.Equal("bp-new", root.ChildBranchPointId);
            Assert.Equal(3, root.SidecarEpoch);
            Assert.Equal("bp-new", loaded.Recordings["rec-new"].ParentBranchPointId);

            foreach (Recording rec in loaded.Recordings.Values)
                RecordingSidecarStore.LoadRecordingFiles(rec);
            Assert.True(root.SidecarLoadFailed);
            Assert.Equal("trajectory-missing", root.SidecarLoadFailureReason);
            Assert.True(ParsekScenario.IsSyntheticFixtureSidecarMarker(root));
            Assert.False(loaded.Recordings["rec-new"].SidecarLoadFailed);
            Assert.Equal(2, loaded.Recordings["rec-new"].Points.Count);

            Assert.Equal(0, ParsekScenario.DropFailedSidecarHydrationRecordings(loaded, "test"));
            Assert.Equal(3, loaded.Recordings.Count);
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
            written.GetNodes("RECORDING")[0].AddValue("sidecarEpoch", "9");
            ConfigNode disk = BuildTreeNode("tree-n", "rec-a");
            var ids = new List<string> { "rec-a" };
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(null, disk, ids));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, null, ids));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, disk, null));
            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(written, disk, new List<string>()));
            Assert.Equal("9", Assert.Single(written.GetNodes("RECORDING")).GetValue("sidecarEpoch"));
        }

        // Only the unwritable member takes disk values, and only its sidecar keys; every
        // other key, every other recording and the order stay memory's.
        [Fact]
        public void Splice_TakesOnlyTheSidecarKeysOfTheUnwritableMembers()
        {
            ConfigNode written = BuildTreeNode("tree-s", "rec-a", "rec-b", "rec-c");
            foreach (ConfigNode r in written.GetNodes("RECORDING"))
            {
                r.AddValue("vesselName", "memory");
                r.AddValue("sidecarEpoch", "5");
                r.AddValue("ghostHash", "memory-ghost");
            }
            written.AddNode("BRANCH_POINT").AddValue("id", "bp-1");
            ConfigNode disk = BuildTreeNode("tree-s", "rec-a", "rec-b");
            foreach (ConfigNode r in disk.GetNodes("RECORDING"))
            {
                r.AddValue("vesselName", "disk");
                r.AddValue("sidecarEpoch", "2");
                r.AddValue("ghostSnapshotMode", "Separate");
            }

            Assert.Equal(1, ParsekScenario.SpliceLastKnownGoodRecordingNodes(
                written, disk, new List<string> { "rec-b" }));

            ConfigNode[] recs = written.GetNodes("RECORDING");
            Assert.Equal(new[] { "rec-a", "rec-b", "rec-c" },
                Array.ConvertAll(recs, r => r.GetValue("recordingId")));
            Assert.Equal(new[] { "memory", "memory", "memory" },
                Array.ConvertAll(recs, r => r.GetValue("vesselName")));
            Assert.Equal(new[] { "5", "2", "5" },
                Array.ConvertAll(recs, r => r.GetValue("sidecarEpoch")));
            Assert.Equal(new[] { "memory-ghost", null, "memory-ghost" },
                Array.ConvertAll(recs, r => r.GetValue("ghostHash")));
            Assert.Equal(new[] { null, "Separate", null },
                Array.ConvertAll(recs, r => r.GetValue("ghostSnapshotMode")));
            Assert.Single(written.GetNodes("BRANCH_POINT"));
        }

        // The carried keys are ones the codec writes for the files: pin them to the codec
        // so a renamed key cannot silently stop being carried, and keep the topology and
        // metadata keys out.
        [Fact]
        public void CarriedSidecarIdentityKeys_AreKeysTheRecordingCodecWrites()
        {
            var rec = new Recording
            {
                RecordingId = "rec-keys",
                SidecarEpoch = 4,
                PrecHash = "p",
                VesselSnapshotHash = "v",
                GhostSnapshotHash = "g",
                GhostSnapshotMode = GhostSnapshotMode.Separate,
            };
            var recNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(recNode, rec);

            string[] keys = ParsekScenario.CarriedSidecarIdentityKeys();
            foreach (string key in keys)
                Assert.True(recNode.HasValue(key), "codec does not write carried key " + key);
            Assert.DoesNotContain("childBranchPointId", keys);
            Assert.DoesNotContain("parentBranchPointId", keys);
            Assert.DoesNotContain("vesselName", keys);
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
        // memory's node rather than vanishing.
        [Fact]
        public void Splice_UnwritableMemberMissingFromDisk_KeepsMemorysNode()
        {
            ConfigNode written = BuildTreeNode("tree-m", "rec-old", "rec-new");
            written.GetNodes("RECORDING")[1].AddValue("sidecarEpoch", "6");
            ConfigNode disk = BuildTreeNode("tree-m", "rec-old");

            Assert.Equal(0, ParsekScenario.SpliceLastKnownGoodRecordingNodes(
                written, disk, new List<string> { "rec-new" }));

            ConfigNode[] recs = written.GetNodes("RECORDING");
            Assert.Equal(new[] { "rec-old", "rec-new" },
                Array.ConvertAll(recs, r => r.GetValue("recordingId")));
            Assert.Equal("6", recs[1].GetValue("sidecarEpoch"));
        }

        [Fact]
        public void FormatUnwritableIds_ListsUpToTenAndCountsTheRest()
        {
            Assert.Equal("", ParsekScenario.FormatUnwritableIds(null));
            Assert.Equal("a,<none>", ParsekScenario.FormatUnwritableIds(new List<string> { "a", null }));
            var many = new List<string>();
            for (int i = 0; i < 12; i++)
                many.Add("r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("r0,r1,r2,r3,r4,r5,r6,r7,r8,r9,+2 more", ParsekScenario.FormatUnwritableIds(many));
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

        /// <summary>
        /// The frozen-tree shape: rec-root loaded with its .prec missing (unwritable, with a
        /// branch added under it since the disk save), rec-b healthy and renamed in memory
        /// since the disk save, rec-new flown after the disk save. Returns the recordings
        /// directory; <paramref name="diskTree"/> is the older on-disk tree node.
        /// </summary>
        private string BuildFrozenTreeScenario(out ConfigNode diskTree)
        {
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            string recordingsDir = PrepareSaveFolder("carry-forward-frozen");
            RecordingPaths.SaveRootOverrideForTesting = Directory.GetParent(
                Directory.GetParent(recordingsDir).FullName).FullName;

            RecordingTree tree = MakeUnserializableTree("tree-f", "Duna Program", "rec-root");
            Recording root = tree.Recordings["rec-root"];
            root.ChildBranchPointId = "bp-new";
            root.SidecarEpoch = 3;
            root.PrecHash = "disk-prec-hash";
            root.VesselSnapshotHash = "memory-only-vessel-hash";

            var healthy = new Recording
            {
                RecordingId = "rec-b",
                VesselName = "Renamed In Memory",
                TreeId = "tree-f",
            };
            healthy.Points.Add(new TrajectoryPoint { ut = 101.0, bodyName = "Kerbin" });
            healthy.Points.Add(new TrajectoryPoint { ut = 102.0, bodyName = "Kerbin" });
            healthy.MarkFilesDirty();
            tree.AddOrReplaceRecording(healthy);

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

            // The on-disk save predates rec-new, the branch and the rename.
            diskTree = new ConfigNode("RECORDING_TREE");
            diskTree.AddValue("id", "tree-f");
            diskTree.AddValue("treeName", "Duna Program");
            diskTree.AddValue("rootRecordingId", "rec-root");
            ConfigNode diskRoot = diskTree.AddNode("RECORDING");
            diskRoot.AddValue("recordingId", "rec-root");
            diskRoot.AddValue("vesselName", "Last Known Good");
            diskRoot.AddValue("sidecarEpoch", "3");
            diskRoot.AddValue("precHash", "disk-prec-hash");
            diskRoot.AddValue("pointCount", "7");
            ConfigNode diskB = diskTree.AddNode("RECORDING");
            diskB.AddValue("recordingId", "rec-b");
            diskB.AddValue("vesselName", "Old Name On Disk");
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
