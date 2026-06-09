using System;
using System.IO;
using Parsek.InGameTests;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure-logic xUnit coverage for the universal test-runner campaign isolation
    /// decision helpers (InGameTestRunner.ClassifyBatchIsolationMode /
    /// ShouldRunFinalBatchRestore / BuildRestoreDegradedAlertMessage) and the
    /// capture-before-durable-marker source-text ordering contract. The
    /// Unity/scene-bound orchestration (CaptureBatchBaseline, the restore coroutines,
    /// disk revert, crash reconcile) is covered by in-game tests + manual playtest.
    /// </summary>
    public class TestBatchIsolationTests
    {
        // ----- ClassifyBatchIsolationMode truth table -----

        [Fact]
        public void Classify_FlightWithVessel_IsInMemoryAndDisk()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.InMemoryAndDisk,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: true));
        }

        [Fact]
        public void Classify_FlightNoVessel_IsDiskOnly()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.DiskOnly,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void Classify_TrackStation_IsInMemoryAndDisk()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.InMemoryAndDisk,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.TRACKSTATION, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void Classify_SpaceCenter_IsDiskOnly()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.DiskOnly,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.SPACECENTER, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void Classify_Editor_IsDiskOnly()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.DiskOnly,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.EDITOR, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void Classify_MainMenu_IsNone()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.None,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.MAINMENU, hasCurrentGame: true, hasSaveFolder: true, hasActiveVessel: false));
        }

        [Fact]
        public void Classify_NoGame_IsNone()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.None,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.FLIGHT, hasCurrentGame: false, hasSaveFolder: true, hasActiveVessel: true));
        }

        [Fact]
        public void Classify_NoSaveFolder_IsNone()
        {
            Assert.Equal(InGameTestRunner.BatchIsolationMode.None,
                InGameTestRunner.ClassifyBatchIsolationMode(
                    GameScenes.FLIGHT, hasCurrentGame: true, hasSaveFolder: false, hasActiveVessel: true));
        }

        // ----- ShouldRunFinalBatchRestore (8 rows) -----

        [Theory]
        // hasInMemoryBaseline, stateDirty, aborted -> expected
        [InlineData(false, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, false, true)]
        [InlineData(false, false, true, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, true, false)] // aborted always wins
        public void ShouldRunFinalBatchRestore_TruthTable(
            bool hasInMemoryBaseline, bool stateDirty, bool aborted, bool expected)
        {
            Assert.Equal(expected,
                InGameTestRunner.ShouldRunFinalBatchRestore(hasInMemoryBaseline, stateDirty, aborted));
        }

        // ----- BuildRestoreDegradedAlertMessage -----

        [Fact]
        public void AlertMessage_NamesSlotAndScene()
        {
            var info = new InGameTestRunner.FlightBatchBaselineState_SlotInfo
            {
                SlotName = "parsek-test-batch-baseline-x",
                CapturedScene = GameScenes.FLIGHT,
            };
            string msg = InGameTestRunner.BuildRestoreDegradedAlertMessage(info, "boom", diskFullyReverted: true);
            Assert.Contains("parsek-test-batch-baseline-x", msg);
            Assert.Contains("FLIGHT", msg);
            Assert.Contains("boom", msg);
        }

        [Fact]
        public void AlertMessage_DiskReverted_TellsPlayerToReload()
        {
            var info = new InGameTestRunner.FlightBatchBaselineState_SlotInfo
            {
                SlotName = "slot", CapturedScene = GameScenes.TRACKSTATION,
            };
            string msg = InGameTestRunner.BuildRestoreDegradedAlertMessage(info, "x", diskFullyReverted: true);
            Assert.Contains("WAS reverted", msg);
            Assert.DoesNotContain("could NOT be fully reverted", msg);
        }

        [Fact]
        public void AlertMessage_DiskPartial_TellsPlayerManualRecovery()
        {
            var info = new InGameTestRunner.FlightBatchBaselineState_SlotInfo
            {
                SlotName = "slot", CapturedScene = GameScenes.SPACECENTER,
            };
            string msg = InGameTestRunner.BuildRestoreDegradedAlertMessage(info, "x", diskFullyReverted: false);
            Assert.Contains("could NOT be fully reverted", msg);
            Assert.Contains("preserved for manual recovery", msg);
        }

        [Fact]
        public void AlertMessage_NullSlotAndFailure_DoesNotThrow()
        {
            var info = new InGameTestRunner.FlightBatchBaselineState_SlotInfo
            {
                SlotName = null, CapturedScene = GameScenes.FLIGHT,
            };
            string msg = InGameTestRunner.BuildRestoreDegradedAlertMessage(info, null, diskFullyReverted: true);
            Assert.Contains("(unknown)", msg);
            Assert.Contains("(none)", msg);
        }

        // ----- Capture-before-durable-marker source-text ordering contract -----
        // Mirrors ChainSaveLoadTests.ChainStateNotPersistedInScenario: a source gate
        // pinning the slot-and-bak-before-durable-marker invariant the same-process
        // guard depends on (reviewer 1's fragility concern). The marker MUST be
        // written only AFTER the clean .bak / quicksave slot are captured, and the
        // durable persistent save MUST fire only AFTER the marker object is built.

        private static string ReadSource(string fileRelativeUnderSource)
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", fileRelativeUnderSource);
            if (!File.Exists(path))
                path = Path.Combine(projectRoot, "Parsek", fileRelativeUnderSource);
            Assert.True(File.Exists(path), $"source not found at {path}");
            return File.ReadAllText(path);
        }

        [Fact]
        public void CaptureBaseline_CapturesBeforeWritingMarker()
        {
            string source = ReadSource(Path.Combine("InGameTests", "InGameTestRunner.cs"));

            int captureMethodStart = source.IndexOf(
                "private void CaptureBatchBaseline(", StringComparison.Ordinal);
            Assert.True(captureMethodStart >= 0, "CaptureBatchBaseline method not found");

            // Within CaptureBatchBaseline, the capture calls must textually precede
            // the WriteTestBatchMarker(mode) call.
            int captureCall = source.IndexOf("CaptureFlightBatchBaseline()", captureMethodStart, StringComparison.Ordinal);
            int backupCall = source.IndexOf("BackupCampaignPersistentSave(", captureMethodStart, StringComparison.Ordinal);
            int writeMarkerCall = source.IndexOf("WriteTestBatchMarker(mode)", captureMethodStart, StringComparison.Ordinal);

            Assert.True(captureCall >= 0, "CaptureFlightBatchBaseline() call not found in CaptureBatchBaseline");
            Assert.True(backupCall >= 0, "BackupCampaignPersistentSave(...) call not found in CaptureBatchBaseline");
            Assert.True(writeMarkerCall >= 0, "WriteTestBatchMarker(mode) call not found in CaptureBatchBaseline");

            Assert.True(captureCall < writeMarkerCall,
                "CaptureFlightBatchBaseline() must precede WriteTestBatchMarker(mode)");
            Assert.True(backupCall < writeMarkerCall,
                "BackupCampaignPersistentSave(...) must precede WriteTestBatchMarker(mode)");
        }

        [Fact]
        public void WriteMarker_BuildsMarkerBeforeDurableSave()
        {
            string source = ReadSource(Path.Combine("InGameTests", "InGameTestRunner.cs"));

            int writeMethodStart = source.IndexOf(
                "private void WriteTestBatchMarker(", StringComparison.Ordinal);
            Assert.True(writeMethodStart >= 0, "WriteTestBatchMarker method not found");

            int newMarker = source.IndexOf("new TestBatchMarker", writeMethodStart, StringComparison.Ordinal);
            int durableSave = source.IndexOf("TriggerCampaignPersistentSave()", writeMethodStart, StringComparison.Ordinal);

            Assert.True(newMarker >= 0, "new TestBatchMarker not found in WriteTestBatchMarker");
            Assert.True(durableSave >= 0, "TriggerCampaignPersistentSave() not found in WriteTestBatchMarker");
            Assert.True(newMarker < durableSave,
                "new TestBatchMarker must be built BEFORE TriggerCampaignPersistentSave()");
        }

        // ----- Crash-reconcile sidecar/ledger source-text contracts -----
        //
        // The crash finisher is Unity/scene-bound (GamePersistence.LoadGame +
        // coroutine), so its correctness invariants are pinned as source-text
        // ordering gates here, mirroring the capture-before-marker gates above.

        [Fact]
        public void WriteMarker_CarriesParsekSnapshotDir()
        {
            // The marker MUST carry the on-disk Parsek/ sidecar snapshot dir so the
            // crash finisher (a new process) can revert the ledger's events.pgse.
            string source = ReadSource(Path.Combine("InGameTests", "InGameTestRunner.cs"));
            int writeMethodStart = source.IndexOf(
                "private void WriteTestBatchMarker(", StringComparison.Ordinal);
            Assert.True(writeMethodStart >= 0, "WriteTestBatchMarker method not found");
            int assign = source.IndexOf("ParsekSnapshotDir = ", writeMethodStart, StringComparison.Ordinal);
            Assert.True(assign >= 0,
                "WriteTestBatchMarker must set TestBatchMarker.ParsekSnapshotDir");
            int snapshotSource = source.IndexOf(
                "batchFlightBaseline?.ParsekSaveSnapshotDirectory", assign, StringComparison.Ordinal);
            Assert.True(snapshotSource >= 0 && snapshotSource - assign < 80,
                "ParsekSnapshotDir must be set from batchFlightBaseline?.ParsekSaveSnapshotDirectory");
        }

        [Fact]
        public void CrashReconcile_RevertsSidecarsAndForcesColdReload_BeforeSchedulingDeferredReload()
        {
            string source = ReadSource("ParsekScenario.cs");
            int coreStart = source.IndexOf(
                "private void RunTestBatchCrashReconcileCore(", StringComparison.Ordinal);
            Assert.True(coreStart >= 0, "RunTestBatchCrashReconcileCore method not found");

            int persistentRevert = source.IndexOf(
                "RestoreCampaignPersistentSaveOnReconcile(", coreStart, StringComparison.Ordinal);
            int sidecarRevert = source.IndexOf(
                "RestoreParsekSidecarDirOnReconcile(", coreStart, StringComparison.Ordinal);
            int forceColdReload = source.IndexOf(
                "PrepareInMemoryStateForTestBatchCrashReload(", coreStart, StringComparison.Ordinal);
            int scheduleDeferred = source.IndexOf(
                "StartCoroutine(DeferredReloadAfterTestBatchCrashReconcile(", coreStart, StringComparison.Ordinal);

            Assert.True(persistentRevert >= 0, "persistent.sfs revert call not found in core");
            Assert.True(sidecarRevert >= 0, "Parsek/ sidecar revert call not found in core");
            Assert.True(forceColdReload >= 0, "cold-reload prep call not found in core");
            Assert.True(scheduleDeferred >= 0, "deferred reload schedule not found in core");

            // Sidecar revert (the LEDGER) and the cold-reload prep MUST both precede
            // the deferred reload, so the reload's OnLoad re-loads the ledger from the
            // clean events.pgse instead of recalc'ing from the stale in-memory ledger.
            Assert.True(sidecarRevert < scheduleDeferred,
                "Parsek/ sidecar revert must precede the deferred reload");
            Assert.True(forceColdReload < scheduleDeferred,
                "cold-reload prep (initialLoadDone=false) must precede the deferred reload");
        }

        [Fact]
        public void CrashReconcile_ColdReloadPrep_ResetsLedgerAndInitialLoadFlag()
        {
            string source = ReadSource("ParsekScenario.cs");
            int prepStart = source.IndexOf(
                "private static void PrepareInMemoryStateForTestBatchCrashReload(",
                StringComparison.Ordinal);
            Assert.True(prepStart >= 0, "PrepareInMemoryStateForTestBatchCrashReload not found");
            // Bound the search to the method body.
            int prepEnd = source.IndexOf("private static", prepStart + 1, StringComparison.Ordinal);
            string body = source.Substring(prepStart, (prepEnd > prepStart ? prepEnd : source.Length) - prepStart);

            Assert.Contains("initialLoadDone = false", body);
            Assert.Contains("LedgerOrchestrator.ResetForTesting()", body);
            Assert.Contains("GameStateStore.ResetForTesting()", body);
            Assert.Contains("MilestoneStore.ResetForTesting()", body);
            Assert.Contains("GameStateRecorder.ResetForTesting()", body);
        }

        [Fact]
        public void CrashReconcile_DeferredReload_SweepsArtifactsAfterReload()
        {
            // The artifact sweep (which deletes the snapshot) must run INSIDE the
            // deferred reload coroutine, AFTER the reload commits, never before it.
            // Deleting the snapshot before the reload would destroy the clean ledger
            // source (the BUG-F regression this fix closes).
            string source = ReadSource("ParsekScenario.cs");
            int deferredStart = source.IndexOf(
                "DeferredReloadAfterTestBatchCrashReconcile(\n", StringComparison.Ordinal);
            if (deferredStart < 0)
                deferredStart = source.IndexOf(
                    "IEnumerator DeferredReloadAfterTestBatchCrashReconcile(", StringComparison.Ordinal);
            Assert.True(deferredStart >= 0, "DeferredReloadAfterTestBatchCrashReconcile not found");
            int deferredEnd = source.IndexOf(
                "private static void TryDeletePersistentBackupAndSnapshotArtifacts(",
                deferredStart, StringComparison.Ordinal);
            Assert.True(deferredEnd > deferredStart, "deferred reload method end not found");
            string body = source.Substring(deferredStart, deferredEnd - deferredStart);

            int loadGame = body.IndexOf("GamePersistence.LoadGame(", StringComparison.Ordinal);
            int sweep = body.IndexOf(
                "TryDeletePersistentBackupAndSnapshotArtifacts(", StringComparison.Ordinal);
            Assert.True(loadGame >= 0, "deferred reload must call GamePersistence.LoadGame");
            Assert.True(sweep >= 0,
                "deferred reload must sweep artifacts after the reload");
            Assert.True(loadGame < sweep,
                "artifact sweep must run AFTER the reload, not before");
        }

        [Fact]
        public void CrashReconcile_CoreWrappedInDefensiveTryCatch()
        {
            // Nit N1: the reconcile must never throw out of OnLoad.
            string source = ReadSource("ParsekScenario.cs");
            int wrapperStart = source.IndexOf(
                "private void RunTestBatchCrashReconcile()", StringComparison.Ordinal);
            Assert.True(wrapperStart >= 0, "RunTestBatchCrashReconcile wrapper not found");
            int wrapperEnd = source.IndexOf(
                "private void RunTestBatchCrashReconcileCore(", wrapperStart, StringComparison.Ordinal);
            Assert.True(wrapperEnd > wrapperStart, "wrapper end not found");
            string body = source.Substring(wrapperStart, wrapperEnd - wrapperStart);
            Assert.Contains("try", body);
            Assert.Contains("RunTestBatchCrashReconcileCore()", body);
            Assert.Contains("catch (Exception", body);
        }
    }
}
