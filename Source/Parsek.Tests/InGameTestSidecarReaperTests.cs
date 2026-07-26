using System;
using System.Collections.Generic;
using System.IO;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Unit tests for <see cref="InGameTestSidecarReaper"/>, the shared helper that
    /// deletes the orphan sidecars an in-game test leaves on disk after it drives a
    /// REAL recording-pipeline write (<c>RecordingStore.CommitTree</c>'s per-recording
    /// <c>SaveRecordingFiles</c> flush, or <c>RunOptimizationPass</c>'s dirty-file
    /// flush over synthetic + split-half recordings).
    ///
    /// <para>The in-game tests themselves run inside KSP's Unity play mode and write
    /// real <c>saves/&lt;save&gt;/Parsek/Recordings/</c> files; the xUnit harness can
    /// only exercise the reaper against a temp directory. These tests verify it
    /// deletes every sidecar suffix the recording pipeline writes (mirroring
    /// <c>RecordingPaths</c>) for the supplied id set, leaves unrelated files alone,
    /// PRESERVES any id the store still knows about (the known-ids guard), rejects
    /// path-traversal ids, and emits the expected INFO summary.</para>
    /// </summary>
    [Collection("Sequential")]
    public class InGameTestSidecarReaperTests : IDisposable
    {
        private const string Ctx = "UnitTest";

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        // Every suffix the recording pipeline writes (RecordingPaths.Build* + the
        // legacy .pcrf). The reaper must delete all of these.
        private static readonly string[] AllSuffixes =
        {
            ".prec",
            ".prec.txt",
            ".pann",
            "_vessel.craft",
            "_vessel.craft.txt",
            "_ghost.craft",
            "_ghost.craft.txt",
            ".pcrf",
        };

        public InGameTestSidecarReaperTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek-cleanup-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch { }
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private void WriteSidecarsFor(string id)
        {
            for (int i = 0; i < AllSuffixes.Length; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, id + AllSuffixes[i]), "stub");
            }
        }

        [Fact]
        public void Deletes_AllKnownSuffixes_ForSyntheticIds()
        {
            const string syntheticA = "rec_persistence_smoke_ascent_reentry";
            const string syntheticB = "rec_split_half_random_xyz";
            WriteSidecarsFor(syntheticA);
            WriteSidecarsFor(syntheticB);

            // Sanity: every file is on disk before the reap.
            foreach (var suffix in AllSuffixes)
            {
                Assert.True(File.Exists(Path.Combine(tempDir, syntheticA + suffix)),
                    $"setup: missing {syntheticA}{suffix}");
                Assert.True(File.Exists(Path.Combine(tempDir, syntheticB + suffix)),
                    $"setup: missing {syntheticB}{suffix}");
            }

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { syntheticA, syntheticB }, Ctx);

            // Two ids x eight suffixes.
            Assert.Equal(AllSuffixes.Length * 2, deleted);

            // All sidecars for the two ids are gone.
            foreach (var suffix in AllSuffixes)
            {
                Assert.False(File.Exists(Path.Combine(tempDir, syntheticA + suffix)),
                    $"after reap: {syntheticA}{suffix} should be gone");
                Assert.False(File.Exists(Path.Combine(tempDir, syntheticB + suffix)),
                    $"after reap: {syntheticB}{suffix} should be gone");
            }

            // INFO summary line emitted, carrying the caller's context tag.
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][TestRunner]") &&
                l.Contains("UnitTest sidecar reap") &&
                l.Contains("deleted 16 sidecar file(s)") &&
                l.Contains("for 2 synthetic recording id(s)") &&
                l.Contains("preservedKnown=0"));
        }

        [Fact]
        public void LeavesUnrelatedFilesAlone()
        {
            const string synthetic = "rec_persistence_smoke_grazing";
            const string playerLive = "rec_player_live_dont_touch";
            WriteSidecarsFor(synthetic);
            WriteSidecarsFor(playerLive);
            // Also a wholly unrelated file in the same dir.
            string readme = Path.Combine(tempDir, "readme.txt");
            File.WriteAllText(readme, "keep me");

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { synthetic }, Ctx);

            Assert.Equal(AllSuffixes.Length, deleted);

            foreach (var suffix in AllSuffixes)
            {
                Assert.False(File.Exists(Path.Combine(tempDir, synthetic + suffix)),
                    $"synthetic {synthetic}{suffix} should be deleted");
                Assert.True(File.Exists(Path.Combine(tempDir, playerLive + suffix)),
                    $"unrelated {playerLive}{suffix} must survive");
            }
            Assert.True(File.Exists(readme), "readme.txt must survive");
        }

        // ---- Known-ids guard (the S0.5-class safety property) --------------------

        [Fact]
        public void PreservesIds_StillKnownToTheStore()
        {
            // The shape this guards: a test's reap list overlaps a recording the store
            // still owns (an id collision, or a reap that ran before the in-memory
            // removal). Those files are real mission data and must survive.
            const string stillOwned = "rec_live_committed";
            const string testResidue = "rec_synthetic_residue";
            WriteSidecarsFor(stillOwned);
            WriteSidecarsFor(testResidue);

            var known = new HashSet<string>(StringComparer.Ordinal) { stillOwned };

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { stillOwned, testResidue }, Ctx, known);

            // Only the unowned id's files were unlinked.
            Assert.Equal(AllSuffixes.Length, deleted);
            foreach (var suffix in AllSuffixes)
            {
                Assert.True(File.Exists(Path.Combine(tempDir, stillOwned + suffix)),
                    $"store-owned {stillOwned}{suffix} must survive the reap");
                Assert.False(File.Exists(Path.Combine(tempDir, testResidue + suffix)),
                    $"residue {testResidue}{suffix} should be reaped");
            }

            // The preservation is counted and named, never silent.
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][TestRunner]") &&
                l.Contains("preserving 'rec_live_committed'") &&
                l.Contains("still known to RecordingStore"));
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][TestRunner]") &&
                l.Contains("for 1 synthetic recording id(s)") &&
                l.Contains("preservedKnown=1"));
        }

        [Fact]
        public void NullKnownSet_ReapsEverything()
        {
            // A directory-scoped unit test (or any caller with no store context) passes
            // null, which reads as "the store knows nothing" - no id is preserved.
            const string id = "rec_no_store_context";
            WriteSidecarsFor(id);

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { id }, Ctx, null);

            Assert.Equal(AllSuffixes.Length, deleted);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][TestRunner]") && l.Contains("preservedKnown=0"));
        }

        [Fact]
        public void PreservesEveryId_WhenAllAreStillKnown()
        {
            // Degenerate case: a reap issued BEFORE the in-memory removal deletes
            // NOTHING rather than destroying the tree it was about to drop.
            const string a = "rec_owned_a";
            const string b = "rec_owned_b";
            WriteSidecarsFor(a);
            WriteSidecarsFor(b);

            var known = new HashSet<string>(StringComparer.Ordinal) { a, b };

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { a, b }, Ctx, known);

            Assert.Equal(0, deleted);
            foreach (var suffix in AllSuffixes)
            {
                Assert.True(File.Exists(Path.Combine(tempDir, a + suffix)));
                Assert.True(File.Exists(Path.Combine(tempDir, b + suffix)));
            }
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][TestRunner]") &&
                l.Contains("deleted 0 sidecar file(s)") &&
                l.Contains("for 0 synthetic recording id(s)") &&
                l.Contains("preservedKnown=2"));
        }

        // ---- Degenerate inputs ---------------------------------------------------

        [Fact]
        public void NoOp_WhenIdHasNoSidecarsOnDisk()
        {
            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { "rec_never_flushed" }, Ctx);

            Assert.Equal(0, deleted);
            Assert.Contains(logLines, l =>
                l.Contains("[INFO][TestRunner]") &&
                l.Contains("deleted 0 sidecar file(s)") &&
                l.Contains("for 1 synthetic recording id(s)"));
        }

        [Fact]
        public void NoOp_WhenDirectoryDoesNotExist()
        {
            string missing = Path.Combine(Path.GetTempPath(),
                "parsek-cleanup-missing-" + Guid.NewGuid().ToString("N"));

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                missing, new[] { "rec_anything" }, Ctx);

            Assert.Equal(0, deleted);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][TestRunner]") &&
                l.Contains("does not exist"));
        }

        [Fact]
        public void NoOp_WhenIdCollectionIsNull()
        {
            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, null, Ctx);

            Assert.Equal(0, deleted);
            Assert.Contains(logLines, l =>
                l.Contains("[VERBOSE][TestRunner]") &&
                l.Contains("null id collection"));
        }

        [Fact]
        public void RejectsPathTraversalIds()
        {
            // Set up a sentinel file *outside* the recordings dir that an attacker
            // could try to delete via "../sentinel".
            string outsideDir = Path.Combine(Path.GetTempPath(),
                "parsek-cleanup-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideDir);
            string sentinel = Path.Combine(outsideDir, "sentinel.prec");
            File.WriteAllText(sentinel, "must survive");

            try
            {
                int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                    tempDir, new[] { "../sentinel", "..\\sentinel", "valid_id" }, Ctx);

                // No deletes happen - the two traversal ids are rejected by
                // ValidateRecordingId, the third has no files on disk.
                Assert.Equal(0, deleted);
                Assert.True(File.Exists(sentinel), "sentinel outside recordings dir must survive");

                // INFO summary reports only one valid id (the two traversal ids
                // failed validation and never reached the per-suffix loop).
                Assert.Contains(logLines, l =>
                    l.Contains("[INFO][TestRunner]") &&
                    l.Contains("for 1 synthetic recording id(s)") &&
                    l.Contains("invalidId=2"));
            }
            finally
            {
                try { Directory.Delete(outsideDir, true); } catch { }
            }
        }

        [Fact]
        public void SkipsEmptyAndNullIdsInCollection()
        {
            const string realId = "rec_real";
            WriteSidecarsFor(realId);

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { null, "", realId, "   " }, Ctx);

            // Only realId actually deletes anything; null/empty are skipped.
            // "   " (whitespace) goes through ValidateRecordingId - the trailing
            // spaces are valid filename chars on most platforms but no sidecar
            // file matches, so 0 deletes for it.
            Assert.Equal(AllSuffixes.Length, deleted);
            foreach (var suffix in AllSuffixes)
            {
                Assert.False(File.Exists(Path.Combine(tempDir, realId + suffix)));
            }
        }

        [Fact]
        public void SuffixListMatches_RecordingPipeline()
        {
            // Belt-and-braces: confirm the reaper's suffix coverage matches
            // what RecordingPaths exposes builders for (plus the legacy .pcrf).
            // If a new sidecar suffix is added to RecordingPaths, this test starts
            // failing - forcing the reaper's list to be updated.
            const string id = "test_id";

            string[] expectedFromPaths =
            {
                Path.GetFileName(RecordingPaths.BuildTrajectoryRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildAnnotationsRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildVesselSnapshotRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildGhostSnapshotRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildReadableTrajectoryMirrorRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildReadableVesselSnapshotMirrorRelativePath(id)),
                Path.GetFileName(RecordingPaths.BuildReadableGhostSnapshotMirrorRelativePath(id)),
            };

            // Write each pipeline-known sidecar; the reaper must delete them all.
            for (int i = 0; i < expectedFromPaths.Length; i++)
            {
                File.WriteAllText(Path.Combine(tempDir, expectedFromPaths[i]), "stub");
            }

            int deleted = InGameTestSidecarReaper.DeleteSidecarsForIdsIn(
                tempDir, new[] { id }, Ctx);

            Assert.Equal(expectedFromPaths.Length, deleted);
            for (int i = 0; i < expectedFromPaths.Length; i++)
            {
                Assert.False(File.Exists(Path.Combine(tempDir, expectedFromPaths[i])),
                    $"pipeline-known sidecar {expectedFromPaths[i]} must be deleted");
            }
        }
    }
}
