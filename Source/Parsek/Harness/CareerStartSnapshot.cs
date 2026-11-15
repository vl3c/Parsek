using System;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// Manages a one-per-save pristine quicksave captured at the very start of a career
    /// (the "career-start snapshot"). The Timeline "Warp to time" feature reloads it to make
    /// Year 1 / Day 1 (UT 0) a true reset to the game's initial state - resources, facilities,
    /// roster, and clock all at career creation - rather than landing at the earliest launch.
    ///
    /// <para>The snapshot is captured the first time a brand-new career reaches the Space
    /// Center (no recordings yet, clock still at the very start), and never overwritten after
    /// that. Saves created before this feature shipped will not have one and fall back to the
    /// earliest-launch rewind. Stored alongside the rewind quicksaves under
    /// <c>saves/&lt;save&gt;/Parsek/Saves/</c>.</para>
    /// </summary>
    internal static class CareerStartSnapshot
    {
        private const string Tag = "CareerStart";
        internal const string SaveFileName = "parsek_career_start";

        /// <summary>Test seam for the SaveGame call (xUnit cannot touch GamePersistence).</summary>
        internal static Func<string, string, bool> CaptureGameForTesting;

        internal static string ResolvePath()
        {
            return RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(SaveFileName));
        }

        internal static bool Exists()
        {
            string path = ResolvePath();
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        /// <summary>
        /// Pure decision: capture only for a genuinely fresh career - no snapshot yet, no
        /// recordings, and the clock still within the first day. This distinguishes a new
        /// career (captured at its first Space Center visit) from an existing save loaded for
        /// the first time after this feature shipped (which has recordings and/or a high UT,
        /// so it is skipped and keeps the earliest-launch fallback).
        /// </summary>
        internal static bool ShouldCapture(
            bool snapshotExists, int recordingCount, double currentUT, double secsPerDay)
        {
            return !snapshotExists
                && recordingCount == 0
                && currentUT >= 0
                && currentUT < secsPerDay;
        }

        /// <summary>
        /// Writes the snapshot via GamePersistence.SaveGame (to the saves root) then moves it
        /// into the Parsek rewind-saves directory. Mirrors FlightRecorder.CaptureRewindSave.
        /// Returns true on success. Idempotent at the call site (caller checks ShouldCapture).
        /// </summary>
        internal static bool Capture()
        {
            try
            {
                if (CaptureGameForTesting != null)
                    return CaptureGameForTesting(SaveFileName, HighLogic.SaveFolder);

                string result = GamePersistence.SaveGame(SaveFileName, HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Error(Tag, "Failed to capture career-start snapshot: SaveGame returned null");
                    return false;
                }

                string savesDir = Path.Combine(
                    KSPUtil.ApplicationRootPath ?? "", "saves", HighLogic.SaveFolder ?? "");
                string rootPath = Path.Combine(savesDir, SaveFileName + ".sfs");

                string rewindDir = RecordingPaths.EnsureRewindSavesDirectory();
                if (string.IsNullOrEmpty(rewindDir))
                {
                    ParsekLog.Error(Tag, "Failed to capture career-start snapshot: cannot create Parsek/Saves/ directory");
                    try { File.Delete(rootPath); } catch { }
                    return false;
                }

                string destPath = Path.Combine(rewindDir, SaveFileName + ".sfs");
                File.Move(rootPath, destPath);

                // SaveGame also wrote a .loadmeta sidecar to the root; only the .sfs
                // moves, so delete the orphan instead of leaving it to litter the save folder.
                FileIOUtils.DeleteSaveSidecarLoadMeta(savesDir, SaveFileName, Tag);

                ParsekLog.Info(Tag, $"Captured career-start snapshot: {destPath}");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"Failed to capture career-start snapshot: {ex.Message}");
                return false;
            }
        }
    }
}
