using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #290 fix: CleanOrphanFiles must include pending tree
    /// recording IDs so that active-tree branch recordings (debris, EVA)
    /// are not deleted as orphans on cold-start resume.
    /// </summary>
    [Collection("Sequential")]
    public class Bug290_BuildKnownRecordingIdsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug290_BuildKnownRecordingIdsTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void IncludesCommittedRecordingIds()
        {
            var rec = new Recording { RecordingId = "committed-rec-1", VesselName = "Ship" };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            var knownIds = RecordingStore.BuildKnownRecordingIds();
            Assert.Contains("committed-rec-1", knownIds);
        }

        [Fact]
        public void IncludesPendingTreeRecordingIds()
        {
            // Simulate cold-start: TryRestoreActiveTreeNode stashes active tree
            // into pending slot BEFORE CleanOrphanFiles runs
            var tree = new RecordingTree
            {
                Id = "active-tree",
                TreeName = "KerbalX",
                ActiveRecordingId = "active-rec"
            };
            tree.Recordings["active-rec"] = new Recording
            {
                RecordingId = "active-rec",
                VesselName = "Kerbal X",
                TreeId = "active-tree"
            };
            tree.Recordings["debris-rec"] = new Recording
            {
                RecordingId = "debris-rec",
                VesselName = "Kerbal X Debris",
                TreeId = "active-tree"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var knownIds = RecordingStore.BuildKnownRecordingIds();

            Assert.Contains("active-rec", knownIds);
            Assert.Contains("debris-rec", knownIds);
        }

        [Fact]
        public void PendingTreeIds_NotTreatedAsOrphans()
        {
            // The core bug: without the fix, these IDs would be missing from
            // the known set and their sidecar files deleted as orphans
            var tree = new RecordingTree
            {
                Id = "tree-1",
                TreeName = "Rocket",
                ActiveRecordingId = "rec-root"
            };
            tree.Recordings["rec-root"] = new Recording
            {
                RecordingId = "rec-root",
                VesselName = "Rocket",
                TreeId = "tree-1"
            };
            tree.Recordings["rec-booster"] = new Recording
            {
                RecordingId = "rec-booster",
                VesselName = "Rocket Debris",
                TreeId = "tree-1"
            };
            tree.Recordings["rec-eva"] = new Recording
            {
                RecordingId = "rec-eva",
                VesselName = "Jeb Kerman",
                TreeId = "tree-1"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var knownIds = RecordingStore.BuildKnownRecordingIds();

            // All three should be known (not orphaned)
            Assert.Equal(3, knownIds.Count);
            Assert.Contains("rec-root", knownIds);
            Assert.Contains("rec-booster", knownIds);
            Assert.Contains("rec-eva", knownIds);
        }

        [Fact]
        public void CombinesCommittedAndPendingIds()
        {
            // Committed tree from a previous flight
            var oldRec = new Recording { RecordingId = "old-committed", VesselName = "OldShip" };
            RecordingStore.AddRecordingWithTreeForTesting(oldRec);

            // Active tree stashed as pending (current flight, cold-start resume)
            var activeTree = new RecordingTree
            {
                Id = "active-tree",
                TreeName = "NewRocket"
            };
            activeTree.Recordings["new-active"] = new Recording
            {
                RecordingId = "new-active",
                VesselName = "NewRocket",
                TreeId = "active-tree"
            };
            RecordingStore.StashPendingTree(activeTree, PendingTreeState.Limbo);

            var knownIds = RecordingStore.BuildKnownRecordingIds();

            Assert.Contains("old-committed", knownIds);
            Assert.Contains("new-active", knownIds);
        }

        [Fact]
        public void NoPendingTree_OnlyReturnsCommitted()
        {
            var rec = new Recording { RecordingId = "only-committed", VesselName = "Ship" };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // No pending tree
            Assert.False(RecordingStore.HasPendingTree);

            var knownIds = RecordingStore.BuildKnownRecordingIds();

            Assert.Contains("only-committed", knownIds);
            Assert.Equal(1, knownIds.Count);
        }

        [Fact]
        public void EmptyStore_ReturnsEmptySet()
        {
            var knownIds = RecordingStore.BuildKnownRecordingIds();
            Assert.Empty(knownIds);
        }

        [Fact]
        public void SkipsNullOrEmptyRecordingIds()
        {
            var tree = new RecordingTree
            {
                Id = "tree-with-gaps",
                TreeName = "Test"
            };
            tree.Recordings["valid"] = new Recording
            {
                RecordingId = "valid-id",
                VesselName = "Ship",
                TreeId = "tree-with-gaps"
            };
            tree.Recordings["empty"] = new Recording
            {
                RecordingId = "",
                VesselName = "Empty",
                TreeId = "tree-with-gaps"
            };
            tree.Recordings["null"] = new Recording
            {
                RecordingId = null,
                VesselName = "Null",
                TreeId = "tree-with-gaps"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            var knownIds = RecordingStore.BuildKnownRecordingIds();

            Assert.Contains("valid-id", knownIds);
            Assert.DoesNotContain("", knownIds);
        }

        [Fact]
        public void DeduplicatesCommittedRecordingsAndTrees()
        {
            // After T56, all committed recordings are tree recordings, so the
            // flat committedRecordings list and committedTrees overlap.
            // BuildKnownRecordingIds must deduplicate via HashSet.
            var rec = new Recording { RecordingId = "dup-id", VesselName = "Ship" };
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            // rec is now in both committedRecordings AND committedTrees
            var knownIds = RecordingStore.BuildKnownRecordingIds();

            Assert.Contains("dup-id", knownIds);
            // Count should be 1, not 2 — HashSet deduplicates
            Assert.Equal(1, knownIds.Count);
        }

        [Fact]
        public void OutOverload_ReportsPendingTreeCount()
        {
            var tree = new RecordingTree
            {
                Id = "tree-out",
                TreeName = "TestRocket"
            };
            tree.Recordings["a"] = new Recording
            {
                RecordingId = "rec-a",
                VesselName = "Root",
                TreeId = "tree-out"
            };
            tree.Recordings["b"] = new Recording
            {
                RecordingId = "rec-b",
                VesselName = "Debris",
                TreeId = "tree-out"
            };
            // Add one with empty ID to verify it's not counted
            tree.Recordings["c"] = new Recording
            {
                RecordingId = "",
                VesselName = "Empty",
                TreeId = "tree-out"
            };
            RecordingStore.StashPendingTree(tree, PendingTreeState.Limbo);

            RecordingStore.BuildKnownRecordingIds(out int pendingCount);

            // Only 2 valid IDs, not 3 (empty ID filtered)
            Assert.Equal(2, pendingCount);
        }
    }
}
