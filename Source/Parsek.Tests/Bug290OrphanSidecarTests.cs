using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly string originalSaveFolder;
        private readonly List<string> cleanupRoots = new List<string>();

        public Bug290_BuildKnownRecordingIdsTests()
        {
            originalSaveFolder = HighLogic.SaveFolder;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(cleanupRoots[i]))
                        Directory.Delete(cleanupRoots[i], true);
                }
                catch { }
            }
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
            Assert.Single(knownIds);
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
            Assert.Single(knownIds);
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

        [Fact]
        public void CleanOrphanFiles_DeletesTransientArtifacts_WithoutTouchingValidSidecars()
        {
            string recordingsDir = CreateRecordingsDir("transient-artifacts");
            var keep = new Recording { RecordingId = "keep-rec", VesselName = "Keep" };
            RecordingStore.AddRecordingWithTreeForTesting(keep);

            string keepPrec = Path.Combine(recordingsDir, "keep-rec.prec");
            string keepVessel = Path.Combine(recordingsDir, "keep-rec_vessel.craft");
            string keepGhost = Path.Combine(recordingsDir, "keep-rec_ghost.craft");
            string keepReadablePrec = Path.Combine(recordingsDir, "keep-rec.prec.txt");
            string keepReadableVessel = Path.Combine(recordingsDir, "keep-rec_vessel.craft.txt");
            string keepReadableGhost = Path.Combine(recordingsDir, "keep-rec_ghost.craft.txt");
            File.WriteAllText(keepPrec, "keep-prec");
            File.WriteAllText(keepVessel, "keep-vessel");
            File.WriteAllText(keepGhost, "keep-ghost");
            File.WriteAllText(keepReadablePrec, "keep-readable-prec");
            File.WriteAllText(keepReadableVessel, "keep-readable-vessel");
            File.WriteAllText(keepReadableGhost, "keep-readable-ghost");

            string transientStage = Path.Combine(recordingsDir, "orphan.prec.stage.1");
            string transientBak = Path.Combine(recordingsDir, "keep-rec_vessel.craft.bak.1");
            string transientTmp = Path.Combine(recordingsDir, "other-rec_ghost.craft.tmp");
            string transientReadableTmp = Path.Combine(recordingsDir, "other-rec_ghost.craft.txt.tmp");
            string orphanReadable = Path.Combine(recordingsDir, "orphan-readable.prec.txt");
            string readme = Path.Combine(recordingsDir, "readme.txt");
            File.WriteAllText(transientStage, "stage");
            File.WriteAllText(transientBak, "bak");
            File.WriteAllText(transientTmp, "tmp");
            File.WriteAllText(transientReadableTmp, "readable-tmp");
            File.WriteAllText(orphanReadable, "orphan-readable");
            File.WriteAllText(readme, "keep-me");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            Assert.True(File.Exists(keepPrec));
            Assert.True(File.Exists(keepVessel));
            Assert.True(File.Exists(keepGhost));
            Assert.True(File.Exists(keepReadablePrec));
            Assert.True(File.Exists(keepReadableVessel));
            Assert.True(File.Exists(keepReadableGhost));
            Assert.False(File.Exists(transientStage));
            Assert.False(File.Exists(transientBak));
            Assert.False(File.Exists(transientTmp));
            Assert.False(File.Exists(transientReadableTmp));
            Assert.False(File.Exists(orphanReadable));
            Assert.True(File.Exists(readme));
            Assert.Contains(logLines, l =>
                l.Contains("Cleaned 1 orphaned recording file(s), 4 transient sidecar artifact(s)"));
        }

        private string CreateRecordingsDir(string label)
        {
            string saveFolder = "parsek-test-" + label + "-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.GetFullPath(Path.Combine("saves", saveFolder));
            string recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;
            cleanupRoots.Add(root);
            return recordingsDir;
        }
    }
}
