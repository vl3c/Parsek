using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner RULE 1 ("Always keep them", finding H5): the settings the player owns on a
    /// committed recording (seal / stash through <see cref="Recording.MergeState"/>, hide,
    /// ghost on / off, loop, groups) survive a union or replace commit of a live copy of the
    /// tree. The live copy is a clone taken before the player changed them, so the committed
    /// side wins for those fields; the re-flown slot's own recordings keep the merge state the
    /// Re-Fly commit gives them.
    /// </summary>
    [Collection("Sequential")]
    public class TreeCommitPlayerFieldsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public TreeCommitPlayerFieldsTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            EffectiveState.ResetCachesForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_tcpf_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            RecordingPaths.SaveRootOverrideForTesting = tempDir;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = false;
            ParsekScenario.ResetInstanceForTesting();
            MilestoneStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }
            catch { }
        }

        private static Recording Rec(string id, MergeState state = MergeState.CommittedProvisional,
            double endUT = 100)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = "tree_1",
                VesselName = "V-" + id,
                MergeState = state,
                ExplicitStartUT = 0,
                ExplicitEndUT = endUT,
            };
        }

        /// <summary>The committed copy after the player sealed, hid, muted, looped and regrouped it.</summary>
        private static Recording PlayerEdited(string id)
        {
            var rec = Rec(id, MergeState.Immutable);
            rec.Hidden = true;
            rec.PlaybackEnabled = false;
            rec.LoopPlayback = true;
            rec.LoopIntervalSeconds = 1234;
            rec.LoopTimeUnit = LoopTimeUnit.Min;
            rec.LoopStartUT = 10;
            rec.LoopEndUT = 90;
            rec.LoopAnchorVesselId = 4242u;
            rec.LoopAnchorBodyName = "Mun";
            rec.RecordingGroups = new List<string> { "My Fleet" };
            return rec;
        }

        private static void AssertPlayerEdited(Recording rec)
        {
            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.True(rec.Hidden);
            Assert.False(rec.PlaybackEnabled);
            Assert.True(rec.LoopPlayback);
            Assert.Equal(1234, rec.LoopIntervalSeconds);
            Assert.Equal(LoopTimeUnit.Min, rec.LoopTimeUnit);
            Assert.Equal(10, rec.LoopStartUT);
            Assert.Equal(90, rec.LoopEndUT);
            Assert.Equal(4242u, rec.LoopAnchorVesselId);
            Assert.Equal("Mun", rec.LoopAnchorBodyName);
            Assert.Equal(new[] { "My Fleet" }, rec.RecordingGroups);
        }

        private static RecordingTree Tree(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Tree1",
                RootRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
                ActiveRecordingId = recs.Length > 0 ? recs[0].RecordingId : null,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var r in recs)
                tree.AddOrReplaceRecording(r);
            return tree;
        }

        private static void CommitExisting(RecordingTree tree)
        {
            foreach (var r in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(r);
            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        [Fact]
        public void ReFlyUnion_KeepsTheSealAndSettingsOfAnotherSlot_ReFlownSlotKeepsIncomingState()
        {
            // Committed tree: slot A's origin (being re-flown), slot B the player sealed,
            // hid, muted, looped and regrouped during the Re-Fly of A, and a debris piece
            // the live tree pruned (so the strict gate refuses and the union runs).
            var originA = Rec("rec_A");
            var slotB = PlayerEdited("rec_B");
            var debris = Rec("rec_debris", MergeState.Immutable);
            CommitExisting(Tree(originA, slotB, debris));

            // The live Re-Fly tree: clones taken at rewind time, before the edits.
            var liveA = Rec("rec_A", MergeState.NotCommitted);
            var liveB = Rec("rec_B");
            var fork = Rec("rec_fork", MergeState.NotCommitted);
            var incoming = Tree(liveA, liveB, fork);
            incoming.ActiveRecordingId = "rec_fork";

            ParsekScenario.SetInstanceForTesting(new ParsekScenario
            {
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_1",
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = "rec_fork",
                    OriginChildRecordingId = "rec_A",
                    SupersedeTargetId = "rec_A",
                    InPlaceContinuation = true,
                },
            });

            Assert.True(TreeCommitService.CommitTree(incoming));

            var canonical = RecordingStore.FindCommittedTreeById("tree_1");
            AssertPlayerEdited(canonical.Recordings["rec_B"]);
            Recording committedB = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_B");
            AssertPlayerEdited(committedB);
            // The re-flown slot's origin keeps the merge state the live tree carries.
            Assert.Equal(MergeState.NotCommitted, canonical.Recordings["rec_A"].MergeState);
            Assert.True(canonical.Recordings.ContainsKey("rec_debris"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("kept the player's settings")
                && l.Contains("rec=rec_B"));
        }

        [Fact]
        public void StrictReplace_KeepsThePlayersSettings()
        {
            var root = PlayerEdited("rec_root");
            CommitExisting(Tree(root));

            // A richer live copy (a new leg) replaces the committed tree.
            var liveRoot = Rec("rec_root", endUT: 200);
            var leg = Rec("rec_leg", endUT: 300);
            var incoming = Tree(liveRoot, leg);
            incoming.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_new",
                Type = BranchPointType.Undock,
                ParentRecordingIds = new List<string> { "rec_root" },
                ChildRecordingIds = new List<string> { "rec_leg" },
            });

            Assert.True(TreeCommitService.CommitTree(incoming));

            Recording committedRoot = RecordingStore.CommittedRecordings.Single(r => r.RecordingId == "rec_root");
            Assert.Same(liveRoot, committedRoot);
            AssertPlayerEdited(committedRoot);
            Assert.Equal(200, committedRoot.EndUT);
        }

        [Fact]
        public void UnionRecordingsInto_SharedId_KeepsThePlayersSettings()
        {
            // The merge journal's TreeMerge union swaps tree entries without a finalize.
            var existing = Tree(PlayerEdited("s"));
            var incomingS = Rec("s");
            var incoming = Tree(incomingS);

            TreeCommitService.UnionRecordingsInto(existing, incoming);

            Assert.Same(incomingS, existing.Recordings["s"]);
            AssertPlayerEdited(incomingS);
        }

        [Fact]
        public void UnionRecordingsInto_ReFlownSlotId_KeepsTheIncomingMergeState()
        {
            var existing = Tree(PlayerEdited("origin"));
            var incomingOrigin = Rec("origin", MergeState.NotCommitted);

            TreeCommitService.UnionRecordingsInto(existing, Tree(incomingOrigin),
                new HashSet<string> { "origin" });

            Assert.Equal(MergeState.NotCommitted, incomingOrigin.MergeState);
            Assert.True(incomingOrigin.Hidden);
            Assert.Equal(new[] { "My Fleet" }, incomingOrigin.RecordingGroups);
        }

        [Fact]
        public void PreservePlayerOwnedFields_CountsOnlyChangedFields_AndCopiesTheGroupList()
        {
            var existing = PlayerEdited("x");
            var incoming = Rec("x");

            int restored = TreeCommitService.PreservePlayerOwnedFieldsOnReplace(
                existing, incoming, keepIncomingMergeState: false, out string fields);

            Assert.Equal(11, restored);
            Assert.Contains("MergeState", fields);
            Assert.Contains("RecordingGroups", fields);
            AssertPlayerEdited(incoming);
            Assert.NotSame(existing.RecordingGroups, incoming.RecordingGroups);

            // A second pass finds nothing to restore.
            Assert.Equal(0, TreeCommitService.PreservePlayerOwnedFieldsOnReplace(
                existing, incoming, keepIncomingMergeState: false, out fields));
            Assert.Equal("", fields);
        }

        [Fact]
        public void PreservePlayerOwnedFields_NullSides_RestoreNothing()
        {
            Assert.Equal(0, TreeCommitService.PreservePlayerOwnedFieldsOnReplace(
                null, Rec("x"), false, out _));
            Assert.Equal(0, TreeCommitService.PreservePlayerOwnedFieldsOnReplace(
                Rec("x"), null, false, out _));
        }

        [Fact]
        public void ReFlownSlotRecordingIds_NamesTheMarkersIdsForItsTreeOnly()
        {
            var marker = new ReFlySessionMarker
            {
                TreeId = "tree_1",
                ActiveReFlyRecordingId = "fork",
                OriginChildRecordingId = "origin",
                SupersedeTargetId = "tip",
            };

            var ids = TreeCommitService.ReFlownSlotRecordingIds(marker, "tree_1");
            Assert.Equal(new[] { "fork", "origin", "tip" }, ids.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            Assert.Null(TreeCommitService.ReFlownSlotRecordingIds(marker, "tree_2"));
            Assert.Null(TreeCommitService.ReFlownSlotRecordingIds(null, "tree_1"));
        }
    }
}
