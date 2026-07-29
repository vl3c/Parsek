using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// End-to-end coverage for the schema-reject prune chain, plus direct unit
    /// cases for the prune itself.
    ///
    /// <para>The chain under test:</para>
    /// <list type="number">
    /// <item>a recording whose <c>recordingSchemaGeneration</c> is not the current
    /// generation fails <see cref="RecordingStore.IsRecordingSchemaCompatible"/>
    /// inside <c>RecordingTreeRecordCodec.LoadRecordingFrom</c>, which stamps
    /// <c>RecordingFormatVersion = -1</c>;</item>
    /// <item><see cref="RecordingTree.Load"/> drops that recording and collects its
    /// id into the rejected set;</item>
    /// <item><c>RecordingTree.PruneRejectedRecordingReferences</c> removes every
    /// reference to it (active-recording id, branch points that lose all parents or
    /// all children, and per-recording parent / parent-anchor / branch-point ids),
    /// or drops the WHOLE tree when the rejected recording was the root;</item>
    /// <item>the next save writes the tree without it, so a reload is a fixed
    /// point;</item>
    /// <item><see cref="RecordingStore.CleanOrphanFiles"/> later moves the dropped
    /// recording's now-unreferenced sidecars into the quarantine folder (never
    /// deletes them).</item>
    /// </list>
    ///
    /// <para><c>PruneRejectedRecordingReferences</c> is private; its ONLY entry
    /// point is <see cref="RecordingTree.Load"/>, so these tests drive it through
    /// that entry point over hand-built trees. The one piece it delegates to,
    /// <see cref="RecordingTree.ClearRejectedRecordingReferences"/>, is internal and
    /// is exercised directly at the bottom of this file.</para>
    ///
    /// <para>NOTE: the schema stamp lives in the <c>RECORDING</c> ConfigNode
    /// metadata, NOT in the <c>.prec</c> sidecar — the sidecar's own generation gate
    /// (<c>RecordingSidecarStore</c>) marks <c>SidecarLoadFailed</c> instead, which
    /// is a different, later drop path (<c>DropFailedSidecarHydrationRecordings</c>).
    /// The prune chain is driven by the metadata generation, so that is what these
    /// fixtures stamp.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SchemaRejectPruneChainTests : IDisposable
    {
        private const string RootId = "prune-root";
        private const string MidId = "prune-mid";
        private const string LeafId = "prune-leaf";
        private const string DebrisId = "prune-debris";
        private const string ChainId = "prune-chain";
        private const string RootMidBranchPointId = "bp-root-mid";
        private const string MidLeafBranchPointId = "bp-mid-leaf";
        private const string TreeId = "prune-tree";

        /// <summary>Any generation below <see cref="RecordingStore.CurrentRecordingSchemaGeneration"/>
        /// rejects with reason <c>generation-older</c>.</summary>
        private static readonly int OlderSchemaGeneration =
            RecordingStore.CurrentRecordingSchemaGeneration - 1;

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;
        private readonly string recordingsDir;

        public SchemaRejectPruneChainTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            // Every assertion below is on a Warn line; pin Verbose off so the sink
            // does not depend on whatever ParsekSettings a sibling test left loaded.
            ParsekLog.VerboseOverrideForTesting = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-schema-reject-prune-" + Guid.NewGuid().ToString("N"));
            recordingsDir = Path.Combine(tempDir, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        // -----------------------------------------------------------------
        // End-to-end: on-disk tree -> load -> prune -> save -> reload
        // -----------------------------------------------------------------

        // Fails if: a metadata-rejected mid-chain recording survives the load, or
        // any surviving recording / branch point keeps a reference to it. This is
        // the dangerous shape - the rejected id is BOTH the leaf's chain/branch
        // parent AND its parent anchor, and it sits between two branch points, so
        // a prune that forgets any one field leaves a dangling reference behind.
        [Fact]
        public void MidChainRecordingRejected_IsDroppedAndAllReferencesPruned()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            WriteSidecars(authored);
            string treePath = WriteTreeToDisk(authored, "tree-mid.cfg", MidId, OlderSchemaGeneration);

            logLines.Clear();
            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            // 1. The rejected recording is gone; the other two survive.
            Assert.Equal(2, loaded.Recordings.Count);
            Assert.True(loaded.Recordings.ContainsKey(RootId));
            Assert.True(loaded.Recordings.ContainsKey(LeafId));
            Assert.False(loaded.Recordings.ContainsKey(MidId));

            // 2. The tree root is untouched (the root was not the rejected one).
            Assert.Equal(RootId, loaded.RootRecordingId);

            // 3. Both branch points touched the rejected recording, so both lost
            //    all parents (bp-mid-leaf) or all children (bp-root-mid) and were
            //    removed.
            Assert.Empty(loaded.BranchPoints);

            // 4. Per-recording references are cleared on BOTH sides of the gap.
            Recording root = loaded.Recordings[RootId];
            Recording leaf = loaded.Recordings[LeafId];
            Assert.Null(root.ChildBranchPointId);
            Assert.Null(leaf.ParentRecordingId);
            Assert.Null(leaf.ParentAnchorRecordingId);
            Assert.Null(leaf.ParentBranchPointId);

            // 5. Nothing anywhere still names the pruned id or a removed branch point.
            AssertNoDanglingReferences(loaded, MidId, RootMidBranchPointId, MidLeafBranchPointId);

            // 6. Chain metadata is NOT rewritten by the prune - the chain simply has
            //    a hole at index 1. Contiguity repair is a separate, later step
            //    (ParsekScenario.RepairMissingContiguousChainPredecessors).
            Assert.Equal(ChainId, root.ChainId);
            Assert.Equal(ChainId, leaf.ChainId);
            Assert.Equal(0, root.ChainIndex);
            Assert.Equal(2, leaf.ChainIndex);

            // 7. Both the per-recording rejection and the prune summary are logged.
            Assert.Contains(logLines, l =>
                l.Contains("[Codec]") &&
                l.Contains("rejecting recording " + MidId) &&
                l.Contains("reason=generation-older"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("pruned references to 1 metadata-rejected recording(s)") &&
                l.Contains("removedBranchPoints=2"));
        }

        // Fails if: dropping the rejected recording also degrades the survivors'
        // bulk data. The prune is a metadata operation; the surviving recordings'
        // sidecars must still hydrate with their trajectory payload intact.
        [Fact]
        public void MidChainRecordingRejected_SurvivorsStillHydrateFromSidecars()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            WriteSidecars(authored);
            string treePath = WriteTreeToDisk(authored, "tree-hydrate.cfg", MidId, OlderSchemaGeneration);

            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            foreach (Recording rec in loaded.Recordings.Values)
            {
                Assert.True(
                    RecordingStore.LoadRecordingFilesFromPathsForTesting(
                        rec, PrecPath(rec.RecordingId), VesselPath(rec.RecordingId), GhostPath(rec.RecordingId)),
                    $"surviving recording '{rec.RecordingId}' failed to hydrate after the prune");
                Assert.False(rec.SidecarLoadFailed);
                Assert.True(rec.Points.Count > 0,
                    $"surviving recording '{rec.RecordingId}' hydrated with no trajectory points");
            }
        }

        // Fails if: the prune is not a fixed point. After the pruned tree is saved
        // back out, the next load must reject nothing and prune nothing - the
        // rejected RECORDING node is simply no longer written. A second-load
        // divergence here would mean the save wrote a reference the load then had
        // to repair again (the loop that silently rewrites history every save).
        [Fact]
        public void PrunedTree_SaveThenReload_IsStable()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            WriteSidecars(authored);
            string firstPath = WriteTreeToDisk(authored, "tree-stable-1.cfg", MidId, OlderSchemaGeneration);
            RecordingTree firstLoad = RecordingTree.Load(ConfigNode.Load(firstPath));

            // Save the pruned tree back out exactly as ParsekScenario would, with
            // no further stamping, then reload it.
            string secondPath = WriteTreeToDisk(firstLoad, "tree-stable-2.cfg", null, 0);

            logLines.Clear();
            RecordingTree secondLoad = RecordingTree.Load(ConfigNode.Load(secondPath));

            Assert.Equal(
                firstLoad.Recordings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                secondLoad.Recordings.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
            Assert.Equal(firstLoad.RootRecordingId, secondLoad.RootRecordingId);
            Assert.Equal(firstLoad.BranchPoints.Count, secondLoad.BranchPoints.Count);
            Assert.Empty(secondLoad.BranchPoints);
            Assert.Null(secondLoad.Recordings[RootId].ChildBranchPointId);
            Assert.Null(secondLoad.Recordings[LeafId].ParentRecordingId);
            Assert.Null(secondLoad.Recordings[LeafId].ParentAnchorRecordingId);
            Assert.Null(secondLoad.Recordings[LeafId].ParentBranchPointId);
            AssertNoDanglingReferences(secondLoad, MidId, RootMidBranchPointId, MidLeafBranchPointId);

            // Nothing was rejected and nothing was pruned on the second load.
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Codec]") && l.Contains("rejecting recording"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("pruned references to"));
        }

        // Fails if: the sidecars of a recording the load dropped stay in the active
        // set, or the survivors' sidecars are swept with it. Orphan cleanup must
        // QUARANTINE (never delete) recorded bulk data.
        [Fact]
        public void PrunedRecordingSidecars_AreQuarantinedByOrphanCleanup()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            WriteSidecars(authored);
            string treePath = WriteTreeToDisk(authored, "tree-orphan.cfg", MidId, OlderSchemaGeneration);
            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            // The store now knows only the two survivors, exactly as it would after
            // ParsekScenario.LoadRecordingTrees.
            RecordingStore.AddCommittedTreeForTesting(loaded);
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;

            Assert.True(File.Exists(PrecPath(MidId)));
            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            string quarantineDir = Path.Combine(recordingsDir, RecordingStore.OrphanQuarantineDirName);
            Assert.False(File.Exists(PrecPath(MidId)));
            Assert.True(File.Exists(Path.Combine(quarantineDir, MidId + ".prec")));

            Assert.True(File.Exists(PrecPath(RootId)));
            Assert.True(File.Exists(PrecPath(LeafId)));
            Assert.False(File.Exists(Path.Combine(quarantineDir, RootId + ".prec")));
            Assert.False(File.Exists(Path.Combine(quarantineDir, LeafId + ".prec")));
        }

        // -----------------------------------------------------------------
        // Prune shapes, driven through RecordingTree.Load over hand-built trees
        // -----------------------------------------------------------------

        // Fails if: rejecting a LEAF prunes more than it should. Only the branch
        // point that lost its single child goes; the upstream branch point still
        // has a live parent AND a live child and must survive intact.
        [Fact]
        public void LeafRecordingRejected_PrunesOnlyTheOrphanedBranchPoint()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            string treePath = WriteTreeToDisk(authored, "tree-leaf.cfg", LeafId, OlderSchemaGeneration);

            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Equal(2, loaded.Recordings.Count);
            Assert.False(loaded.Recordings.ContainsKey(LeafId));

            BranchPoint survivor = Assert.Single(loaded.BranchPoints);
            Assert.Equal(RootMidBranchPointId, survivor.Id);
            Assert.Equal(new[] { RootId }, survivor.ParentRecordingIds.ToArray());
            Assert.Equal(new[] { MidId }, survivor.ChildRecordingIds.ToArray());

            Recording root = loaded.Recordings[RootId];
            Recording mid = loaded.Recordings[MidId];
            Assert.Equal(RootMidBranchPointId, root.ChildBranchPointId);
            Assert.Equal(RootId, mid.ParentRecordingId);
            Assert.Equal(RootMidBranchPointId, mid.ParentBranchPointId);
            // The branch point that pointed at the rejected leaf is gone, so the
            // mid recording's downstream link is cleared and it becomes a leaf.
            Assert.Null(mid.ChildBranchPointId);
            AssertNoDanglingReferences(loaded, LeafId, MidLeafBranchPointId);
        }

        // Fails if: rejecting a recording that HAS children leaves those children
        // pointing at a recording that is no longer in the tree. Every child of the
        // rejected recording must lose its parent link, and the shared branch point
        // (which lost its only parent) must go with it.
        [Fact]
        public void RecordingWithChildrenRejected_ClearsEveryChildParentReference()
        {
            RecordingTree authored = BuildFanOutTree();
            string treePath = WriteTreeToDisk(authored, "tree-fanout.cfg", MidId, OlderSchemaGeneration);

            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Equal(3, loaded.Recordings.Count);
            Assert.False(loaded.Recordings.ContainsKey(MidId));
            Assert.Empty(loaded.BranchPoints);

            foreach (string childId in new[] { LeafId, DebrisId })
            {
                Recording child = loaded.Recordings[childId];
                Assert.Null(child.ParentRecordingId);
                Assert.Null(child.ParentBranchPointId);
                Assert.Null(child.ParentAnchorRecordingId);
            }

            Assert.Null(loaded.Recordings[RootId].ChildBranchPointId);
            AssertNoDanglingReferences(loaded, MidId, RootMidBranchPointId, MidLeafBranchPointId);
        }

        // Fails if: rejecting the ROOT leaves a headless tree behind. There is no
        // safe partial shape once the root is unloadable, so the whole tree is
        // dropped - recordings, branch points, background map and both id fields.
        [Fact]
        public void RootRecordingRejected_DropsEntireTree()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            authored.ActiveRecordingId = LeafId;
            string treePath = WriteTreeToDisk(authored, "tree-root.cfg", RootId, OlderSchemaGeneration);

            logLines.Clear();
            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Empty(loaded.Recordings);
            Assert.Empty(loaded.BranchPoints);
            Assert.Empty(loaded.RootRecordingId);
            Assert.Null(loaded.ActiveRecordingId);
            Assert.Empty(loaded.BackgroundMap);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("dropped entire tree id=" + TreeId) &&
                l.Contains("root recording '" + RootId + "' was rejected"));
        }

        // Fails if: a rejected ACTIVE recording leaves activeRecordingId pointing at
        // an id the tree no longer contains. Every later resolver that dereferences
        // ActiveRecordingId would then miss.
        [Fact]
        public void ActiveRecordingRejected_ClearsActiveRecordingId()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            authored.ActiveRecordingId = MidId;
            string treePath = WriteTreeToDisk(authored, "tree-active.cfg", MidId, OlderSchemaGeneration);

            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Null(loaded.ActiveRecordingId);
            Assert.Equal(2, loaded.Recordings.Count);
            AssertNoDanglingReferences(loaded, MidId, RootMidBranchPointId, MidLeafBranchPointId);
        }

        // Fails if: a newer-generation stamp is silently accepted. The gate rejects
        // in both directions, and the prune must run identically for either reason.
        [Fact]
        public void NewerGenerationRecording_IsRejectedAndPrunedToo()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            string treePath = WriteTreeToDisk(authored, "tree-newer.cfg", MidId,
                RecordingStore.CurrentRecordingSchemaGeneration + 1);

            logLines.Clear();
            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Equal(2, loaded.Recordings.Count);
            Assert.False(loaded.Recordings.ContainsKey(MidId));
            Assert.Contains(logLines, l =>
                l.Contains("[Codec]") &&
                l.Contains("rejecting recording " + MidId) &&
                l.Contains("reason=generation-newer"));
            AssertNoDanglingReferences(loaded, MidId, RootMidBranchPointId, MidLeafBranchPointId);
        }

        // Fails if: an all-current tree is disturbed by the prune path. With no
        // rejected ids the prune must return immediately and leave every branch
        // point and every reference exactly as authored.
        [Fact]
        public void NoRejectedRecordings_LeavesTreeUntouched()
        {
            RecordingTree authored = BuildThreeRecordingChainTree();
            string treePath = WriteTreeToDisk(authored, "tree-clean.cfg", null, 0);

            logLines.Clear();
            RecordingTree loaded = RecordingTree.Load(ConfigNode.Load(treePath));

            Assert.Equal(3, loaded.Recordings.Count);
            Assert.Equal(2, loaded.BranchPoints.Count);
            Assert.Equal(RootMidBranchPointId, loaded.Recordings[RootId].ChildBranchPointId);
            Assert.Equal(MidId, loaded.Recordings[LeafId].ParentRecordingId);
            Assert.Equal(MidId, loaded.Recordings[LeafId].ParentAnchorRecordingId);
            Assert.Equal(MidLeafBranchPointId, loaded.Recordings[LeafId].ParentBranchPointId);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[RecordingTree]") && l.Contains("pruned references to"));
        }

        // -----------------------------------------------------------------
        // ClearRejectedRecordingReferences — direct unit cases
        // -----------------------------------------------------------------

        // Fails if: the per-recording clear stops honouring any of the four fields
        // it owns, or starts clearing ids it was not given.
        [Fact]
        public void ClearRejectedRecordingReferences_ClearsOnlyMatchingIds()
        {
            var rec = new Recording
            {
                RecordingId = LeafId,
                ParentRecordingId = MidId,
                ParentAnchorRecordingId = MidId,
                ParentBranchPointId = MidLeafBranchPointId,
                ChildBranchPointId = "bp-still-live",
            };

            RecordingTree.ClearRejectedRecordingReferences(
                rec,
                new HashSet<string>(StringComparer.Ordinal) { MidId },
                new HashSet<string>(StringComparer.Ordinal) { MidLeafBranchPointId });

            Assert.Null(rec.ParentRecordingId);
            Assert.Null(rec.ParentAnchorRecordingId);
            Assert.Null(rec.ParentBranchPointId);
            Assert.Equal("bp-still-live", rec.ChildBranchPointId);
        }

        // Fails if: the recording-id clear is skipped when no branch point was
        // removed. The branch-point short-circuit must come AFTER the parent /
        // parent-anchor clears, never before them.
        [Fact]
        public void ClearRejectedRecordingReferences_NoRemovedBranchPoints_StillClearsParentIds()
        {
            var rec = new Recording
            {
                RecordingId = LeafId,
                ParentRecordingId = MidId,
                ParentAnchorRecordingId = MidId,
                ParentBranchPointId = MidLeafBranchPointId,
            };

            RecordingTree.ClearRejectedRecordingReferences(
                rec,
                new HashSet<string>(StringComparer.Ordinal) { MidId },
                removedBranchPointIds: null);

            Assert.Null(rec.ParentRecordingId);
            Assert.Null(rec.ParentAnchorRecordingId);
            // No branch point was removed, so the branch-point link is left alone.
            Assert.Equal(MidLeafBranchPointId, rec.ParentBranchPointId);
        }

        // Fails if: an unrelated recording is mutated. A recording that names no
        // rejected id and no removed branch point must come out byte-identical.
        [Fact]
        public void ClearRejectedRecordingReferences_UnrelatedRecording_IsUntouched()
        {
            var rec = new Recording
            {
                RecordingId = LeafId,
                ParentRecordingId = RootId,
                ParentAnchorRecordingId = RootId,
                ParentBranchPointId = RootMidBranchPointId,
                ChildBranchPointId = MidLeafBranchPointId,
            };

            RecordingTree.ClearRejectedRecordingReferences(
                rec,
                new HashSet<string>(StringComparer.Ordinal) { MidId },
                new HashSet<string>(StringComparer.Ordinal) { "bp-unrelated" });

            Assert.Equal(RootId, rec.ParentRecordingId);
            Assert.Equal(RootId, rec.ParentAnchorRecordingId);
            Assert.Equal(RootMidBranchPointId, rec.ParentBranchPointId);
            Assert.Equal(MidLeafBranchPointId, rec.ChildBranchPointId);
        }

        // Fails if: a null recording throws instead of no-opping. The prune walks
        // whatever the dictionary holds and must tolerate a null value.
        [Fact]
        public void ClearRejectedRecordingReferences_NullRecording_IsNoOp()
        {
            RecordingTree.ClearRejectedRecordingReferences(
                null,
                new HashSet<string>(StringComparer.Ordinal) { MidId },
                new HashSet<string>(StringComparer.Ordinal) { MidLeafBranchPointId });
        }

        // -----------------------------------------------------------------
        // Fixture helpers
        // -----------------------------------------------------------------

        /// <summary>
        /// root -[bp-root-mid]- mid -[bp-mid-leaf]- leaf, all three on one chain.
        /// The leaf names the mid as its parent recording, its parent anchor AND
        /// (through bp-mid-leaf) its branch parent, so rejecting the mid exercises
        /// every reference the prune has to clear.
        /// </summary>
        private RecordingTree BuildThreeRecordingChainTree()
        {
            Recording root = BuildRecording(RootId, "Prune Root", 1000.0, pid: 900001u);
            Recording mid = BuildRecording(MidId, "Prune Mid", 1100.0, pid: 900002u);
            Recording leaf = BuildRecording(LeafId, "Prune Leaf", 1200.0, pid: 900003u);

            root.ChainId = ChainId;
            root.ChainIndex = 0;
            root.ChildBranchPointId = RootMidBranchPointId;

            mid.ChainId = ChainId;
            mid.ChainIndex = 1;
            mid.ParentRecordingId = RootId;
            mid.ParentBranchPointId = RootMidBranchPointId;
            mid.ChildBranchPointId = MidLeafBranchPointId;

            leaf.ChainId = ChainId;
            leaf.ChainIndex = 2;
            leaf.ParentRecordingId = MidId;
            leaf.ParentAnchorRecordingId = MidId;
            leaf.ParentBranchPointId = MidLeafBranchPointId;

            RecordingTree tree = MakeTree(root, mid, leaf);
            tree.BranchPoints.Add(MakeBranchPoint(
                RootMidBranchPointId, 1100.0, new[] { RootId }, new[] { MidId }));
            tree.BranchPoints.Add(MakeBranchPoint(
                MidLeafBranchPointId, 1200.0, new[] { MidId }, new[] { LeafId }));
            return tree;
        }

        /// <summary>
        /// root -[bp-root-mid]- mid -[bp-mid-leaf]- {leaf, debris}: the mid has TWO
        /// children through one branch point, so rejecting it must clear both
        /// children's parent references, not just the first one walked.
        /// </summary>
        private RecordingTree BuildFanOutTree()
        {
            Recording root = BuildRecording(RootId, "Fanout Root", 2000.0, pid: 910001u);
            Recording mid = BuildRecording(MidId, "Fanout Mid", 2100.0, pid: 910002u);
            Recording leaf = BuildRecording(LeafId, "Fanout Leaf", 2200.0, pid: 910003u);
            Recording debris = BuildRecording(DebrisId, "Fanout Debris", 2200.0, pid: 910004u);

            root.ChildBranchPointId = RootMidBranchPointId;

            mid.ParentRecordingId = RootId;
            mid.ParentBranchPointId = RootMidBranchPointId;
            mid.ChildBranchPointId = MidLeafBranchPointId;

            leaf.ParentRecordingId = MidId;
            leaf.ParentAnchorRecordingId = MidId;
            leaf.ParentBranchPointId = MidLeafBranchPointId;

            debris.ParentRecordingId = MidId;
            debris.ParentAnchorRecordingId = MidId;
            debris.ParentBranchPointId = MidLeafBranchPointId;
            debris.IsDebris = true;

            RecordingTree tree = MakeTree(root, mid, leaf, debris);
            tree.BranchPoints.Add(MakeBranchPoint(
                RootMidBranchPointId, 2100.0, new[] { RootId }, new[] { MidId }));
            tree.BranchPoints.Add(MakeBranchPoint(
                MidLeafBranchPointId, 2200.0, new[] { MidId }, new[] { LeafId, DebrisId }));
            return tree;
        }

        private static RecordingTree MakeTree(params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Prune Chain Fixture",
                RootRecordingId = recordings[0].RecordingId,
            };
            for (int i = 0; i < recordings.Length; i++)
            {
                recordings[i].TreeId = TreeId;
                tree.AddOrReplaceRecording(recordings[i]);
            }
            return tree;
        }

        private static BranchPoint MakeBranchPoint(
            string id, double ut, string[] parentIds, string[] childIds)
        {
            return new BranchPoint
            {
                Id = id,
                UT = ut,
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string>(parentIds),
                ChildRecordingIds = new List<string>(childIds),
                SplitCause = "schema-reject-prune-fixture",
            };
        }

        private static Recording BuildRecording(string id, string vesselName, double t0, uint pid)
        {
            // Both snapshots so the recording writes the full sidecar set
            // (.prec + _vessel.craft + _ghost.craft) and hydrates through the
            // Separate ghost-snapshot path, the same shape the storage
            // round-trip fixtures use.
            var builder = new RecordingBuilder(vesselName)
                .WithRecordingId(id)
                .WithFormatVersion(RecordingStore.CurrentRecordingFormatVersion)
                .AddPoint(t0 + 0, -0.0972, -74.5575, 77.0)
                .AddPoint(t0 + 10, -0.0940, -74.5510, 900.0)
                .WithVesselSnapshot(
                    VesselSnapshotBuilder.ProbeShip(vesselName, pid)
                        .AsLanded(-0.0940, -74.5510, 77.0))
                .WithGhostVisualSnapshot(
                    VesselSnapshotBuilder.ProbeShip(vesselName + " Ghost", pid + 1u)
                        .AsLanded(-0.0940, -74.5510, 77.0));

            Recording rec = RecordingStorageFixtures.MaterializeTrajectory(builder);
            rec.VesselPersistentId = pid;
            rec.ExplicitStartUT = t0;
            rec.ExplicitEndUT = t0 + 10;
            return rec;
        }

        /// <summary>
        /// Serializes the tree the way <c>ParsekScenario</c> does, optionally
        /// stamping ONE recording's on-disk <c>recordingSchemaGeneration</c> to an
        /// incompatible value, then writes it to a real file. The stamp is applied
        /// on the SERIALIZED node so the fixture is independent of whether the codec
        /// writes the in-memory field or the current constant.
        /// </summary>
        private string WriteTreeToDisk(
            RecordingTree tree, string fileName, string rejectedRecordingId, int rejectedGeneration)
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);
            if (!string.IsNullOrEmpty(rejectedRecordingId))
                StampRecordingSchemaGeneration(treeNode, rejectedRecordingId, rejectedGeneration);

            string path = Path.Combine(tempDir, fileName);
            treeNode.Save(path);
            return path;
        }

        private static void StampRecordingSchemaGeneration(
            ConfigNode treeNode, string recordingId, int generation)
        {
            ConfigNode[] recNodes = treeNode.GetNodes("RECORDING");
            for (int i = 0; i < recNodes.Length; i++)
            {
                if (!string.Equals(recNodes[i].GetValue("recordingId"), recordingId, StringComparison.Ordinal))
                    continue;
                recNodes[i].SetValue("recordingSchemaGeneration",
                    generation.ToString(CultureInfo.InvariantCulture));
                return;
            }

            throw new InvalidOperationException(
                $"RECORDING node '{recordingId}' not found in the serialized tree");
        }

        private void WriteSidecars(RecordingTree tree)
        {
            foreach (Recording rec in tree.Recordings.Values)
            {
                Assert.True(
                    RecordingStore.SaveRecordingFilesToPathsForTesting(
                        rec,
                        PrecPath(rec.RecordingId),
                        VesselPath(rec.RecordingId),
                        GhostPath(rec.RecordingId),
                        incrementEpoch: false),
                    $"failed to write sidecars for '{rec.RecordingId}'");
            }
        }

        private string PrecPath(string id) => Path.Combine(recordingsDir, id + ".prec");

        private string VesselPath(string id) => Path.Combine(recordingsDir, id + "_vessel.craft");

        private string GhostPath(string id) => Path.Combine(recordingsDir, id + "_ghost.craft");

        /// <summary>
        /// Asserts that no surviving recording and no surviving branch point names
        /// <paramref name="prunedRecordingId"/> or any of
        /// <paramref name="removedBranchPointIds"/>.
        /// </summary>
        private static void AssertNoDanglingReferences(
            RecordingTree tree, string prunedRecordingId, params string[] removedBranchPointIds)
        {
            var liveBranchPointIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                BranchPoint bp = tree.BranchPoints[i];
                if (!string.IsNullOrEmpty(bp.Id))
                    liveBranchPointIds.Add(bp.Id);
                Assert.DoesNotContain(prunedRecordingId, bp.ParentRecordingIds);
                Assert.DoesNotContain(prunedRecordingId, bp.ChildRecordingIds);
            }

            for (int i = 0; i < removedBranchPointIds.Length; i++)
                Assert.DoesNotContain(removedBranchPointIds[i], liveBranchPointIds);

            foreach (Recording rec in tree.Recordings.Values)
            {
                Assert.NotEqual(prunedRecordingId, rec.ParentRecordingId);
                Assert.NotEqual(prunedRecordingId, rec.ParentAnchorRecordingId);

                // Every branch-point link a survivor still carries must resolve.
                if (!string.IsNullOrEmpty(rec.ParentBranchPointId))
                    Assert.Contains(rec.ParentBranchPointId, liveBranchPointIds);
                if (!string.IsNullOrEmpty(rec.ChildBranchPointId))
                    Assert.Contains(rec.ChildBranchPointId, liveBranchPointIds);
            }

            Assert.DoesNotContain(prunedRecordingId, tree.Recordings.Keys);
            Assert.False(
                string.Equals(tree.ActiveRecordingId, prunedRecordingId, StringComparison.Ordinal),
                "activeRecordingId still points at the pruned recording");
        }
    }
}
