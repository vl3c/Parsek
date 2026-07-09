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
            // Default to the fault path (an incomplete cold load) so the guard fires; individual
            // tests flip this true to exercise the successful-load / intentional-wipe skip path.
            ParsekScenario.CommittedTreeStateLoadedForTesting = false;
        }

        public void Dispose()
        {
            HighLogic.SaveFolder = originalSaveFolder;
            RecordingStore.CleanOrphanFilesDirectoryOverrideForTesting = null;
            ParsekScenario.PersistentSavePathOverrideForTesting = null;
            ParsekScenario.CommittedTreeStateLoadedForTesting = false;
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

        // ----- ParsekScenario load-fault preservation guard (an incomplete-load OnSave over a
        //       populated .sfs must NOT hollow the save, and must never resurrect a real wipe) --
        //
        // NOTE: in the xUnit host ParsekScenario's private static initialLoadDone stays false
        // (OnLoad, which sets it true, is Unity-gated and never runs), so calling the guard
        // directly exercises the fault path (initialLoadDone == false). The successful-load /
        // intentional-wipe skip (initialLoadDone == true) is pinned by the pure-decision test.

        [Fact]
        public void PreserveGuard_TriggerDecision_FiresOnlyOnLoadFaultFingerprint()
        {
            // Pure trigger, first arg = committedTreeStateLoaded: fires iff the tree/mission load
            // did NOT complete (false) AND the committed store is empty AND stranded sidecars exist.
            Assert.True(ParsekScenario.ShouldPreserveCommittedTreeState(false, 0, 1));
            Assert.True(ParsekScenario.ShouldPreserveCommittedTreeState(false, 0, 5));
            // committedTreeStateLoaded == true: a completed load or a deliberate wipe — never resurrect.
            Assert.False(ParsekScenario.ShouldPreserveCommittedTreeState(true, 0, 1));
            Assert.False(ParsekScenario.ShouldPreserveCommittedTreeState(true, 0, 5));
            // committed store non-empty: normal populated save.
            Assert.False(ParsekScenario.ShouldPreserveCommittedTreeState(false, 2, 3));
            // no stranded sidecars: genuinely empty.
            Assert.False(ParsekScenario.ShouldPreserveCommittedTreeState(false, 0, 0));
        }

        [Fact]
        public void PreserveGuard_RehydratesCommittedTreesAndMissions_WhenCommittedStoreEmpty()
        {
            // The fix: an empty-committed-store OnSave over a populated on-disk campaign save must
            // NOT hollow it. With stranded sidecars present (the load-fault fingerprint) and the
            // on-disk save still holding committed tree + mission metadata, the guard re-injects
            // both surfaces into the node being written so persistent.sfs keeps its state.
            string recordingsDir = CreateRecordingsDir("preserve-rehydrate");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec_vessel.craft"), "v");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new[] { "tree-a", "tree-b" },
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
                && l.Contains("Re-hydrated 2 committed RECORDING_TREE + 3 MISSION"));
        }

        [Fact]
        public void PreserveGuard_PreservesLiveActiveNode_AndSkipsOnDiskActiveMarker()
        {
            // Finding [1]: a cold-load fault in FLIGHT leaves the committed store empty while an
            // auto-started recording writes its own active RECORDING_TREE node. The guard must
            // still restore the lost committed trees (gating on the COMMITTED count, not the total
            // node count) WITHOUT clobbering the live active node and WITHOUT importing the on-disk
            // active marker (which would create a second active node).
            string recordingsDir = CreateRecordingsDir("preserve-active");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new[] { "committed-a", "committed-b" },
                missionNames: new[] { "Mission A" },
                activeTreeIds: new[] { "disk-active" });

            var node = new ConfigNode("ParsekScenario");
            var live = node.AddNode("RECORDING_TREE"); // the in-flight active recording this save
            live.AddValue("id", "live-active");
            live.AddValue("isActive", "True");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            var ids = System.Array.ConvertAll(node.GetNodes("RECORDING_TREE"), n => n.GetValue("id"));
            Assert.Equal(3, ids.Length);                 // live-active + committed-a + committed-b
            Assert.Contains("live-active", ids);         // live recording preserved
            Assert.Contains("committed-a", ids);
            Assert.Contains("committed-b", ids);
            Assert.DoesNotContain("disk-active", ids);   // on-disk active marker NOT duplicated
            Assert.Single(node.GetNodes("MISSION"));
            Assert.Contains(logLines, l =>
                l.Contains("load-fault guard") && l.Contains("Re-hydrated 2 committed RECORDING_TREE + 1 MISSION"));
        }

        [Fact]
        public void PreserveGuard_Warns_WhenStrandedButOnDiskHasNoCommittedTrees()
        {
            // Stranded sidecars but the on-disk save has no committed trees (only an active marker,
            // or already 0-trees — the terminal hollowed state). Nothing to preserve; the guard
            // warns to steer recovery toward quicksave.sfs / a backup, and never throws.
            string recordingsDir = CreateRecordingsDir("preserve-nothing-to-restore");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new string[0], missionNames: new string[0],
                activeTreeIds: new[] { "only-active" });

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("writing 0 committed RECORDING_TREE nodes")
                && l.Contains("1 stranded sidecar") && l.Contains("no committed tree metadata to preserve"));
        }

        [Fact]
        public void PreserveGuard_DoesNotInjectMissions_WhenNoCommittedTreesOnDisk()
        {
            // Missions belong to committed trees: if the on-disk save has missions but no committed
            // trees to anchor them, the guard restores nothing (orphan missions are not resurrected).
            string recordingsDir = CreateRecordingsDir("preserve-orphan-missions");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");

            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new string[0], missionNames: new[] { "Orphan A", "Orphan B" });

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Empty(node.GetNodes("MISSION"));
            Assert.Contains(logLines, l => l.Contains("no committed tree metadata to preserve"));
        }

        [Fact]
        public void PreserveGuard_Warns_AndDoesNotThrow_WhenSfsMalformed()
        {
            // Finding [5]: a missing / unparseable persistent.sfs must never throw out of OnSave.
            // The guard leaves the node untouched and warns.
            string recordingsDir = CreateRecordingsDir("preserve-malformed");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");

            string badPath = Path.Combine(Path.GetTempPath(),
                "parsek-test-badsfs-" + Guid.NewGuid().ToString("N") + ".sfs");
            File.WriteAllText(badPath, "not a config node }}} ][ garbage without any GAME wrapper");
            cleanupFiles.Add(badPath);
            ParsekScenario.PersistentSavePathOverrideForTesting = badPath;

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node); // must not throw

            Assert.Empty(node.GetNodes("RECORDING_TREE"));
            Assert.Contains(logLines, l => l.Contains("load-fault guard"));
        }

        [Fact]
        public void PreserveGuard_NoOp_WhenNoStrandedSidecars()
        {
            // Genuinely-empty save: no committed trees, no sidecars on disk. The guard must stay
            // silent and never touch the node — deleting all recordings is legitimate (it removes
            // the sidecars too), so the on-disk save must not be re-injected.
            CreateRecordingsDir("preserve-clean");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new[] { "should-not-be-read" }, missionNames: new string[0]);

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE")); // untouched — guard never ran
            Assert.DoesNotContain(logLines, l => l.Contains("load-fault guard"));
        }

        [Fact]
        public void PreserveGuard_NoOp_WhenCommittedStoreNonEmpty()
        {
            // A non-empty committed store is a normal populated save — the guard short-circuits
            // before the disk scan even with stranded sidecars present, so it can never clobber a
            // real save's data with a stale on-disk copy.
            string recordingsDir = CreateRecordingsDir("preserve-consistent");
            var rec = new Recording { RecordingId = "live-rec", VesselName = "Live" };
            RecordingStore.AddRecordingWithTreeForTesting(rec); // committed store now non-empty
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new[] { "disk-tree" }, missionNames: new[] { "Disk Mission" });

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE")); // disk trees NOT injected
            Assert.Empty(node.GetNodes("MISSION"));
            Assert.DoesNotContain(logLines, l => l.Contains("load-fault guard"));
        }

        [Fact]
        public void PreserveGuard_NoOp_WhenCommittedTreeStateLoaded()
        {
            // The re-review regression fix: a COMPLETED tree/mission load (or an intentional
            // Wipe All against a loaded store) leaves committedTreeStateLoaded == true. Even with
            // the committed store empty AND stranded sidecars on disk AND on-disk trees available,
            // the guard must NOT fire — otherwise it would resurrect a deliberate wipe. This pins
            // the completed-load / wipe skip that the committedTreeStateLoaded gate provides (the
            // fault path — flag false — is covered by the tests above).
            string recordingsDir = CreateRecordingsDir("preserve-loaded-skip");
            File.WriteAllText(Path.Combine(recordingsDir, "stranded-rec.prec"), "p");
            ParsekScenario.PersistentSavePathOverrideForTesting = WriteTempPersistentSfs(
                committedTreeIds: new[] { "disk-a", "disk-b" }, missionNames: new[] { "M1" });
            ParsekScenario.CommittedTreeStateLoadedForTesting = true; // load completed / wipe

            var node = new ConfigNode("ParsekScenario");
            logLines.Clear();
            ParsekScenario.PreserveRecordingStateIfLoadFault(node);

            Assert.Empty(node.GetNodes("RECORDING_TREE")); // NOT re-injected — deliberate empty respected
            Assert.Empty(node.GetNodes("MISSION"));
            Assert.DoesNotContain(logLines, l => l.Contains("load-fault guard"));
        }

        // Writes a minimal KSP-shaped .sfs (GAME { SCENARIO { name = ParsekScenario ... } }) with
        // the requested committed RECORDING_TREE / MISSION children (plus optional isActive-marked
        // trees) and a decoy SCENARIO so the finder must match ParsekScenario by name. Tracked for cleanup.
        private string WriteTempPersistentSfs(
            string[] committedTreeIds, string[] missionNames, string[] activeTreeIds = null)
        {
            var root = new ConfigNode();
            var game = root.AddNode("GAME");
            game.AddNode("SCENARIO").AddValue("name", "ContractSystem"); // decoy
            var scenario = game.AddNode("SCENARIO");
            scenario.AddValue("name", "ParsekScenario");
            for (int i = 0; i < committedTreeIds.Length; i++)
                scenario.AddNode("RECORDING_TREE").AddValue("id", committedTreeIds[i]);
            if (activeTreeIds != null)
            {
                for (int i = 0; i < activeTreeIds.Length; i++)
                {
                    var t = scenario.AddNode("RECORDING_TREE");
                    t.AddValue("id", activeTreeIds[i]);
                    t.AddValue("isActive", "True");
                }
            }
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
