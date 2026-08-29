using System;
using System.Collections.Generic;
using System.IO;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The <c>PreParsekBackup</c> in-game category: the four PRE-PARSEK SAFETY BACKUP
    /// properties that only a live KSP process can state, automated so they stop being a
    /// pending manual runbook (docs/dev/todo-and-known-bugs.md, "Pre-Parsek save safety
    /// backup"). Everything here reads the REAL filesystem of the running instance and
    /// calls the REAL production entry points; nothing is simulated.
    ///
    /// <para><b>Why in-game and not xUnit.</b> The pure decision helpers
    /// (<c>ShouldBackup</c>, <c>HasParsekGameplayFootprint</c>, <c>IsBrandNewEmptySave</c>,
    /// <c>BuildBackupFolderName</c>) are already exhaustively unit-tested in
    /// <c>PreParsekBackupTests</c>. What was never tested is the part that needs KSP:
    /// that the cold-<c>OnLoad</c> hook FIRES at all on a footprint-free save, that the
    /// staged copy reaches <c>saves/</c> as a resumable sibling, that what landed there is
    /// gameplay-pristine, and that a second contact adds nothing.</para>
    ///
    /// <para><b>Scene-agnostic by design.</b> Every cell is <c>AnyScene</c>: the harness
    /// drives this category from two fixtures that boot to different scenes (a career with
    /// a pad craft routes to FLIGHT, a vessel-less brand-new career routes to SPACECENTER),
    /// and the subject - the save folder on disk - is identical either way.</para>
    ///
    /// <para><b>Read-only except for one deliberate call.</b>
    /// <see cref="RepeatColdContactCreatesNoSecondBackup"/> invokes
    /// <c>PreParsekBackup.MaybeBackupOnFirstColdContact()</c> a second time, which is the
    /// whole point of that cell: the idempotency claim is about the production entry point,
    /// not about a predicate. On a healthy save it takes the marker fast path and writes
    /// nothing; the cell reds if it does anything else.</para>
    /// </summary>
    internal static class PreParsekBackupInGameTests
    {
        private const string PersistentSfsName = "persistent.sfs";
        private const string PersistentLoadMetaName = "persistent.loadmeta";
        private static readonly string[] CraftDirs = { "Ships", "Subassemblies" };

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// (savesDir, saveName, saveDir) for the running instance, or a Skip when the save
        /// context is unavailable (a batch fired from a scene with no game loaded).
        /// </summary>
        private static bool TryResolveContext(out string savesDir, out string saveName,
                                              out string saveDir)
        {
            savesDir = PreParsekBackup.ResolveSavesDir();
            saveName = PreParsekBackup.CurrentSaveFolderName();
            saveDir = null;
            if (string.IsNullOrEmpty(savesDir) || string.IsNullOrEmpty(saveName))
                return false;
            saveDir = Path.Combine(savesDir, saveName);
            return Directory.Exists(saveDir);
        }

        private static List<string> RelativeFilesUnder(string root)
        {
            var rel = new List<string>();
            if (!Directory.Exists(root)) return rel;
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                rel.Add(f.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar,
                                                           Path.AltDirectorySeparatorChar)
                         .Replace('\\', '/'));
            rel.Sort(StringComparer.Ordinal);
            return rel;
        }

        // ------------------------------------------------------------------ P2

        /// <summary>
        /// PROPERTY 2 - the backup lands as a resumable sibling under <c>saves/</c> with the
        /// shape KSP's Load menu reads: exactly one folder named
        /// <c>&lt;save&gt; (pre-Parsek &lt;ts&gt;)</c> holding a parseable
        /// <c>persistent.sfs</c>, the <c>persistent.loadmeta</c> card KSP renders beside it,
        /// and the Parsek sentinel; plus a file-for-file mirror of whichever craft dirs the
        /// source save actually had.
        ///
        /// <para>STATED HONESTLY: this asserts the FILE SHAPE the Load menu enumerates
        /// (<c>saves/*/persistent.sfs</c> + its loadmeta), not the pixels of the menu. What
        /// only a human eye settles is whether the entry READS well - the card's title comes
        /// from the copied loadmeta, so it shows the SOURCE save's title while the folder
        /// shows the timestamped one.</para>
        /// </summary>
        [InGameTest(Category = "PreParsekBackup",
            Description = "The published backup is a sibling save folder with Load-menu file shape.")]
        internal static void BackupSiblingCarriesLoadMenuShape()
        {
            if (!TryResolveContext(out string savesDir, out string saveName, out string saveDir))
            {
                InGameAssert.Skip("no resolvable save context (needs a loaded game)");
                return;
            }

            List<string> backups = PreParsekBackup.FindBackupFoldersFor(savesDir, saveName);
            if (backups.Count == 0)
            {
                InGameAssert.Skip(
                    $"save '{saveName}' has no pre-Parsek backup to inspect (footprint-carrying " +
                    "or brand-new-empty fixture); the presence/absence decision itself is " +
                    "asserted by BackupPresenceMatchesTheEligibilityDecision");
                return;
            }

            InGameAssert.AreEqual(1, backups.Count,
                $"expected exactly one pre-Parsek backup of '{saveName}', found {backups.Count}: " +
                string.Join(", ", backups.ToArray()));

            string backupDir = Path.Combine(savesDir, backups[0]);
            string persistent = Path.Combine(backupDir, PersistentSfsName);
            string loadmeta = Path.Combine(backupDir, PersistentLoadMetaName);
            string sentinel = Path.Combine(backupDir, PreParsekBackup.SentinelName);

            InGameAssert.IsTrue(File.Exists(persistent),
                $"backup '{backups[0]}' has no {PersistentSfsName} - KSP's Load menu would not list it");
            InGameAssert.IsTrue(File.Exists(loadmeta),
                $"backup '{backups[0]}' has no {PersistentLoadMetaName} - the Load-menu card would not render");
            InGameAssert.IsTrue(File.Exists(sentinel),
                $"backup '{backups[0]}' has no {PreParsekBackup.SentinelName} sentinel");

            ConfigNode parsed = ConfigNode.Load(persistent);
            InGameAssert.IsNotNull(parsed,
                $"backup '{backups[0]}' {PersistentSfsName} did not parse as a ConfigNode");

            // The craft dirs are copied only when the source HAS them; assert the mirror for
            // whichever exist rather than demanding both (a career with no saved craft is a
            // legitimate subject, and a vacuous pass is what this message names).
            int mirrored = 0;
            for (int i = 0; i < CraftDirs.Length; i++)
            {
                string src = Path.Combine(saveDir, CraftDirs[i]);
                if (!Directory.Exists(src)) continue;
                mirrored++;
                string dst = Path.Combine(backupDir, CraftDirs[i]);
                InGameAssert.IsTrue(Directory.Exists(dst),
                    $"source save has {CraftDirs[i]}/ but backup '{backups[0]}' does not");
                List<string> srcFiles = RelativeFilesUnder(src);
                List<string> dstFiles = RelativeFilesUnder(dst);
                InGameAssert.AreEqual(string.Join("|", srcFiles.ToArray()),
                                      string.Join("|", dstFiles.ToArray()),
                    $"{CraftDirs[i]}/ file set differs between source save and backup '{backups[0]}'");
            }
            ParsekLog.Info("Backup",
                $"[InGameTest] backup shape OK: dir='{backups[0]}' craftDirsMirrored={mirrored}");
        }

        // ------------------------------------------------------------------ P1

        /// <summary>
        /// PROPERTY 1 - the copy happened BEFORE any Parsek write touched the save, measured
        /// as its CONSEQUENCE on the bytes that landed: the published <c>persistent.sfs</c>
        /// carries no populated <c>SCENARIO{name=ParsekScenario}</c> node and the backup
        /// folder carries no <c>Parsek/</c> subdir. Runs the same
        /// <c>PreParsekBackup.EvaluateCapturedPristine</c> the product runs at publish time,
        /// re-measured here against the folder as it stands after the load completed - so a
        /// later Parsek write leaking into the backup would red even though the publish-time
        /// reading was clean.
        /// </summary>
        [InGameTest(Category = "PreParsekBackup",
            Description = "The published backup's persistent.sfs is gameplay-state-pristine.")]
        internal static void CapturedPersistentIsGameplayPristine()
        {
            if (!TryResolveContext(out string savesDir, out string saveName, out _))
            {
                InGameAssert.Skip("no resolvable save context (needs a loaded game)");
                return;
            }
            List<string> backups = PreParsekBackup.FindBackupFoldersFor(savesDir, saveName);
            if (backups.Count == 0)
            {
                InGameAssert.Skip($"save '{saveName}' has no pre-Parsek backup to inspect");
                return;
            }

            string backupDir = Path.Combine(savesDir, backups[0]);
            PreParsekBackup.CapturedPristineVerdict verdict =
                PreParsekBackup.EvaluateCapturedPristine(backupDir, out string reason);
            // Asserted as EQUAL TO Pristine rather than NOT EQUAL TO NotPristine: an
            // Unverified reading means the check could not run at all, and a cell that
            // reads "could not look" as "looked and it was fine" is exactly the vacuous
            // pass this category exists to avoid.
            InGameAssert.AreEqual(PreParsekBackup.CapturedPristineVerdict.Pristine, verdict,
                $"published backup '{backups[0]}' did not verify as gameplay-pristine " +
                $"(verdict={verdict} reason={reason})");
            InGameAssert.AreEqual("pristine", reason);
            ParsekLog.Info("Backup",
                $"[InGameTest] captured pristine OK: dir='{backups[0]}' reason={reason}");
        }

        // ------------------------------------------------------------------ P3

        /// <summary>
        /// PROPERTY 3 - idempotence, asserted against the PRODUCTION ENTRY POINT rather than
        /// a predicate: call <c>MaybeBackupOnFirstColdContact()</c> again and require the
        /// backup census to be byte-for-byte the same list afterwards. Meaningful on every
        /// fixture - an already-backed-up save must take the marker fast path, and a save
        /// that was correctly skipped must go on being skipped.
        /// </summary>
        [InGameTest(Category = "PreParsekBackup",
            Description = "A second cold-contact invocation creates no second backup folder.")]
        internal static void RepeatColdContactCreatesNoSecondBackup()
        {
            if (!TryResolveContext(out string savesDir, out string saveName, out _))
            {
                InGameAssert.Skip("no resolvable save context (needs a loaded game)");
                return;
            }

            List<string> before = PreParsekBackup.FindBackupFoldersFor(savesDir, saveName);
            bool markerBefore = PreParsekBackup.DoneMarkerExists();
            if (markerBefore)
                InGameAssert.AreEqual(1, before.Count,
                    "the done-marker is present, so exactly one backup must exist; found " +
                    before.Count);

            PreParsekBackup.MaybeBackupOnFirstColdContact();

            List<string> after = PreParsekBackup.FindBackupFoldersFor(savesDir, saveName);
            InGameAssert.AreEqual(string.Join("|", before.ToArray()),
                                  string.Join("|", after.ToArray()),
                "a repeat MaybeBackupOnFirstColdContact changed the backup census for save '" +
                saveName + "' - the one-time contract is broken");
            InGameAssert.AreEqual(markerBefore, PreParsekBackup.DoneMarkerExists(),
                "a repeat MaybeBackupOnFirstColdContact moved the done-marker");
            ParsekLog.Info("Backup",
                $"[InGameTest] idempotent repeat OK: save='{saveName}' backups={after.Count} marker={markerBefore}");
        }

        // ------------------------------------------------------------------ P4

        /// <summary>
        /// PROPERTY 4 (and the other direction of properties 1-3) - the on-disk reality
        /// AGREES with the eligibility decision the product would take on this save.
        /// Bidirectional on purpose, so one cell is the live proof on BOTH harness fixtures:
        ///
        /// <list type="bullet">
        /// <item>marker present -> exactly one backup exists and the marker names it;</item>
        /// <item>no marker, no backup -> the save must be one the gate legitimately declines:
        /// brand-new-empty (the property-4 claim), an existing Parsek footprint, or a backup
        /// folder itself. A footprint-free, non-empty save with no backup is the FAILURE
        /// this cell exists to catch - the hook never fired.</item>
        /// </list>
        /// </summary>
        [InGameTest(Category = "PreParsekBackup",
            Description = "Backup presence on disk matches PreParsekBackup's own eligibility decision.")]
        internal static void BackupPresenceMatchesTheEligibilityDecision()
        {
            if (!TryResolveContext(out string savesDir, out string saveName, out string saveDir))
            {
                InGameAssert.Skip("no resolvable save context (needs a loaded game)");
                return;
            }

            List<string> backups = PreParsekBackup.FindBackupFoldersFor(savesDir, saveName);
            bool marker = PreParsekBackup.DoneMarkerExists();

            if (marker)
            {
                InGameAssert.AreEqual(1, backups.Count,
                    $"done-marker present for '{saveName}' but {backups.Count} backup folder(s) exist");
                string markerBody = File.ReadAllText(PreParsekBackup.ResolveDoneMarkerPath());
                InGameAssert.Contains(markerBody, backups[0],
                    "the done-marker does not name the backup folder that exists on disk");
                ParsekLog.Info("Backup",
                    $"[InGameTest] eligibility agreement OK (backed up): save='{saveName}' dir='{backups[0]}'");
                return;
            }

            InGameAssert.AreEqual(0, backups.Count,
                $"no done-marker for '{saveName}' yet {backups.Count} backup folder(s) exist");

            // No marker and no backup: prove the SKIP was justified by re-running the real
            // decision inputs over the on-disk save.
            string persistentPath = Path.Combine(saveDir, PersistentSfsName);
            if (!File.Exists(persistentPath))
            {
                InGameAssert.Skip($"no on-disk {PersistentSfsName} for '{saveName}' " +
                                  "(reason=no-persistent-sfs is itself a legitimate skip)");
                return;
            }
            ConfigNode onDisk = ConfigNode.Load(persistentPath);
            InGameAssert.IsNotNull(onDisk, $"could not parse '{persistentPath}'");

            bool parsekSubdir = Directory.Exists(Path.Combine(saveDir, "Parsek"));
            bool footprint = PreParsekBackup.HasParsekGameplayFootprint(onDisk, parsekSubdir);
            bool isBackupFolder = PreParsekBackup.IsParsekBackupFolder(saveDir, saveName);
            bool brandNew = PreParsekBackup.IsBrandNewEmptySave(CareerSaveParser.Parse(onDisk));

            bool wouldBackUp = PreParsekBackup.ShouldBackup(
                true, false, footprint, isBackupFolder, brandNew, out string reason);
            InGameAssert.IsFalse(wouldBackUp,
                $"save '{saveName}' has NO backup and NO marker, yet the gate says it is " +
                $"eligible (reason={reason}) - the cold-OnLoad hook never fired");
            ParsekLog.Info("Backup",
                $"[InGameTest] eligibility agreement OK (skipped): save='{saveName}' reason={reason} " +
                $"footprint={footprint} brandNew={brandNew} isBackupFolder={isBackupFolder}");
        }
    }
}
