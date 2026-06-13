using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the orphan-cleanup safety guard pair: <see cref="RecordingStore.CleanOrphanFiles"/>
    /// must refuse to delete sidecars when the scenario reports zero known recording IDs but
    /// the disk has live sidecar-shaped recording IDs (the "load lost its tree state" pattern),
    /// and <c>ParsekScenario.SaveTreeRecordings</c> must warn when about to write zero
    /// RECORDING_TREE nodes while disk holds stranded sidecars.
    /// </summary>
    [Collection("Sequential")]
    public class OrphanCleanupSafetyGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string originalSaveFolder;
        private readonly List<string> cleanupRoots = new List<string>();

        public OrphanCleanupSafetyGuardTests()
        {
            originalSaveFolder = HighLogic.SaveFolder;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = null;
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

        // ----- CleanOrphanFiles safety guard -----

        [Fact]
        public void CleanOrphanFiles_RefusesDeletion_WhenKnownIdsEmptyButDiskHasSidecarIds()
        {
            // Reproduces the "load lost its tree state" fault pattern: scenario reports 0
            // committed/pending recordings, but disk holds sidecars from 2 recordings whose
            // metadata still lives in quicksave.sfs. Without the guard, all 8 files would be
            // deleted, turning a recoverable accident into permanent data loss.
            string recordingsDir = CreateRecordingsDir("refuse-deletion");
            string a1 = Path.Combine(recordingsDir, "rec-a.prec");
            string a2 = Path.Combine(recordingsDir, "rec-a_vessel.craft");
            string a3 = Path.Combine(recordingsDir, "rec-a_ghost.craft");
            string a4 = Path.Combine(recordingsDir, "rec-a.pann");
            string b1 = Path.Combine(recordingsDir, "rec-b.prec");
            string b2 = Path.Combine(recordingsDir, "rec-b_vessel.craft");
            string b3 = Path.Combine(recordingsDir, "rec-b_ghost.craft");
            string b4 = Path.Combine(recordingsDir, "rec-b.pann");
            foreach (var p in new[] { a1, a2, a3, a4, b1, b2, b3, b4 })
                File.WriteAllText(p, "data");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            Assert.True(File.Exists(a1));
            Assert.True(File.Exists(a2));
            Assert.True(File.Exists(a3));
            Assert.True(File.Exists(a4));
            Assert.True(File.Exists(b1));
            Assert.True(File.Exists(b2));
            Assert.True(File.Exists(b3));
            Assert.True(File.Exists(b4));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("REFUSING to delete")
                && l.Contains("0 known recording IDs") && l.Contains("2 sidecar-shaped recording ID(s)"));
        }

        [Fact]
        public void CleanOrphanFiles_StillCleansTransientArtifacts_WhenKnownIdsEmpty()
        {
            // The guard must not block cleanup of transient/legacy artifacts when those
            // are the only files present — those carry no recoverable bulk data.
            string recordingsDir = CreateRecordingsDir("transients-only");
            string transientStage = Path.Combine(recordingsDir, "rec-x.prec.stage.1");
            string transientBak = Path.Combine(recordingsDir, "rec-y_vessel.craft.bak.1");
            string transientTmp = Path.Combine(recordingsDir, "rec-z_ghost.craft.tmp");
            string legacyPcrf = Path.Combine(recordingsDir, "rec-w.pcrf");
            File.WriteAllText(transientStage, "stage");
            File.WriteAllText(transientBak, "bak");
            File.WriteAllText(transientTmp, "tmp");
            File.WriteAllText(legacyPcrf, "pcrf");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            Assert.False(File.Exists(transientStage));
            Assert.False(File.Exists(transientBak));
            Assert.False(File.Exists(transientTmp));
            Assert.False(File.Exists(legacyPcrf));
            Assert.DoesNotContain(logLines, l => l.Contains("REFUSING to delete"));
        }

        [Fact]
        public void CleanOrphanFiles_QuarantinesOrphan_WhenKnownIdsNonEmpty()
        {
            // Data-loss fix: an unknown recording sidecar is no longer hard-deleted — it is
            // MOVED to the _quarantine subfolder so its immutable bulk data stays recoverable
            // even when the "orphan" is really a still-referenced recording dropped by a
            // transient state bug (the partial-loss case the count==0 guard cannot catch).
            string recordingsDir = CreateRecordingsDir("quarantine-orphan");
            var keep = new Recording { RecordingId = "keep-rec", VesselName = "Keep" };
            RecordingStore.AddRecordingWithTreeForTesting(keep);

            string keepPrec = Path.Combine(recordingsDir, "keep-rec.prec");
            string orphanPrec = Path.Combine(recordingsDir, "orphan-rec.prec");
            File.WriteAllText(keepPrec, "keep");
            File.WriteAllText(orphanPrec, "orphan");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            string quarantinedOrphan = Path.Combine(
                recordingsDir, RecordingStore.OrphanQuarantineDirName, "orphan-rec.prec");
            Assert.True(File.Exists(keepPrec));            // known recording untouched
            Assert.False(File.Exists(orphanPrec));         // moved out of the active set
            Assert.True(File.Exists(quarantinedOrphan));   // preserved in quarantine, NOT deleted
            Assert.Equal("orphan", File.ReadAllText(quarantinedOrphan));
            Assert.DoesNotContain(logLines, l => l.Contains("REFUSING to delete"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]") && l.Contains("Quarantined orphan recording file")
                && l.Contains("orphan-rec.prec"));
        }

        [Fact]
        public void CleanOrphanFiles_QuarantinesPartialLoss_WhenSomeTreesSurvive()
        {
            // The exact Duna-mission shape: several recordings are known (their trees survived)
            // while a subset is orphaned (its tree was dropped). knownIds is non-empty, so the
            // count==0 guard does NOT fire — quarantine is what keeps the dropped recording's
            // data recoverable instead of destroying it.
            string recordingsDir = CreateRecordingsDir("quarantine-partial");
            var survivor = new Recording { RecordingId = "survivor-rec", VesselName = "Survivor" };
            RecordingStore.AddRecordingWithTreeForTesting(survivor);

            string survivorPrec = Path.Combine(recordingsDir, "survivor-rec.prec");
            string droppedPrec = Path.Combine(recordingsDir, "dropped-rec.prec");
            string droppedVessel = Path.Combine(recordingsDir, "dropped-rec_vessel.craft");
            File.WriteAllText(survivorPrec, "survive");
            File.WriteAllText(droppedPrec, "dropped-prec");
            File.WriteAllText(droppedVessel, "dropped-vessel");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            string qDir = Path.Combine(recordingsDir, RecordingStore.OrphanQuarantineDirName);
            Assert.True(File.Exists(survivorPrec));
            Assert.False(File.Exists(droppedPrec));
            Assert.False(File.Exists(droppedVessel));
            Assert.True(File.Exists(Path.Combine(qDir, "dropped-rec.prec")));
            Assert.True(File.Exists(Path.Combine(qDir, "dropped-rec_vessel.craft")));
            Assert.DoesNotContain(logLines, l => l.Contains("REFUSING to delete"));
        }

        [Fact]
        public void CleanOrphanFiles_QuarantineDoesNotOverwriteExisting_OnRepeatedSweep()
        {
            // A second sweep of an orphan whose name already sits in quarantine must NOT
            // clobber the earlier copy — both are preserved (the new one is suffixed).
            string recordingsDir = CreateRecordingsDir("quarantine-norepeat");
            var keep = new Recording { RecordingId = "keep-rec", VesselName = "Keep" };
            RecordingStore.AddRecordingWithTreeForTesting(keep);
            File.WriteAllText(Path.Combine(recordingsDir, "keep-rec.prec"), "keep");

            string qDir = Path.Combine(recordingsDir, RecordingStore.OrphanQuarantineDirName);
            Directory.CreateDirectory(qDir);
            File.WriteAllText(Path.Combine(qDir, "orphan-rec.prec"), "earlier-copy");

            File.WriteAllText(Path.Combine(recordingsDir, "orphan-rec.prec"), "newer-copy");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            Assert.Equal("earlier-copy", File.ReadAllText(Path.Combine(qDir, "orphan-rec.prec")));
            string[] quarantined = Directory.GetFiles(qDir, "orphan-rec.prec*");
            Assert.Equal(2, quarantined.Length); // earlier + suffixed newer copy
        }

        [Fact]
        public void CleanOrphanFiles_EmptyDirectory_DoesNotWarn()
        {
            // No sidecars on disk and no known IDs: nothing to delete, nothing suspicious.
            // The guard must not fire on a genuinely-empty save.
            CreateRecordingsDir("empty-dir");

            logLines.Clear();
            RecordingStore.CleanOrphanFiles();

            Assert.DoesNotContain(logLines, l => l.Contains("REFUSING to delete"));
        }

        // ----- CollectSidecarIdsOnDisk helper -----

        [Fact]
        public void CollectSidecarIdsOnDisk_ReturnsDistinctIds_FromSidecarFiles()
        {
            string recordingsDir = CreateRecordingsDir("collect-ids");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-a.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-a_vessel.craft"), "v");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-a_ghost.craft"), "g");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-b.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-b.pann"), "a");

            var ids = RecordingStore.CollectSidecarIdsOnDisk();

            Assert.Equal(2, ids.Count);
            Assert.Contains("rec-a", ids);
            Assert.Contains("rec-b", ids);
        }

        [Fact]
        public void CollectSidecarIdsOnDisk_ExcludesTransientAndLegacyArtifacts()
        {
            string recordingsDir = CreateRecordingsDir("exclude-artifacts");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-real.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-stage.prec.stage.1"), "s");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-bak_vessel.craft.bak.1"), "b");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-tmp_ghost.craft.tmp"), "t");
            File.WriteAllText(Path.Combine(recordingsDir, "rec-legacy.pcrf"), "l");
            File.WriteAllText(Path.Combine(recordingsDir, "readme.txt"), "r");

            var ids = RecordingStore.CollectSidecarIdsOnDisk();

            Assert.Single(ids);
            Assert.Contains("rec-real", ids);
        }

        [Fact]
        public void CollectSidecarIdsOnDisk_ReturnsEmpty_WhenDirectoryDoesNotExist()
        {
            // No CreateRecordingsDir call — leave the override pointing at a path that
            // doesn't exist. The helper must return an empty set, not throw.
            string saveFolder = "parsek-test-missing-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.Combine(Path.GetTempPath(), saveFolder, "Parsek", "Recordings");
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = root;

            var ids = RecordingStore.CollectSidecarIdsOnDisk();

            Assert.Empty(ids);
        }

        // ----- ParsekScenario.SaveTreeRecordings stranded-sidecar warn -----

        [Fact]
        public void SaveTreeRecordings_WarnsWhenWritingZeroTrees_OverStrandedSidecars()
        {
            // Reproduces the upstream half of the data-loss chain: scenario state has 0
            // committed trees but disk holds bulk data for prior recordings. Writing now
            // produces an .sfs with 0 RECORDING_TREE nodes — a state-management bug whose
            // damage CleanOrphanFiles would normally compound on next load.
            string recordingsDir = CreateRecordingsDir("save-warns");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec_vessel.craft"), "v");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec_ghost.craft"), "g");

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.SaveTreeRecordings(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("writing 0 RECORDING_TREE nodes")
                && l.Contains("1 stranded sidecar"));
        }

        [Fact]
        public void SaveTreeRecordings_DoesNotWarn_WhenDiskIsAlsoEmpty()
        {
            // Genuinely-empty save (no trees in memory, no sidecars on disk) must not
            // emit the stranded-sidecar warn.
            CreateRecordingsDir("save-clean");

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.SaveTreeRecordings(node);

            Assert.DoesNotContain(logLines, l => l.Contains("stranded sidecar"));
        }

        [Fact]
        public void SaveTreeRecordings_DoesNotWarn_WhenWritingAtLeastOneTree()
        {
            // When the in-memory state is consistent with disk (at least one tree being
            // saved), the diagnostic stays quiet — this is the normal case.
            string recordingsDir = CreateRecordingsDir("save-consistent");
            var rec = new Recording { RecordingId = "live-rec", VesselName = "Live" };
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            File.WriteAllText(Path.Combine(recordingsDir, "live-rec.prec"), "p");

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.SaveTreeRecordings(node);

            Assert.NotEmpty(node.GetNodes("RECORDING_TREE"));
            Assert.DoesNotContain(logLines, l => l.Contains("stranded sidecar"));
        }

        private string CreateRecordingsDir(string label)
        {
            string saveFolder = "parsek-test-orphan-guard-" + label + "-" + Guid.NewGuid().ToString("N");
            HighLogic.SaveFolder = saveFolder;
            string root = Path.Combine(Path.GetTempPath(), saveFolder);
            string recordingsDir = Path.Combine(root, "Parsek", "Recordings");
            Directory.CreateDirectory(recordingsDir);
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = recordingsDir;
            cleanupRoots.Add(root);
            return recordingsDir;
        }
    }
}
