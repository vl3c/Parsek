using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for <see cref="FileIOUtils.DeleteSaveSidecarLoadMeta"/>: the orphan-cleanup
    /// helper called after the rewind / career-start / rewind-point capture sites move a
    /// stock <c>.sfs</c> into a Parsek subdirectory, leaving the <c>.loadmeta</c> sidecar
    /// behind in the save root.
    /// </summary>
    [Collection("Sequential")]
    public class FileIOUtilsLoadMetaTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public FileIOUtilsLoadMetaTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_loadmeta_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        private string WriteFile(string name)
        {
            string path = Path.Combine(tempDir, name);
            File.WriteAllText(path, "stub");
            return path;
        }

        [Fact]
        public void DeletesOrphanedLoadMeta_LeavesOtherFilesAlone()
        {
            string loadMeta = WriteFile("parsek_rw_abc123.loadmeta");
            // A sibling .sfs that would normally have been moved out already, plus the
            // legitimate stock saves that must never be touched.
            string persistentMeta = WriteFile("persistent.loadmeta");
            string quicksaveMeta = WriteFile("quicksave.loadmeta");

            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, "parsek_rw_abc123", "Recorder");

            Assert.False(File.Exists(loadMeta), "orphaned .loadmeta should be deleted");
            Assert.True(File.Exists(persistentMeta), "stock persistent.loadmeta must survive");
            Assert.True(File.Exists(quicksaveMeta), "stock quicksave.loadmeta must survive");
        }

        [Fact]
        public void LogsVerbose_WhenSidecarDeleted()
        {
            WriteFile("parsek_career_start.loadmeta");
            logLines.Clear();

            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, "parsek_career_start", "CareerStart");

            Assert.Contains(logLines, l => l.Contains("[CareerStart]")
                && l.Contains("Deleted orphaned save sidecar")
                && l.Contains("parsek_career_start.loadmeta"));
        }

        [Fact]
        public void NoOp_WhenSidecarMissing()
        {
            logLines.Clear();

            // No file on disk: must not throw, must not log a deletion.
            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, "parsek_rw_missing", "Recorder");

            Assert.DoesNotContain(logLines, l => l.Contains("Deleted orphaned save sidecar"));
            Assert.DoesNotContain(logLines, l => l.Contains("Failed to delete orphaned save sidecar"));
        }

        [Fact]
        public void NoOp_WhenSavesDirNullOrEmpty()
        {
            FileIOUtils.DeleteSaveSidecarLoadMeta(null, "parsek_rw_x", "Recorder");
            FileIOUtils.DeleteSaveSidecarLoadMeta("", "parsek_rw_x", "Recorder");
            // No exception expected; no deletion logged.
            Assert.DoesNotContain(logLines, l => l.Contains("Deleted orphaned save sidecar"));
        }

        [Fact]
        public void NoOp_WhenBaseNameNullOrEmpty()
        {
            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, null, "Recorder");
            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, "", "Recorder");
            Assert.DoesNotContain(logLines, l => l.Contains("Deleted orphaned save sidecar"));
        }

        [Fact]
        public void DoesNotDeleteSfs_OnlyLoadMeta()
        {
            string sfs = WriteFile("parsek_rw_keep.sfs");
            string loadMeta = WriteFile("parsek_rw_keep.loadmeta");

            FileIOUtils.DeleteSaveSidecarLoadMeta(tempDir, "parsek_rw_keep", "Recorder");

            Assert.False(File.Exists(loadMeta), ".loadmeta should be deleted");
            Assert.True(File.Exists(sfs), ".sfs must not be touched");
        }
    }
}
