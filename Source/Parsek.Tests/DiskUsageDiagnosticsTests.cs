using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 14 of Rewind-to-Staging (design §7.28): guards the disk-usage
    /// snapshot helper that backs the Settings window's "Rewind point disk
    /// usage" line. Verifies byte-sum accuracy across multiple files,
    /// defensive handling of a missing directory, and the 10-second
    /// result cache.
    /// </summary>
    [Collection("Sequential")]
    public class DiskUsageDiagnosticsTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly string tempRoot;

        public DiskUsageDiagnosticsTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RewindPointDiskUsage.ResetForTesting();

            // Per-test temp dir so fixtures do not stomp each other under the
            // shared [Collection("Sequential")] scheduler.
            tempRoot = Path.Combine(Path.GetTempPath(),
                "ParsekDiskUsageTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RewindPointDiskUsage.ResetForTesting();

            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — nothing else depends on teardown here.
            }
        }

        private string WriteFile(string name, int byteLength)
        {
            string path = Path.Combine(tempRoot, name);
            File.WriteAllBytes(path, new byte[byteLength]);
            return path;
        }

        [Fact]
        public void DiskUsage_MultipleFiles_SumsBytes()
        {
            // Regression: the helper must sum every file in the directory,
            // not just the first; byte counts must match FileInfo.Length so
            // the UI string lines up with the actual disk footprint.
            WriteFile("rp_a.sfs", 100);
            WriteFile("rp_b.sfs", 250);
            WriteFile("rp_c.sfs", 650);

            var snap = RewindPointDiskUsage.Compute(tempRoot, nowSeconds: 0.0);

            Assert.Equal(1000L, snap.TotalBytes);
            Assert.Equal(3, snap.FileCount);
            Assert.Equal(tempRoot, snap.DirectoryPath);
        }

        [Fact]
        public void DiskUsage_NoDirectory_ReturnsZero()
        {
            // Regression: the helper must treat a missing directory (common
            // pre-game-load / no RP captured yet) as zero bytes, not throw.
            string bogus = Path.Combine(tempRoot, "does_not_exist");
            var snap = RewindPointDiskUsage.Compute(bogus, nowSeconds: 0.0);
            Assert.Equal(0L, snap.TotalBytes);
            Assert.Equal(0, snap.FileCount);
            Assert.Equal(bogus, snap.DirectoryPath);

            // Null path: same fallback, no throw.
            var snapNull = RewindPointDiskUsage.Compute(null, nowSeconds: 0.0);
            Assert.Equal(0L, snapNull.TotalBytes);
            Assert.Equal(0, snapNull.FileCount);
        }

        [Fact]
        public void DiskUsage_CachedFor10s()
        {
            // Regression: the cache TTL guards against per-frame disk thrash.
            // A file appearing between two GetSnapshot calls inside the 10s
            // window must NOT be reflected until the cache expires.
            double fakeNow = 1000.0;
            RewindPointDiskUsage.ClockSourceForTesting = () => fakeNow;

            WriteFile("rp_a.sfs", 100);
            var first = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(100L, first.TotalBytes);
            Assert.Equal(1, first.FileCount);

            // Add another file; advance the clock by less than 10s.
            WriteFile("rp_b.sfs", 200);
            fakeNow = 1005.0; // +5s, under the 10s TTL

            var cached = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(100L, cached.TotalBytes);
            Assert.Equal(1, cached.FileCount);

            // Push past the 10s TTL; now the new file must appear.
            fakeNow = 1010.5; // +10.5s
            var fresh = RewindPointDiskUsage.GetSnapshot(tempRoot);
            Assert.Equal(300L, fresh.TotalBytes);
            Assert.Equal(2, fresh.FileCount);
        }
    }
}
