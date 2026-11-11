using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek
{
    // In-game test batch isolation: the mid-batch crash reconcile, its deferred real reload and
    // the isolated-batch flight baseline restore.
    public partial class ParsekScenario
    {
        /// <summary>
        /// OnLoad crash-reconcile finisher for the in-game test runner's campaign
        /// isolation. Mirrors the re-fly marker + OnLoad-finisher idiom: a
        /// <see cref="TestBatchMarker"/> persisted into persistent.sfs before a
        /// batch started means, on the NEXT OnLoad in a DIFFERENT process, that the
        /// batch was interrupted (crash, hard quit, KSP kill) before it could revert
        /// the campaign. The finisher reverts persistent.sfs from the clean .bak AND
        /// schedules a real deferred reload so the in-memory game + ledger come from
        /// the .bak (a bare disk overwrite is insufficient: ledger-load + the
        /// deferred-seed recalc have already patched the live career from the mutated
        /// save in THIS pass, and the next autosave would re-clobber the .bak). NEVER
        /// calls SaveGame from inside OnLoad.
        /// </summary>
        private void RunTestBatchCrashReconcile()
        {
            // Nit N1: the entire reconcile is best-effort and must NEVER throw out
            // of OnLoad. A reconcile that throws would abort the whole OnLoad after
            // the ledger/recordings have already loaded; on the crash path the disk
            // is left whatever the partial revert achieved (warned below) and the
            // .bak/snapshot are preserved for manual recovery.
            try
            {
                RunTestBatchCrashReconcileCore();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch",
                    "crash-reconcile threw and was swallowed to protect OnLoad; the campaign "
                    + "may be partially reverted. Load the save manually to recover. detail: "
                    + ex.Message);
            }
        }

        private void RunTestBatchCrashReconcileCore()
        {
            var marker = ActiveTestBatchMarker;
            string reason;
            bool shouldReconcile = TestBatchMarker.ShouldReconcileOnLoad(
                marker, ParsekProcess.ProcessSessionId.ToString("N"),
                HighLogic.SaveFolder, out reason);

            if (!shouldReconcile)
            {
                if (marker != null && reason == "same-process-no-crash")
                {
                    // In-process load between/within batches; the in-process teardown
                    // owns cleanup. Do NOT clear (a running batch still needs the
                    // disk marker).
                    ParsekLog.Verbose("TestBatch", $"crash-reconcile skipped: {reason}");
                }
                else if (marker != null)
                {
                    // Stale / wrong-save / no-backup-path marker: drop the in-memory
                    // copy (persisted out clean on the next OnSave); do NOT touch
                    // persistent.sfs.
                    ParsekLog.Warn("TestBatch", $"crash-reconcile clearing stale marker: {reason}");
                    ActiveTestBatchMarker = null;
                }
                return;
            }

            ParsekLog.Warn("TestBatch",
                $"Interrupted test batch detected ({reason}); reverting persistent.sfs from "
                + $"'{marker.PersistentBackupPath}' and the Parsek/ sidecar dir from "
                + $"'{marker.ParsekSnapshotDir ?? "(none)"}', then scheduling a clean reload");

            // 1. Revert the on-disk persistent.sfs from the clean (marker-free) .bak.
            bool diskReverted = RestoreCampaignPersistentSaveOnReconcile(marker.PersistentBackupPath);

            // 2. Revert the live Parsek/ save-scoped sidecar dir (the LEDGER
            //    events.pgse + all other sidecar state) from the batch-start
            //    snapshot, BEFORE the deferred reload and BEFORE any artifact
            //    deletion. The deferred reload's cold OnLoad re-loads the ledger
            //    from this now-clean events.pgse (see step 4). DiskOnly-mode markers
            //    carry no snapshot (ParsekSnapshotDir == null) — the helper no-ops
            //    and returns true (nothing to restore). On a non-null snapshot that
            //    fails to restore we do NOT reload (a stale-ledger reload would
            //    re-corrupt the career); the .bak + snapshot are preserved below.
            bool sidecarsReverted = RestoreParsekSidecarDirOnReconcile(marker.ParsekSnapshotDir);

            // 3. Force a clean in-memory reload for the deferred reload's OnLoad:
            //    set initialLoadDone=false and run the SAME *.ResetForTesting() set
            //    the success path (PrepareForIsolatedBatchFlightBaselineRestore)
            //    uses, so the next OnLoad takes the COLD branch and re-loads the
            //    ledger from the now-clean events.pgse instead of the scene-change
            //    branch (which skips LoadExternalFiles/LedgerOrchestrator.OnLoad and
            //    would RecalculateAndPatch from the STALE in-memory ledger). Only do
            //    this when both disk surfaces are clean — otherwise we leave the
            //    in-memory state as-is and skip the reload entirely.
            bool reloadable = diskReverted && sidecarsReverted;
            if (reloadable)
            {
                // Unsubscribe the live recorder this scenario instance subscribed
                // earlier in OnLoad (mirrors the success path's unsubscribeLiveRecorder
                // callback) so the wiped GameStateRecorder statics are not re-fed by a
                // still-live subscription before the deferred reload rebuilds it.
                stateRecorder?.Unsubscribe();
                PrepareInMemoryStateForTestBatchCrashReload();
            }

            // 4. Clear the in-memory marker so this OnLoad pass does not re-trigger
            //    and the deferred reload's OnLoad sees no marker.
            ActiveTestBatchMarker = null;

            // 5. Schedule a one-frame-deferred REAL reload of the now-clean
            //    persistent.sfs so the in-memory game + ledger come from the .bak +
            //    snapshot (NOT just the disk files). A bare disk overwrite is
            //    insufficient: ledger-load (cold OnLoad) and DeferredSeedAndRecalculate
            //    have already loaded/patched the live career from the MUTATED save in
            //    THIS pass, and the next autosave would re-clobber the reverted .bak.
            //    The artifact sweep (.bak + slot + snapshot) is performed INSIDE the
            //    deferred coroutine, AFTER the reload has consumed them, so the
            //    snapshot survives until the clean reload reads it. Do NOT call
            //    SaveGame from inside OnLoad.
            if (reloadable)
            {
                // [M-A3 G3] Arm the H1 settle gate: hold autorun fire until the
                // deferred reload completes so the batch baseline is captured against
                // the reverted save, not this half-reverted OnLoad pass.
                CrashReconcileInProgress = true;
                ParsekLog.Info("TestBatch", $"crash-reconcile in progress: {reason}");
                StartCoroutine(DeferredReloadAfterTestBatchCrashReconcile(HighLogic.SaveFolder, marker));
            }
            else
            {
                // [M-A3 FIX 2] The marker was already nulled above (step 4), so H1's
                // MarkerWouldReconcile now reports false - but the on-disk save is only
                // HALF reverted here (disk revert incomplete) and NO deferred reload will
                // clean it. Leaving the H1 settle gate open would fire an autorun batch
                // against the mutated save and capture a poisoned baseline. Set the
                // in-progress flag so ReconcileGateClear holds fire FOREVER; unlike the
                // reloadable path there is no deferred-reload completion to clear it, so
                // the external orchestrator's timeout is the intended reaper.
                CrashReconcileInProgress = true;
                ParsekLog.Warn("TestBatch",
                    "crash-reconcile: disk revert incomplete "
                    + $"(persistent={diskReverted}, sidecars={sidecarsReverted}); skipping deferred "
                    + "reload and preserving the .bak + snapshot for manual recovery. Holding the "
                    + "autorun H1 gate closed indefinitely (no deferred reload will clear it; the "
                    + "orchestrator timeout is the reaper) so no autorun batch runs against the "
                    + "half-reverted save");
            }
        }

        /// <summary>Reverts the live save-scoped Parsek/ sidecar dir (where the ledger
        /// events.pgse lives) from the batch-start snapshot directory. OnLoad-safe (no
        /// SaveGame). Mirrors InGameTestRunner.RestoreParsekSidecarsFromSnapshot but is
        /// duplicated here so production code carries no dependency on the InGameTests
        /// namespace. A null/missing snapshot (DiskOnly mode) is a no-op success.
        /// Idempotent recursive replace: deletes the live dir then copies the snapshot
        /// in. The snapshot dir itself is left in place for the deferred reload + the
        /// post-reload artifact sweep.</summary>
        private static bool RestoreParsekSidecarDirOnReconcile(string snapshotDir)
        {
            if (string.IsNullOrEmpty(snapshotDir))
                return true; // DiskOnly mode: no sidecar snapshot to restore
            try
            {
                if (!System.IO.Directory.Exists(snapshotDir))
                {
                    ParsekLog.Warn("TestBatch",
                        $"crash-reconcile: Parsek/ snapshot missing at '{snapshotDir}'");
                    return false;
                }
                string liveParsekDir = RecordingPaths.ResolveSaveScopedPath("Parsek");
                if (string.IsNullOrEmpty(liveParsekDir))
                {
                    ParsekLog.Warn("TestBatch",
                        "crash-reconcile: could not resolve live save-scoped Parsek/ dir");
                    return false;
                }
                if (System.IO.Directory.Exists(liveParsekDir))
                    DeleteDirectoryRecursiveOnReconcile(liveParsekDir);
                CopyDirectoryRecursiveOnReconcile(snapshotDir, liveParsekDir);
                ParsekLog.Info("TestBatch",
                    $"crash-reconcile: reverted Parsek/ sidecar dir from snapshot '{snapshotDir}'");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch",
                    $"crash-reconcile: Parsek/ sidecar revert threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Forces the deferred reload's OnLoad onto the COLD branch so it
        /// re-loads the ledger from the now-clean events.pgse. Mirrors the success
        /// path's PrepareForIsolatedBatchFlightBaselineRestore reset set (minus the
        /// recorder-unsubscribe + RecordingStore guard-bypass wipe, which the cold
        /// OnLoad's own ClearCommittedInternal already covers). Pure in-memory; no
        /// SaveGame.</summary>
        private static void PrepareInMemoryStateForTestBatchCrashReload()
        {
            ParsekLog.Info("TestBatch",
                "crash-reconcile: forcing cold in-memory reload (initialLoadDone=false + "
                + "ResetForTesting set) so the deferred reload re-loads the ledger from the "
                + "clean events.pgse");
            initialLoadDone = false;
            committedTreeStateLoaded = false;
            budgetDeductionApplied = false;
            mergeDialogPending = false;
            pendingActiveTreeResumeRewindSave = null;
            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.None;
            pendingFreshRecordingAfterDiscard = null;
            vesselSwitchPending = false;
            vesselSwitchPendingFrame = -1;

            RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        /// <summary>Recursive directory copy, OnLoad-safe (no SaveGame). Duplicated
        /// here (not shared with InGameTests) so production code carries no dependency
        /// on the test namespace.</summary>
        private static void CopyDirectoryRecursiveOnReconcile(string sourceDir, string destDir)
        {
            System.IO.Directory.CreateDirectory(destDir);
            foreach (string dir in System.IO.Directory.GetDirectories(
                sourceDir, "*", System.IO.SearchOption.AllDirectories))
            {
                string rel = dir.Substring(sourceDir.Length)
                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(destDir, rel));
            }
            foreach (string file in System.IO.Directory.GetFiles(
                sourceDir, "*", System.IO.SearchOption.AllDirectories))
            {
                string rel = file.Substring(sourceDir.Length)
                    .TrimStart(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                string destFile = System.IO.Path.Combine(destDir, rel);
                string destParent = System.IO.Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                    System.IO.Directory.CreateDirectory(destParent);
                System.IO.File.Copy(file, destFile, overwrite: true);
            }
        }

        private static void DeleteDirectoryRecursiveOnReconcile(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir))
                return;
            foreach (string file in System.IO.Directory.GetFiles(
                dir, "*", System.IO.SearchOption.AllDirectories))
            {
                try { System.IO.File.SetAttributes(file, System.IO.FileAttributes.Normal); }
                catch { /* best-effort attribute clear */ }
            }
            System.IO.Directory.Delete(dir, recursive: true);
        }

        /// <summary>Reverts persistent.sfs from the .bak bytes (no SaveGame;
        /// OnLoad-safe). Mirrors InGameTestRunner.RestoreCampaignPersistentSave but
        /// lives here so production code carries no dependency on the InGameTests
        /// assembly namespace.</summary>
        private static bool RestoreCampaignPersistentSaveOnReconcile(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath)) return false;
            try
            {
                if (!System.IO.File.Exists(backupPath))
                {
                    ParsekLog.Warn("TestBatch", $"crash-reconcile: .bak missing at '{backupPath}'");
                    return false;
                }
                string persistentPath = RecordingPaths.ResolveSaveScopedPath("persistent.sfs");
                if (string.IsNullOrEmpty(persistentPath)) return false;
                byte[] bytes = System.IO.File.ReadAllBytes(backupPath);
                FileIOUtils.SafeWriteBytes(bytes, persistentPath, "TestBatch");
                ParsekLog.Info("TestBatch",
                    $"crash-reconcile: reverted persistent.sfs ({bytes.Length} bytes) from .bak");
                return true;
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch", $"crash-reconcile: persistent.sfs revert threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>Best-effort crash-path artifact sweep: deletes the .bak named in
        /// the marker and derives + deletes the sibling slot .sfs / .loadmeta /
        /// -parsek/ snapshot (slot name = .bak filename minus the "-persistent.bak"
        /// suffix).</summary>
        private static void TryDeletePersistentBackupAndSnapshotArtifacts(TestBatchMarker marker)
        {
            try
            {
                string bak = marker?.PersistentBackupPath;
                if (string.IsNullOrEmpty(bak)) return;
                string savesDir = System.IO.Path.GetDirectoryName(bak);
                string bakName = System.IO.Path.GetFileName(bak); // "<slot>-persistent.bak"
                const string suffix = "-persistent.bak";
                string slot = bakName.EndsWith(suffix, StringComparison.Ordinal)
                    ? bakName.Substring(0, bakName.Length - suffix.Length)
                    : null;

                TryDeleteFileQuiet(bak);
                if (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(savesDir))
                {
                    TryDeleteFileQuiet(System.IO.Path.Combine(savesDir, slot + ".sfs"));
                    TryDeleteFileQuiet(System.IO.Path.Combine(savesDir, slot + ".loadmeta"));
                }
                // Delete the snapshot dir the marker actually recorded (authoritative),
                // falling back to the slot-derived sibling path for older markers that
                // predate the ParsekSnapshotDir field.
                string snapshotDir = !string.IsNullOrEmpty(marker.ParsekSnapshotDir)
                    ? marker.ParsekSnapshotDir
                    : (!string.IsNullOrEmpty(slot) && !string.IsNullOrEmpty(savesDir)
                        ? System.IO.Path.Combine(savesDir, slot + "-parsek")
                        : null);
                if (!string.IsNullOrEmpty(snapshotDir) && System.IO.Directory.Exists(snapshotDir))
                    System.IO.Directory.Delete(snapshotDir, recursive: true);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestBatch", $"crash-reconcile artifact sweep partial: {ex.Message}");
            }
        }

        private static void TryDeleteFileQuiet(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
            catch (Exception ex) { ParsekLog.Warn("TestBatch", $"crash-reconcile delete '{path}' failed: {ex.Message}"); }
        }

        /// <summary>
        /// In-game test helper: clears save-scoped in-memory state before an isolated
        /// FLIGHT baseline quickload restore so the next OnLoad uses the cold-load
        /// path and rebuilds Parsek state from the restored save + sidecars.
        /// </summary>
        internal void UnsubscribeStateRecorderForIsolatedBatchFlightBaselineRestore()
        {
            stateRecorder?.Unsubscribe();
        }

        internal static void PrepareForIsolatedBatchFlightBaselineRestore(
            Action unsubscribeLiveRecorder = null,
            Action onWipeStart = null)
        {
            ParsekLog.Info("Scenario",
                "Preparing save-scoped state for isolated FLIGHT batch baseline restore");

            unsubscribeLiveRecorder?.Invoke();
            initialLoadDone = false;
            committedTreeStateLoaded = false;
            budgetDeductionApplied = false;
            mergeDialogPending = false;
            pendingActiveTreeResumeRewindSave = null;
            ScheduleActiveTreeRestoreOnFlightReady = ActiveTreeRestoreMode.None;
            pendingFreshRecordingAfterDiscard = null;
            vesselSwitchPending = false;
            vesselSwitchPendingFrame = -1;

            // onWipeStart fires IMMEDIATELY before the first in-memory
            // store reset. This is the destructive boundary -- callers
            // wire this to arm their rollback flag here, not after this
            // method returns, because the eight Reset calls below are not
            // atomic. If GroupHierarchyStore.ResetForTesting (or any
            // later reset) throws, RecordingStore has already been wiped
            // and the caller's rollback must fire to recover live data.
            // Arming after this method returned would leave the rollback
            // disabled for that partial-failure window.
            onWipeStart?.Invoke();

            // The next operation after this prep is a quickload from the
            // baseline save slot, which restores RecordingStore from disk
            // via OnLoad. The in-memory wipe is transient. Use the
            // explicit guard-bypassing variant -- ResetForTesting() throws
            // when committedRecordings/Trees are non-empty (the
            // PersistenceSplitOptimizerTest 2026-05-01 bug guard), and any
            // batch FLIGHT test running on a save with live recordings hits
            // that throw on the prep step before its body runs.
            RecordingStore.ResetForBatchFlightBaselineRestoreBypassingGuard();
            GroupHierarchyStore.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RevertDetector.ResetForTesting();
        }
    }
}
