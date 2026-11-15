using System;

namespace Parsek
{
    /// <summary>
    /// Singleton ScenarioModule entry marking that an in-game test batch is in
    /// progress. Written into the campaign persistent.sfs before a batch starts
    /// (alongside a clean persistent.sfs .bak), cleared on clean completion / cancel
    /// (by reverting persistent.sfs from the marker-free .bak). Its presence on the
    /// NEXT OnLoad in a DIFFERENT process means the batch was interrupted (crash,
    /// hard quit, KSP kill) before it could revert the campaign. The OnLoad finisher
    /// (ParsekScenario.RunTestBatchCrashReconcile) then (1) reverts persistent.sfs from
    /// the .bak, (2) restores the live Parsek/ save-scoped sidecar dir (where the LEDGER
    /// events.pgse lives) from <see cref="ParsekSnapshotDir"/>, (3) forces a clean
    /// in-memory reload (initialLoadDone=false + the success-path ResetForTesting set) so
    /// the deferred reload's OnLoad re-loads the ledger from the now-clean events.pgse,
    /// and (4) schedules a real in-memory reload of the reverted persistent.sfs. A bare
    /// disk overwrite is insufficient: the in-memory ledger is loaded earlier in OnLoad
    /// from the still-mutated sidecars and the deferred-seed recalc would re-patch the
    /// live career from that mutated ledger, so the sidecar dir must be reverted and the
    /// ledger reloaded from disk, not recalc'd from stale statics. Mirrors the
    /// ReFlySessionMarker + OnLoad-finisher idiom.
    /// </summary>
    public class TestBatchMarker
    {
        internal const string NodeName = "PARSEK_TEST_BATCH_MARKER";

        /// <summary>AppDomain identity at batch start (ParsekProcess.ProcessSessionId,
        /// "N" format). A process MISMATCH on load == the writing process died ==
        /// crash recovery. A same-process load (per-test/final baseline-slot reload,
        /// between-batch load) does NOT auto-restore; the in-process teardown owns it.</summary>
        public string ProcessSessionId;

        /// <summary>Per-batch token (defensive: distinct from ProcessSessionId so a
        /// reused/forked process cannot collide; diagnostics + future guard).</summary>
        public string BatchInstanceId;

        /// <summary>Absolute path of the clean persistent.sfs .bak taken at batch start
        /// (before the durable-marker save). The finisher reverts persistent.sfs from this.</summary>
        public string PersistentBackupPath;

        /// <summary>Absolute path of the on-disk Parsek/ save-scoped sidecar snapshot
        /// (the <c>&lt;slot&gt;-parsek</c> directory) taken at batch start. The LEDGER
        /// (<c>Parsek/GameState/events.pgse</c>) and all other sidecar state live here.
        /// The crash finisher restores the live <c>Parsek/</c> dir from this BEFORE the
        /// deferred reload so the reloaded in-memory ledger comes from the clean snapshot,
        /// not the test-mutated live sidecars. Null in DiskOnly mode (no snapshot taken)
        /// and on autosave-disabled runs that never persisted the marker. The snapshot is
        /// a durable directory under <c>saves/&lt;save&gt;/</c> that survives a process kill.</summary>
        public string ParsekSnapshotDir;

        /// <summary>Save folder the batch ran against. Guards against restoring into the
        /// wrong save if the player loaded a different campaign after a crash.</summary>
        public string SaveFolder;

        /// <summary>Scene the batch was captured in (diagnostics).</summary>
        public string CapturedScene;

        /// <summary>Wall-clock batch start (ISO 8601 UTC; diagnostics).</summary>
        public string StartedRealTime;

        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("processSessionId", ProcessSessionId ?? "");
            node.AddValue("batchInstanceId", BatchInstanceId ?? "");
            node.AddValue("persistentBackupPath", PersistentBackupPath ?? "");
            node.AddValue("parsekSnapshotDir", ParsekSnapshotDir ?? "");
            node.AddValue("saveFolder", SaveFolder ?? "");
            node.AddValue("capturedScene", CapturedScene ?? "");
            if (!string.IsNullOrEmpty(StartedRealTime))
                node.AddValue("startedRealTime", StartedRealTime);
        }

        public static TestBatchMarker LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var m = new TestBatchMarker();
            m.ProcessSessionId = NullIfEmpty(node.GetValue("processSessionId"));
            m.BatchInstanceId = NullIfEmpty(node.GetValue("batchInstanceId"));
            m.PersistentBackupPath = NullIfEmpty(node.GetValue("persistentBackupPath"));
            m.ParsekSnapshotDir = NullIfEmpty(node.GetValue("parsekSnapshotDir"));
            m.SaveFolder = NullIfEmpty(node.GetValue("saveFolder"));
            m.CapturedScene = node.GetValue("capturedScene");
            m.StartedRealTime = node.GetValue("startedRealTime");
            return m;
        }

        private static string NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

        /// <summary>
        /// Pure crash-reconcile decision. Auto-restore fires ONLY when the marker
        /// belongs to a DIFFERENT process than the current one (mismatch => writer
        /// died) AND the save folder matches the currently loaded save AND a backup
        /// path is present. A same-process load does NOT auto-restore (in-process
        /// teardown owns it). Returns the reason for diagnostics.
        /// </summary>
        internal static bool ShouldReconcileOnLoad(
            TestBatchMarker marker, string currentProcessSessionId,
            string currentSaveFolder, out string reason)
        {
            if (marker == null) { reason = "no-marker"; return false; }
            if (string.IsNullOrEmpty(marker.PersistentBackupPath)) { reason = "no-backup-path"; return false; }
            if (!string.IsNullOrEmpty(marker.SaveFolder)
                && !string.IsNullOrEmpty(currentSaveFolder)
                && !string.Equals(marker.SaveFolder, currentSaveFolder, StringComparison.Ordinal))
            { reason = "save-folder-mismatch"; return false; }
            if (!string.IsNullOrEmpty(marker.ProcessSessionId)
                && string.Equals(marker.ProcessSessionId, currentProcessSessionId, StringComparison.Ordinal))
            { reason = "same-process-no-crash"; return false; }
            reason = "interrupted-batch-crash-recovery";
            return true;
        }
    }
}
