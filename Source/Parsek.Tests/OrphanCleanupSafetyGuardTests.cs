using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the load-fault data-loss guard pair: <see cref="RecordingStore.CleanOrphanFiles"/>
    /// must refuse to delete sidecars when the scenario reports zero known recording IDs but
    /// the disk has live sidecar-shaped recording IDs (the "load lost its tree state" pattern),
    /// and <c>ParsekScenario.PreserveRecordingStateIfLoadFault</c> must re-hydrate the on-disk
    /// save's RECORDING_TREE + MISSION nodes (rather than let OnSave hollow the .sfs) when the
    /// in-memory store is empty while stranded sidecars remain on disk.
    /// </summary>
    [Collection("Sequential")]
    public class OrphanCleanupSafetyGuardTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string originalSaveFolder;
        private readonly List<string> cleanupRoots = new List<string>();
        private readonly List<string> cleanupFiles = new List<string>();

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
            ParsekScenario.PersistentSavePathOverrideForTesting = null;
            for (int i = 0; i < cleanupRoots.Count; i++)
            {
                try
                {
                    if (Directory.Exists(cleanupRoots[i]))
                        Directory.Delete(cleanupRoots[i], true);
                }
                catch { }
            }
            for (int i = 0; i < cleanupFiles.Count; i++)
            {
                try
                {
                    if (File.Exists(cleanupFiles[i]))
                        File.Delete(cleanupFiles[i]);
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

        // ----- ParsekScenario load-fault preservation guard (empty-store OnSave over a
        //       populated .sfs must NOT hollow the save) -----

        [Fact]
        public void PreserveGuard_TriggerDecision_OnlyFires_OnZeroTreesWithStrandedSidecars()
        {
            // Pure trigger: fires iff 0 tree nodes are being written AND stranded sidecars
            // exist. A genuinely-empty save (0 sidecars — deletion removes them) never trips,
            // and any populated write (>=1 tree node) short-circuits so a real save is never touched.
            Assert.True(ParsekScenario.ShouldPreserveRecordingStateOnEmptyWrite(0, 1));
            Assert.True(ParsekScenario.ShouldPreserveRecordingStateOnEmptyWrite(0, 5));
            Assert.False(ParsekScenario.ShouldPreserveRecordingStateOnEmptyWrite(0, 0));
            Assert.False(ParsekScenario.ShouldPreserveRecordingStateOnEmptyWrite(2, 3));
            Assert.False(ParsekScenario.ShouldPreserveRecordingStateOnEmptyWrite(1, 0));
        }

        [Fact]
        public void PreserveGuard_RehydratesTreesAndMissions_FromDiskSave_WhenStoreEmpty()
        {
            // The fix: an empty-store OnSave over a populated on-disk campaign save must NOT
            // hollow it. With stranded sidecars present (the load-fault fingerprint) and the
            // on-disk save still holding tree + mission metadata, the guard re-injects both
            // surfaces into the node being written so persistent.sfs keeps its state.
            string recordingsDir = CreateRecordingsDir("preserve-rehydrate");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec_vessel.craft"), "v");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                treeIds: new[] { "tree-a", "tree-b" },
                missionNames: new[] { "Mun Run", "Minmus Run", "Duna Run" });

            var node = new ConfigNode("ParsekScenario"); // empty store => 0 trees, 0 missions
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Equal(2, node.GetNodes("RECORDING_TREE").Length);
            Assert.Equal(3, node.GetNodes("MISSION").Length);
            Assert.Contains("tree-a", System.Array.ConvertAll(
                node.GetNodes("RECORDING_TREE"), n => n.GetValue("id")));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("load-fault guard")
                && l.Contains("Re-hydrated 2 RECORDING_TREE + 3 MISSION"));
        }

        [Fact]
        public void PreserveGuard_Warns_WhenStrandedButOnDiskSaveHasNoTrees()
        {
            // Stranded sidecars but the on-disk save is itself already 0-trees (nothing left
            // to preserve — the terminal hollowed state). The guard cannot recover it; it
            // warns to steer recovery toward quicksave.sfs / a backup, and never throws.
            string recordingsDir = CreateRecordingsDir("preserve-nothing-to-restore");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                treeIds: new string[0], missionNames: new string[0]);

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("writing 0 RECORDING_TREE nodes")
                && l.Contains("1 stranded sidecar") && l.Contains("no tree metadata to preserve"));
        }

        [Fact]
        public void PreserveGuard_NoOp_WhenNoStrandedSidecars()
        {
            // Genuinely-empty save: no trees in memory, no sidecars on disk. The guard must
            // stay silent and never touch the node — deleting all recordings is legitimate
            // (it removes the sidecars too), so the on-disk save must not be re-injected.
            CreateRecordingsDir("preserve-clean");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                treeIds: new[] { "should-not-be-read" }, missionNames: new string[0]);

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE")); // untouched — guard never ran
            Assert.DoesNotContain(logLines, l => l.Contains("load-fault guard"));
        }

        [Fact]
        public void PreserveGuard_NoOp_WhenTreeNodesAlreadyPresent()
        {
            // At least one tree node was written (consistent store) — the guard short-circuits
            // even with stranded sidecars present, so it can never clobber a real save's data
            // with a stale on-disk copy.
            string recordingsDir = CreateRecordingsDir("preserve-consistent");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                treeIds: new[] { "disk-tree" }, missionNames: new[] { "Disk Mission" });

            var node = new ConfigNode("ParsekScenario");
            node.AddNode("RECORDING_TREE").AddValue("id", "in-memory-tree");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Single(node.GetNodes("RECORDING_TREE"));
            Assert.Equal("in-memory-tree", node.GetNodes("RECORDING_TREE")[0].GetValue("id"));
            Assert.Empty(node.GetNodes("MISSION")); // disk missions NOT injected
            Assert.DoesNotContain(logLines, l => l.Contains("load-fault guard"));
        }

        // Writes a minimal KSP-shaped .sfs (GAME { SCENARIO { name = ParsekScenario ... } })
        // with the requested RECORDING_TREE / MISSION children plus a decoy SCENARIO so the
        // finder must match ParsekScenario by name. Tracked for cleanup.
        private string WriteTempPersistentSfs(string[] treeIds, string[] missionNames)
        {
            var root = new ConfigNode();
            var game = root.AddNode("GAME");
            game.AddNode("SCENARIO").AddValue("name", "ContractSystem"); // decoy
            var scenario = game.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            for (int i = 0; i < treeIds.Length; i++)
                scenario.AddNode("RECORDING_TREE").AddValue("id", treeIds[i]);
            for (int i = 0; i < missionNames.Length; i++)
                scenario.AddNode("MISSION").AddValue("name", missionNames[i]);

            string path = Path.Combine(Path.GetTempPath(),
                "parsek-test-sfs-" + Guid.NewGuid().ToString("N") + ".sfs");
            root.Save(path);
            cleanupFiles.Add(path);
            return path;
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
