using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Parsek
{
    /// <summary>
    /// One-time safety backup of an existing save the first time Parsek opens it.
    ///
    /// <para>Parsek edits <c>persistent.sfs</c> (ledger patches to funds/science/rep, crew
    /// stand-ins/reservations, facility state, contracts, tech, plus its own ScenarioModule
    /// node). A player who installs Parsek, tries it on an existing career, then uninstalls it,
    /// would be left with an altered save. To protect against that, the first time Parsek
    /// cold-loads a save that has no Parsek footprint yet, it copies that save - BEFORE any
    /// Parsek modification - into a timestamped sibling folder under <c>saves/</c> that appears
    /// in KSP's Load menu as a resumable "pre-Parsek" entry.</para>
    ///
    /// <para><b>Pristine guarantee.</b> A scenario module's <c>OnSave</c> cannot run before its
    /// own <c>OnLoad</c>, so a hook at the top of the cold <c>OnLoad</c> path (see
    /// <see cref="ParsekScenario.OnLoad"/>) runs before Parsek's first <c>persistent.sfs</c>
    /// write. The copied file is therefore <i>gameplay-state-pristine</i> (no Parsek
    /// funds/science/rep/crew/tech/contract/facility footprint). It is NOT necessarily
    /// byte-identical to the pre-install original: KSP injects an empty
    /// <c>SCENARIO{name=ParsekScenario}</c> node into every save (AddToAllGames) and may
    /// reformat the file; that node carries no gameplay data and KSP drops it cleanly if Parsek
    /// is uninstalled.</para>
    ///
    /// <para><b>Idempotency.</b> The authoritative "already touched by Parsek" signal is the
    /// on-disk footprint (a <c>Parsek/</c> subfolder or a populated <c>ParsekScenario</c> node),
    /// which also catches a prior aborted session that wrote a node but never a marker. The
    /// done-marker file is only a fast-path optimization.</para>
    ///
    /// <para><b>Atomicity.</b> The copy is staged into a temp dir under <c>Parsek/</c> and only
    /// <see cref="Directory.Move"/>d into <c>saves/</c> as the final step, so a mid-copy failure
    /// never leaves a visible, half-written save in the Load menu. Orphan staging dirs are swept
    /// on load.</para>
    /// </summary>
    internal static class PreParsekBackup
    {
        private const string Tag = "Backup";
        private const string PersistentSfsName = "persistent.sfs";
        private const string PersistentLoadMetaName = "persistent.loadmeta";
        private const string ParsekSubdirName = "Parsek";
        private const string ParsekScenarioNodeName = "ParsekScenario";

        /// <summary>Advisory done-marker (fast-path only; footprint is authoritative).</summary>
        internal static readonly string DoneMarkerRelative =
            Path.Combine(ParsekSubdirName, "pre-parsek-backup.txt");

        /// <summary>Sentinel dropped in each backup folder root; marks it as a Parsek backup.</summary>
        internal const string SentinelName = "parsek_backup_source.txt";

        /// <summary>Prefix of the transient staging directory (swept on load if orphaned).</summary>
        internal const string StagingPrefix = ".parsek-backup-staging-";

        /// <summary>Player-visible name fragment used as a secondary backup-folder heuristic.</summary>
        private const string BackupNameFragment = "(pre-Parsek";

        /// <summary>Top-level save subdirs copied into the backup (small; the player's craft).</summary>
        private static readonly string[] CopiedCraftDirs = { "Ships", "Subassemblies" };

        // ---------------------------------------------------------------------
        // Pure decision helpers (xUnit-testable; no KSP globals)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Replaces filesystem-invalid characters in a save name with '_' so it can form a
        /// backup folder name. Parentheses/spaces/dashes are valid and preserved.
        /// </summary>
        internal static string SanitizeSaveName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "save";
            char[] invalid = Path.GetInvalidFileNameChars();
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(invalid, chars[i]) >= 0)
                    chars[i] = '_';
            }
            string result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "save" : result;
        }

        /// <summary>
        /// Builds a collision-free backup folder name of the form
        /// <c>"&lt;name&gt; (pre-Parsek yyyy-MM-dd_HHmm)"</c>, appending <c>_2</c>/<c>_3</c>/... via
        /// the injected <paramref name="folderExists"/> predicate. <paramref name="nowLocal"/>
        /// is injected (local time) so the method stays pure/testable.
        /// </summary>
        internal static string BuildBackupFolderName(
            string baseSaveName, DateTime nowLocal, Func<string, bool> folderExists)
        {
            string san = SanitizeSaveName(baseSaveName);
            string ts = nowLocal.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            string baseCandidate = $"{san} {BackupNameFragment} {ts})";
            string candidate = baseCandidate;
            int n = 2;
            while (folderExists != null && folderExists(candidate))
            {
                candidate = $"{baseCandidate}_{n}";
                n++;
            }
            return candidate;
        }

        /// <summary>
        /// True if the current save folder is itself a Parsek backup, so it is never backed up
        /// again. Recognized by the <see cref="SentinelName"/> sentinel file (primary) or the
        /// <c>(pre-Parsek</c> name fragment (secondary, for a de-sentineled backup).
        /// </summary>
        internal static bool IsParsekBackupFolder(string saveFolderAbsPath, string saveFolderName)
        {
            if (!string.IsNullOrEmpty(saveFolderName)
                && saveFolderName.IndexOf(BackupNameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (!string.IsNullOrEmpty(saveFolderAbsPath))
            {
                try
                {
                    if (File.Exists(Path.Combine(saveFolderAbsPath, SentinelName)))
                        return true;
                }
                catch { /* unreadable path -> treat as not-a-backup; ShouldBackup still gated by footprint */ }
            }
            return false;
        }

        /// <summary>
        /// True if the on-disk save already carries a Parsek footprint - a <c>Parsek/</c>
        /// subfolder (<paramref name="parsekSubdirExists"/>) or a populated
        /// <c>SCENARIO{name=ParsekScenario}</c> node in the on-disk <c>persistent.sfs</c>
        /// (<paramref name="onDiskPersistentRoot"/>). The empty node KSP injects on first
        /// AddToAllGames contact (name+scene only, no children) is NOT a footprint. Biases the
        /// caller to skip: a Parsek-touched save must never be captured and mislabeled pristine.
        /// </summary>
        internal static bool HasParsekGameplayFootprint(
            ConfigNode onDiskPersistentRoot, bool parsekSubdirExists)
        {
            if (parsekSubdirExists) return true;
            if (onDiskPersistentRoot == null) return false;

            ConfigNode gameNode = onDiskPersistentRoot.GetNode("GAME") ?? onDiskPersistentRoot;
            ConfigNode[] scenarios = gameNode.GetNodes("SCENARIO");
            if (scenarios == null) return false;
            for (int i = 0; i < scenarios.Length; i++)
            {
                ConfigNode scn = scenarios[i];
                if (scn.GetValue("name") != ParsekScenarioNodeName) continue;
                // Populated == any child node, or any value beyond the stock name+scene pair.
                if (scn.nodes.Count > 0) return true;
                if (scn.values.Count > 2) return true;
            }
            return false;
        }

        /// <summary>
        /// True only for an unambiguously brand-new empty save (no vessels, no gathered science,
        /// no completed milestones, no active contracts) parsed from the on-disk
        /// <c>persistent.sfs</c>. Fail-open: an unparseable/ambiguous save returns false so it is
        /// backed up - the only cost of a false positive is one small folder; a false negative
        /// loses a career.
        /// </summary>
        internal static bool IsBrandNewEmptySave(CareerSaveSnapshot parsed)
        {
            if (parsed == null || !parsed.Parsed) return false;
            return parsed.Vessels.Count == 0
                && parsed.SubjectScience.Count == 0
                && parsed.CompletedMilestoneIds.Count == 0
                && parsed.ActiveContractGuids.Count == 0;
        }

        /// <summary>
        /// Pure gate deciding whether to make a pre-Parsek backup, with a grep-stable
        /// <paramref name="reason"/> for the log. Ordered cheapest-first; footprint is checked
        /// before brand-new so a Parsek-touched save is never captured.
        /// </summary>
        internal static bool ShouldBackup(
            bool isColdLoad, bool enabled, bool markerExists, bool footprintPresent,
            bool isBackupFolder, bool isBrandNewEmpty, out string reason)
        {
            if (!isColdLoad) { reason = "not-cold-load"; return false; }
            if (!enabled) { reason = "disabled"; return false; }
            if (markerExists) { reason = "marker-present"; return false; }
            if (isBackupFolder) { reason = "is-backup-folder"; return false; }
            if (footprintPresent) { reason = "already-parsek-footprint"; return false; }
            if (isBrandNewEmpty) { reason = "brand-new-empty"; return false; }
            reason = "eligible";
            return true;
        }

        // ---------------------------------------------------------------------
        // Impure orchestration (runs inside KSP; not xUnit-driven - in-game tested)
        // ---------------------------------------------------------------------

        /// <summary>Absolute path to the advisory done-marker, or null if context is unavailable.</summary>
        internal static string ResolveDoneMarkerPath()
            => RecordingPaths.ResolveSaveScopedPath(DoneMarkerRelative);

        internal static bool DoneMarkerExists()
        {
            string p = ResolveDoneMarkerPath();
            return !string.IsNullOrEmpty(p) && File.Exists(p);
        }

        /// <summary>
        /// Removes any leftover <see cref="StagingPrefix"/> staging directories in the save
        /// folder (crash-safe cleanup). Cheap no-op when none exist.
        /// </summary>
        internal static void SweepOrphanStagingDirs()
        {
            try
            {
                string saveDir = ResolveSaveDir();
                if (string.IsNullOrEmpty(saveDir) || !Directory.Exists(saveDir)) return;

                int swept = 0;
                foreach (string dir in Directory.GetDirectories(saveDir))
                {
                    string name = Path.GetFileName(dir);
                    if (name == null || !name.StartsWith(StagingPrefix, StringComparison.Ordinal))
                        continue;
                    try
                    {
                        Directory.Delete(dir, true);
                        swept++;
                    }
                    catch (Exception ex)
                    {
                        ParsekLog.Warn(Tag, $"SweepOrphanStagingDirs: failed to delete '{dir}': {ex.Message}");
                    }
                }
                if (swept > 0)
                    ParsekLog.Info(Tag, $"Swept {swept} orphan backup-staging dir(s)");
            }
            catch (Exception ex)
            {
                ParsekLog.Verbose(Tag, $"SweepOrphanStagingDirs: {ex.GetType().Name}:{ex.Message}");
            }
        }

        /// <summary>
        /// Decides and (if eligible) performs the one-time pre-Parsek backup. Called once from
        /// the cold-load path of <see cref="ParsekScenario.OnLoad"/>. Fail-loud: a failed copy
        /// logs an Error, warns the player on-screen, and writes no marker (retry next cold load).
        /// </summary>
        internal static void MaybeBackupOnFirstColdContact()
        {
            try
            {
                bool enabled = ParsekSettings.Current?.autoBackupExistingSaves ?? true;

                // Fast path: a prior successful backup wrote the marker.
                if (DoneMarkerExists())
                {
                    ParsekLog.Verbose(Tag, "Skip: pre-Parsek backup marker already present");
                    return;
                }

                string root = SafeApplicationRootPath();
                string saveName = SafeSaveFolder();
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveName))
                {
                    ParsekLog.Verbose(Tag,
                        $"Skip: missing save context (root='{root}', save='{saveName}')");
                    return;
                }

                string savesDir = Path.Combine(root, "saves");
                string saveDir = Path.Combine(savesDir, saveName);
                string persistentPath = Path.Combine(saveDir, PersistentSfsName);

                bool isBackupFolder = IsParsekBackupFolder(saveDir, saveName);
                bool parsekSubdir = Directory.Exists(Path.Combine(saveDir, ParsekSubdirName));

                ConfigNode onDisk = null;
                try
                {
                    if (File.Exists(persistentPath))
                        onDisk = ConfigNode.Load(persistentPath);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn(Tag, $"Could not read on-disk '{persistentPath}': {ex.Message}");
                }

                bool footprint = HasParsekGameplayFootprint(onDisk, parsekSubdir);

                bool brandNew = false;
                try
                {
                    if (onDisk != null)
                        brandNew = IsBrandNewEmptySave(CareerSaveParser.Parse(onDisk));
                }
                catch (Exception ex)
                {
                    ParsekLog.Verbose(Tag, $"Progress parse failed (fail-open -> back up): {ex.Message}");
                    brandNew = false;
                }

                if (!ShouldBackup(true, enabled, false, footprint, isBackupFolder, brandNew, out string reason))
                {
                    ParsekLog.Info(Tag, $"Skip: reason={reason} save='{saveName}'");
                    return;
                }

                ParsekLog.Info(Tag,
                    $"First-contact backup: save='{saveName}' footprint={footprint} brandNew={brandNew}");
                PerformBackup(savesDir, saveName, saveDir, persistentPath);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag, $"MaybeBackupOnFirstColdContact failed: {ex.GetType().Name}:{ex.Message}");
            }
        }

        /// <summary>
        /// Stages the backup (persistent.sfs first, then loadmeta + craft dirs + sentinel) into a
        /// temp dir under <c>Parsek/</c>, then atomically moves it into
        /// <c>saves/&lt;final name&gt;</c>. Writes the done-marker only after the move succeeds.
        /// </summary>
        private static void PerformBackup(
            string savesDir, string saveName, string saveDir, string persistentPath)
        {
            string finalName = BuildBackupFolderName(
                saveName,
                KSPUtil.SystemDateTime.DateTimeNow(),
                candidate => Directory.Exists(Path.Combine(savesDir, candidate)));
            string finalPath = Path.Combine(savesDir, finalName);

            // Stage inside the SOURCE save folder (same volume -> atomic Directory.Move), but NOT
            // under Parsek/: creating an empty Parsek/ here would make a failed backup look like a
            // Parsek footprint on the next load and suppress the retry. A staging subdir inside the
            // save folder is not a top-level saves/ entry, so it never appears in the Load menu.
            string staging = Path.Combine(saveDir, StagingPrefix + Guid.NewGuid().ToString("N"));

            int files = 0;
            long bytes = 0;
            try
            {
                Directory.CreateDirectory(staging);

                // persistent.sfs first: locks in the pristine capture.
                CopyFileInto(persistentPath, staging, ref files, ref bytes);
                CopyFileInto(Path.Combine(saveDir, PersistentLoadMetaName), staging, ref files, ref bytes);

                // The player's saved craft (small; excludes quicksaves + Parsek/ + KSP Backup/).
                foreach (string craftDir in CopiedCraftDirs)
                {
                    string src = Path.Combine(saveDir, craftDir);
                    if (!Directory.Exists(src)) continue;
                    if (!FileIOUtils.CopyDirectory(src, Path.Combine(staging, craftDir), null, Tag,
                            out int f, out long b))
                        throw new IOException($"CopyDirectory failed for '{craftDir}'");
                    files += f;
                    bytes += b;
                }

                WriteSentinel(staging, saveName);

                if (Directory.Exists(finalPath))
                    throw new IOException($"backup folder '{finalName}' already exists");
                Directory.Move(staging, finalPath);

                ParsekLog.Info(Tag,
                    $"Captured pre-Parsek backup: save='{saveName}' -> '{finalName}' files={files} bytes={bytes}");
                WriteDoneMarker(finalName);
                ParsekLog.ScreenMessage(
                    $"Parsek backed up your save as '{finalName}' (resume it from the main menu if needed)", 8f);
            }
            catch (Exception ex)
            {
                ParsekLog.Error(Tag,
                    $"FAILED to back up save='{saveName}': {ex.GetType().Name}:{ex.Message}");
                ParsekLog.ScreenMessage(
                    "Parsek could not auto-backup your save; please back it up manually", 8f);
                TryDeleteDir(staging);
            }
        }

        private static void CopyFileInto(string srcFile, string dstDir, ref int files, ref long bytes)
        {
            if (string.IsNullOrEmpty(srcFile) || !File.Exists(srcFile)) return;
            var fi = new FileInfo(srcFile);
            File.Copy(srcFile, Path.Combine(dstDir, fi.Name), false);
            files++;
            bytes += fi.Length;
        }

        private static void WriteSentinel(string backupFolder, string saveName)
        {
            try
            {
                string body =
                    "This folder is a Parsek pre-installation backup of your save.\r\n" +
                    $"Source save: {saveName}\r\n" +
                    $"Created: {KSPUtil.SystemDateTime.DateTimeNow():yyyy-MM-dd HH:mm}\r\n" +
                    "Do not delete this file if you want Parsek to keep recognizing this folder as a backup.\r\n";
                File.WriteAllText(Path.Combine(backupFolder, SentinelName), body);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"WriteSentinel failed: {ex.Message}");
            }
        }

        internal static void WriteDoneMarker(string backupFolderName)
        {
            string path = ResolveDoneMarkerPath();
            if (string.IsNullOrEmpty(path))
            {
                ParsekLog.Warn(Tag, "WriteDoneMarker: could not resolve marker path");
                return;
            }
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                string body =
                    "Parsek made a one-time pre-Parsek backup of this save.\r\n" +
                    $"Backup folder: {backupFolderName}\r\n" +
                    $"Written: {KSPUtil.SystemDateTime.DateTimeNow():yyyy-MM-dd HH:mm}\r\n";
                File.WriteAllText(path, body);
                ParsekLog.Verbose(Tag, $"Wrote done-marker '{path}'");
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"WriteDoneMarker failed: {ex.Message}");
            }
        }

        private static void TryDeleteDir(string dir)
        {
            try
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(Tag, $"Failed to delete staging dir '{dir}': {ex.Message}");
            }
        }

        private static string ResolveSaveDir()
        {
            string root = SafeApplicationRootPath();
            string saveName = SafeSaveFolder();
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(saveName)) return null;
            return Path.Combine(root, "saves", saveName);
        }

        private static string SafeApplicationRootPath()
        {
            try { return KSPUtil.ApplicationRootPath ?? ""; }
            catch { return ""; }
        }

        private static string SafeSaveFolder()
        {
            try { return HighLogic.SaveFolder ?? ""; }
            catch { return ""; }
        }
    }
}
