using System;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Phase 14 of Rewind-to-Staging (design §7.28): computes the on-disk size
    /// of persisted rewind-point quicksaves under
    /// <c>saves/&lt;save&gt;/Parsek/RewindPoints/</c> with a short result cache
    /// so the Settings window can render a live usage line without hitting the
    /// filesystem every OnGUI frame.
    ///
    /// <para>
    /// <b>Why a 10-second cache:</b> the Settings window redraws on every IMGUI
    /// event. Enumerating a directory + <c>FileInfo.Length</c> for each RP
    /// allocates, hits disk, and under Windows contends with KSP's save-write
    /// pipeline. Cached snapshot is computed at most once per 10s wall-clock
    /// per code path, logged once, and reused. Cache is keyed by the resolved
    /// directory path so switching saves invalidates naturally.
    /// </para>
    ///
    /// <para>
    /// Pure static + internal so unit tests can poke a test clock + reset
    /// state between fixtures. Not added to the ERS/ELS allowlist — this
    /// helper only reads the filesystem; it touches no recording or ledger
    /// state.
    /// </para>
    /// </summary>
    internal static class RewindPointDiskUsage
    {
        /// <summary>
        /// Snapshot of the most recent disk-usage computation, used as the
        /// cache payload and returned to callers. All fields are immutable
        /// post-construction.
        /// </summary>
        internal struct Snapshot
        {
            public long TotalBytes;
            public int FileCount;
            public double ComputedAtSeconds;
            public string DirectoryPath;
        }

        /// <summary>Cache lifetime in wall-clock seconds (design §7.28).</summary>
        internal const double CacheTtlSeconds = 10.0;

        /// <summary>
        /// Test seam: pluggable monotonic clock. Production leaves it null, so
        /// the helper uses <see cref="DateTime.UtcNow"/>. Unit tests override
        /// this so cache expiry is deterministic.
        /// </summary>
        internal static Func<double> ClockSourceForTesting;

        private static Snapshot lastSnapshot;
        private static bool hasSnapshot;

        private static double NowSeconds()
        {
            var clock = ClockSourceForTesting;
            if (clock != null) return clock();
            return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// Returns a cached or freshly-computed snapshot of the rewind-point
        /// directory's total size and file count. Computes from scratch when
        /// no snapshot exists, when the directory path changed since last
        /// compute, or when the cached snapshot is older than
        /// <see cref="CacheTtlSeconds"/>.
        /// </summary>
        /// <param name="directoryPath">Absolute directory to scan. May be null
        /// or point at a non-existent directory; in that case the snapshot
        /// returns zero bytes / zero files without throwing.</param>
        internal static Snapshot GetSnapshot(string directoryPath)
        {
            double now = NowSeconds();

            if (hasSnapshot
                && string.Equals(lastSnapshot.DirectoryPath, directoryPath ?? "", StringComparison.Ordinal)
                && (now - lastSnapshot.ComputedAtSeconds) < CacheTtlSeconds)
            {
                return lastSnapshot;
            }

            Snapshot fresh = Compute(directoryPath, now);
            lastSnapshot = fresh;
            hasSnapshot = true;

            ParsekLog.VerboseRateLimited("Rewind", "disk-usage-compute",
                $"Disk usage: {fresh.TotalBytes} bytes across {fresh.FileCount} files",
                CacheTtlSeconds);

            return fresh;
        }

        /// <summary>
        /// Directly compute a snapshot without consulting or updating the
        /// cache. Safe against null / missing directory: returns a zero
        /// snapshot and leaves no log noise behind.
        /// </summary>
        internal static Snapshot Compute(string directoryPath, double nowSeconds)
        {
            var result = new Snapshot
            {
                TotalBytes = 0L,
                FileCount = 0,
                ComputedAtSeconds = nowSeconds,
                DirectoryPath = directoryPath ?? ""
            };

            if (string.IsNullOrEmpty(directoryPath)) return result;
            if (!Directory.Exists(directoryPath)) return result;

            try
            {
                string[] files = Directory.GetFiles(directoryPath);
                long total = 0L;
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        var info = new FileInfo(files[i]);
                        total += info.Length;
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn("Rewind",
                            $"Disk usage: failed to stat '{files[i]}': {ex.GetType().Name}: {ex.Message}");
                    }
                }
                result.TotalBytes = total;
                result.FileCount = files.Length;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("Rewind",
                    $"Disk usage: failed to enumerate '{directoryPath}': {ex.GetType().Name}: {ex.Message}");
            }

            return result;
        }

        /// <summary>Test-only: clears the cached snapshot and resets the clock override.</summary>
        internal static void ResetForTesting()
        {
            hasSnapshot = false;
            lastSnapshot = default(Snapshot);
            ClockSourceForTesting = null;
        }

        /// <summary>
        /// Resolves the rewind-points directory for the current save without
        /// creating it. Returns <c>null</c> when KSP root / save folder are
        /// unavailable (menu / pre-game-load).
        /// </summary>
        internal static string ResolveCurrentSaveDirectory()
        {
            string root = KSPUtil.ApplicationRootPath ?? "";
            string saveFolder = HighLogic.SaveFolder ?? "";
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveFolder))
                return null;
            return Path.GetFullPath(Path.Combine(root, "saves", saveFolder, "Parsek", "RewindPoints"));
        }

        /// <summary>
        /// Formats a size + count snapshot for the Settings window. Uses the
        /// MB/KB/B breakpoints from <see cref="DiagnosticsComputation.FormatBytes"/>
        /// so the string reads the same as other diagnostics lines.
        /// </summary>
        internal static string FormatLine(Snapshot s)
        {
            return $"Rewind point disk usage: {DiagnosticsComputation.FormatBytes(s.TotalBytes)} ({s.FileCount} file{(s.FileCount == 1 ? "" : "s")})";
        }
    }
}
