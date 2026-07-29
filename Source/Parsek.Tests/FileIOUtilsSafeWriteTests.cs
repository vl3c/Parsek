using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the three safe-write primitives in <see cref="FileIOUtils"/>
    /// (<see cref="FileIOUtils.SafeWriteConfigNode"/>, <see cref="FileIOUtils.SafeWriteBytes"/>,
    /// <see cref="FileIOUtils.SafeMove"/>) - the shared write path behind recording sidecars,
    /// the ledger, the settings file, the milestone store and the game-state store.
    ///
    /// <para>
    /// What these catch: the destructive failure mode the old implementation had. It ignored
    /// <c>ConfigNode.Save</c>'s bool return (which is how KSP reports a failed write - it
    /// swallows its own IO exception), then deleted the destination and moved a temp file that
    /// was never written. The result was the caller's data destroyed with nothing written in
    /// its place. Every test here is an assertion that a failed write leaves the previous file
    /// on disk, byte-identical.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class FileIOUtilsSafeWriteTests : IDisposable
    {
        private const string Tag = "SafeWriteTest";

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public FileIOUtilsSafeWriteTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(
                Path.GetTempPath(), "parsek_safewrite_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }

        // ------------------------------------------------------------------ helpers

        private static ConfigNode MakeNode(string marker)
        {
            var node = new ConfigNode("PARSEK_SAFEWRITE_TEST");
            node.AddValue("marker", marker);
            ConfigNode child = node.AddNode("CHILD");
            child.AddValue("k", "v");
            return node;
        }

        /// <summary>
        /// The temp-write failure simulation used throughout: a DIRECTORY squatting on the
        /// <c>.tmp</c> path the safe-write pattern serializes into. Neither
        /// <c>ConfigNode.Save</c> nor <see cref="File.WriteAllBytes"/> can open a directory for
        /// writing, so the temp write fails - which is exactly the situation (failed write, no
        /// temp file to move) that used to destroy the destination. It is preferable to a
        /// read-only parent directory because it fails only the temp write, leaving the
        /// destination itself perfectly writable: any surviving destination content therefore
        /// proves the code chose not to touch it, not that the OS refused.
        /// </summary>
        private static void BlockTempPath(string path)
        {
            Directory.CreateDirectory(path + ".tmp");
        }

        /// <summary>
        /// True when the platform enforces <see cref="FileShare.None"/> against other handles
        /// (Windows does; Mono on Unix does not). The "replace step fails" tests simulate a
        /// locked destination, which is only an obstacle where sharing is actually enforced.
        /// </summary>
        private static bool SharingIsEnforced(string lockedPath)
        {
            try
            {
                using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static void AssertNoScratchFiles(string path)
        {
            Assert.False(File.Exists(path + ".tmp"),
                "the .tmp must not survive a successful write");
            Assert.False(File.Exists(path + ".bak"),
                "the swap .bak must not survive a successful write");
        }

        // ------------------------------------------------------- SafeWriteConfigNode: success

        [Fact]
        public void SafeWriteConfigNode_RoundTripsContent_AndLeavesNoScratchFiles()
        {
            string path = Path.Combine(tempDir, "roundtrip.pcfg");

            FileIOUtils.SafeWriteConfigNode(MakeNode("first"), path, Tag);

            Assert.True(File.Exists(path));
            ConfigNode read = ConfigNode.Load(path);
            Assert.NotNull(read);
            Assert.Equal("first", read.GetValue("marker"));
            Assert.Equal("v", read.GetNode("CHILD").GetValue("k"));
            AssertNoScratchFiles(path);
        }

        [Fact]
        public void SafeWriteConfigNode_OverwritesExistingDestination()
        {
            string path = Path.Combine(tempDir, "overwrite.pcfg");

            FileIOUtils.SafeWriteConfigNode(MakeNode("first"), path, Tag);
            FileIOUtils.SafeWriteConfigNode(MakeNode("second"), path, Tag);

            Assert.Equal("second", ConfigNode.Load(path).GetValue("marker"));
            AssertNoScratchFiles(path);
        }

        [Fact]
        public void SafeWriteConfigNode_CreatesMissingParentDirectory()
        {
            string path = Path.Combine(tempDir, "made", "up", "nested.pcfg");

            FileIOUtils.SafeWriteConfigNode(MakeNode("nested"), path, Tag);

            Assert.Equal("nested", ConfigNode.Load(path).GetValue("marker"));
        }

        // ------------------------------------------------------- SafeWriteConfigNode: failure

        [Fact]
        public void SafeWriteConfigNode_TempWriteFails_DestinationPreservedByteIdentical()
        {
            // The regression this whole file exists for: a failed serialize must not cost the
            // caller the file it already had.
            string path = Path.Combine(tempDir, "precious.pcfg");
            FileIOUtils.SafeWriteConfigNode(MakeNode("original"), path, Tag);
            byte[] before = File.ReadAllBytes(path);

            BlockTempPath(path);
            logLines.Clear();

            Assert.ThrowsAny<Exception>(
                () => FileIOUtils.SafeWriteConfigNode(MakeNode("replacement"), path, Tag));

            Assert.True(File.Exists(path), "the destination must survive a failed write");
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal("original", ConfigNode.Load(path).GetValue("marker"));
        }

        [Fact]
        public void SafeWriteConfigNode_TempWriteFails_LogsGrepStableWarnNamingThePath()
        {
            string path = Path.Combine(tempDir, "logged.pcfg");
            FileIOUtils.SafeWriteConfigNode(MakeNode("original"), path, Tag);

            BlockTempPath(path);
            logLines.Clear();

            Assert.ThrowsAny<Exception>(
                () => FileIOUtils.SafeWriteConfigNode(MakeNode("replacement"), path, Tag));

            Assert.Contains(logLines, l => l.Contains("[" + Tag + "]")
                && l.Contains("SafeWrite: failed to write temp file")
                && l.Contains(path)
                && l.Contains("left untouched"));
        }

        [Fact]
        public void SafeWriteConfigNode_TempWriteFails_CreatesNoDestinationAtAll()
        {
            // No prior file: a failed write must not leave a half-made destination behind
            // either, so a later load sees "absent", not "corrupt".
            string path = Path.Combine(tempDir, "never_written.pcfg");
            BlockTempPath(path);

            Assert.ThrowsAny<Exception>(
                () => FileIOUtils.SafeWriteConfigNode(MakeNode("replacement"), path, Tag));

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void SafeWriteConfigNode_ReplaceStepFails_DestinationPreserved()
        {
            string path = Path.Combine(tempDir, "locked.pcfg");
            FileIOUtils.SafeWriteConfigNode(MakeNode("original"), path, Tag);
            byte[] before = File.ReadAllBytes(path);

            // Hold the destination open with no sharing: both the File.Replace swap and the
            // move-aside fallback's first move need DELETE access to it, so the replace step
            // fails after a perfectly good temp file was written.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!SharingIsEnforced(path))
                    return; // platform does not enforce FileShare.None - nothing to simulate

                Assert.ThrowsAny<Exception>(
                    () => FileIOUtils.SafeWriteConfigNode(MakeNode("replacement"), path, Tag));
            }

            Assert.True(File.Exists(path), "the destination must survive a failed replace");
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"),
                "the original must never be left parked under .bak");
        }

        // ------------------------------------------------------------- SafeWriteBytes

        [Fact]
        public void SafeWriteBytes_RoundTripsContent_AndLeavesNoScratchFiles()
        {
            string path = Path.Combine(tempDir, "bytes.bin");
            byte[] payload = Encoding.UTF8.GetBytes("parsek-payload");

            FileIOUtils.SafeWriteBytes(payload, path, Tag);

            Assert.Equal(payload, File.ReadAllBytes(path));
            AssertNoScratchFiles(path);
        }

        [Fact]
        public void SafeWriteBytes_OverwritesExistingDestination()
        {
            string path = Path.Combine(tempDir, "bytes_overwrite.bin");
            FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("old"), path, Tag);

            byte[] replacement = Encoding.UTF8.GetBytes("new-and-longer");
            FileIOUtils.SafeWriteBytes(replacement, path, Tag);

            Assert.Equal(replacement, File.ReadAllBytes(path));
            AssertNoScratchFiles(path);
        }

        [Fact]
        public void SafeWriteBytes_TempWriteFails_DestinationPreservedByteIdentical()
        {
            string path = Path.Combine(tempDir, "bytes_precious.bin");
            byte[] original = Encoding.UTF8.GetBytes("original-payload");
            FileIOUtils.SafeWriteBytes(original, path, Tag);

            BlockTempPath(path);
            logLines.Clear();

            Assert.ThrowsAny<Exception>(
                () => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("replacement"), path, Tag));

            Assert.True(File.Exists(path), "the destination must survive a failed write");
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Contains(logLines, l => l.Contains("[" + Tag + "]")
                && l.Contains("SafeWrite: failed to write temp file")
                && l.Contains(path)
                && l.Contains("left untouched"));
        }

        [Fact]
        public void SafeWriteBytes_ReplaceStepFails_DestinationPreserved()
        {
            string path = Path.Combine(tempDir, "bytes_locked.bin");
            byte[] original = Encoding.UTF8.GetBytes("original-payload");
            FileIOUtils.SafeWriteBytes(original, path, Tag);

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!SharingIsEnforced(path))
                    return;

                Assert.ThrowsAny<Exception>(
                    () => FileIOUtils.SafeWriteBytes(Encoding.UTF8.GetBytes("replacement"), path, Tag));
            }

            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }

        // ------------------------------------------------------------------ SafeMove

        [Fact]
        public void SafeMove_ConsumesSource_AndOverwritesExistingDestination()
        {
            string src = Path.Combine(tempDir, "src.sfs");
            string dst = Path.Combine(tempDir, "dst.sfs");
            File.WriteAllText(src, "new-content");
            File.WriteAllText(dst, "old-content");

            FileIOUtils.SafeMove(src, dst, Tag);

            Assert.False(File.Exists(src), "SafeMove consumes the source");
            Assert.Equal("new-content", File.ReadAllText(dst));
            Assert.False(File.Exists(dst + ".bak"),
                "the previous destination must not be left parked under .bak");
        }

        [Fact]
        public void SafeMove_CreatesMissingDestinationDirectory()
        {
            string src = Path.Combine(tempDir, "src_nested.sfs");
            string dst = Path.Combine(tempDir, "rewind", "points", "rp1.sfs");
            File.WriteAllText(src, "rp-content");

            FileIOUtils.SafeMove(src, dst, Tag);

            Assert.Equal("rp-content", File.ReadAllText(dst));
        }

        [Fact]
        public void SafeMove_DestinationLocked_PreservesBothSidesAndLogs()
        {
            string src = Path.Combine(tempDir, "src_locked.sfs");
            string dst = Path.Combine(tempDir, "dst_locked.sfs");
            File.WriteAllText(src, "new-content");
            File.WriteAllText(dst, "old-content");

            logLines.Clear();
            using (new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (!SharingIsEnforced(dst))
                    return;

                Assert.ThrowsAny<Exception>(() => FileIOUtils.SafeMove(src, dst, Tag));
            }

            Assert.True(File.Exists(src), "a failed SafeMove must not consume the source");
            Assert.Equal("new-content", File.ReadAllText(src));
            Assert.Equal("old-content", File.ReadAllText(dst));
            Assert.False(File.Exists(dst + ".bak"));
            Assert.Contains(logLines, l => l.Contains("[" + Tag + "]")
                && l.Contains("SafeMove: File.Move(")
                && l.Contains("failed"));
        }

        [Fact]
        public void SafeMove_MissingSource_LeavesDestinationIntact()
        {
            string src = Path.Combine(tempDir, "does_not_exist.sfs");
            string dst = Path.Combine(tempDir, "dst_survivor.sfs");
            File.WriteAllText(dst, "old-content");

            Assert.ThrowsAny<Exception>(() => FileIOUtils.SafeMove(src, dst, Tag));

            Assert.True(File.Exists(dst), "a SafeMove with no source must not delete the destination");
            Assert.Equal("old-content", File.ReadAllText(dst));
            Assert.False(File.Exists(dst + ".bak"));
        }

        // ---------------------------------------------------------------- argument guards

        [Fact]
        public void SafeWriteConfigNode_RejectsNullNodeAndEmptyPath()
        {
            Assert.Throws<ArgumentNullException>(
                () => FileIOUtils.SafeWriteConfigNode(null, Path.Combine(tempDir, "x.pcfg"), Tag));
            Assert.Throws<ArgumentException>(
                () => FileIOUtils.SafeWriteConfigNode(MakeNode("x"), "", Tag));
        }

        [Fact]
        public void SafeWriteBytes_RejectsEmptyPath()
        {
            Assert.Throws<ArgumentException>(
                () => FileIOUtils.SafeWriteBytes(new byte[] { 1 }, "", Tag));
        }
    }
}
