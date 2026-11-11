using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Rewind launcher (RecordingStore services plan, service O): the plain rewind
    /// (Rewind-to-Launch and Warp-to-game-start) from the gate to the scene load. The
    /// can-rewind / can-fast-forward gates, the two entry points and their shared save-copy /
    /// pre-process / LoadGame / Space Center load, the rewind-save pre-processing (vessel strip
    /// and lead-time windback), the persistent.sfs writes around the load
    /// (<c>RewindLauncher.PersistentWrite.cs</c>), the playback-state reset a rewind runs on
    /// every committed recording, and the one-shot cleanup sets the OnLoad revert / rewind
    /// path arms for <c>ParsekFlight.OnFlightReady</c>. The rewind context itself stays in
    /// <see cref="RewindContext"/>, and the deferred UT-adjustment flags stay on
    /// <see cref="RecordingStore"/>: the ghost map presence, the KSC and tracking-station
    /// hosts and the kerbal walk read them, and those reads would put this type on their cycles.
    /// </summary>
    internal static partial class RewindLauncher
    {
        /// <summary>
        /// Save-scoped reset (<see cref="RecordingStore.ResetSaveScopedStateForLoad"/>): drops the
        /// one-shot cleanup sets, as the store did before they moved here.
        /// </summary>
        internal static void ResetForLoad()
        {
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
        }

        /// <summary>
        /// Test reset (<see cref="RecordingStore.ResetForTesting"/>): the same fields as
        /// <see cref="ResetForLoad"/>. The two test seams stay as the test left them.
        /// </summary>
        internal static void ResetForTesting()
        {
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
        }

        /// <summary>
        /// Resets all playback state on committed recordings.
        /// Called during rewind to prepare all recordings for fresh replay.
        /// <paramref name="keepSpawnStateRecordingIds"/> names recordings whose spawned vessel
        /// the rewind kept as committed history (<see cref="RewindSpawnScope.ResolveRewindSpawnStripScope"/>):
        /// their spawn state (VesselSpawned / SpawnedVesselPersistentId and the spawn
        /// bookkeeping) is preserved so the kept vessel stays linked to its recording, the
        /// same way a revert restores an earlier tree's spawn state from the revert target.
        /// The post-strip reconcile still resets any of them whose vessel did not survive.
        /// </summary>
        internal static (int recordingCount, int treeCount) ResetAllPlaybackState(
            HashSet<string> keepSpawnStateRecordingIds = null)
        {
            TreeCommitService.MarkSupersededTerminalSpawnsForCommittedContinuations("ResetAllPlaybackState");

            var keptLogged = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
                ResetRecordingPlaybackFields(RecordingStore.CommittedRecordings[i], keepSpawnStateRecordingIds, keptLogged);

            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                foreach (var rec in RecordingStore.CommittedTrees[i].Recordings.Values)
                    ResetRecordingPlaybackFields(rec, keepSpawnStateRecordingIds, keptLogged);
            }

            if (!RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Playback state reset: {RecordingStore.CommittedRecordings.Count} recording(s), {RecordingStore.CommittedTrees.Count} tree(s), " +
                    $"keptHistorySpawnState={keptLogged.Count}");

            return (RecordingStore.CommittedRecordings.Count, RecordingStore.CommittedTrees.Count);
        }

        internal static void ResetRecordingPlaybackFields(
            Recording rec,
            HashSet<string> keepSpawnStateRecordingIds = null,
            HashSet<string> keptLogged = null)
        {
            // The terminal verdict is NOT touched: a committed recording's
            // TerminalStateValue is recorded content (real-vessel terminal events stamp
            // only the pending tree), and Destroyed / Recovered are never spawnable, so
            // the spawn fields below are all a re-spawn needs. Same rule as the OnLoad
            // tree-mutable-state reset (ParsekScenario.ResetTreeRecordingMutableStateForLoad).

            bool keepSpawnState = ShouldKeepSpawnState(keepSpawnStateRecordingIds, rec.RecordingId);
            if (keepSpawnState)
            {
                // Committed history the rewind kept: its vessel exists at the rewind target
                // and nothing that replays spawns it again, so its spawn link is kept too.
                if (keptLogged != null && keptLogged.Add(rec.RecordingId) && !RecordingStore.SuppressLogging)
                    ParsekLog.Verbose("Rewind",
                        $"Playback reset kept spawn state for committed history \"{rec.VesselName}\" " +
                        $"id={rec.RecordingId} spawnedPid={rec.SpawnedVesselPersistentId} vesselSpawned={rec.VesselSpawned}");
            }
            else
            {
                ClearSpawnState(rec);
            }
            rec.LastAppliedResourceIndex = -1;
            // SpawnSuppressedByRewind is cleared here so a subsequent rewind starts
            // from a clean slate. ParsekScenario.HandleRewindOnLoad re-marks the
            // active/source recording AFTER ResetAllPlaybackState fires.
            if (rec.SpawnSuppressedByRewind && !RecordingStore.SuppressLogging)
                ParsekLog.Verbose("Rewind",
                    $"SpawnSuppressedByRewind reset: \"{rec.VesselName}\" id={rec.RecordingId} " +
                    $"reason={rec.SpawnSuppressedByRewindReason ?? "<none>"}");
            rec.SpawnSuppressedByRewind = false;
            rec.SpawnSuppressedByRewindReason = null;
            rec.SpawnSuppressedByRewindUT = double.NaN;

            rec.SceneExitSituation = -1;
        }

        /// <summary>
        /// True when the rewind keeps this recording's spawn state: the keep set names its
        /// (non-empty) id.
        /// </summary>
        internal static bool ShouldKeepSpawnState(HashSet<string> keepSpawnStateRecordingIds, string recordingId)
        {
            return keepSpawnStateRecordingIds != null
                && !string.IsNullOrEmpty(recordingId)
                && keepSpawnStateRecordingIds.Contains(recordingId);
        }

        /// <summary>
        /// Zeroes the spawn link and every spawn-attempt counter, and drops the terminal
        /// spawn-safety deferral, so a replay can spawn the recording's vessel again.
        /// </summary>
        internal static void ClearSpawnState(Recording rec)
        {
            rec.VesselSpawned = false;
            rec.VesselDestroyed = false;
            rec.SpawnAttempts = 0;
            rec.SpawnDeathCount = 0;
            rec.SpawnedVesselPersistentId = 0;
            rec.CollisionBlockCount = 0;
            rec.SpawnAbandoned = false;
            rec.WalkbackExhausted = false;
            rec.DuplicateBlockerRecovered = false;
            TerminalOrbitSpawnSafety.Clear(rec);
        }

        /// <summary>
        /// Collects SpawnedVesselPersistentId values and vessel names from all committed
        /// recordings (standalone + tree) that currently have a spawned vessel.
        /// Must be called BEFORE ResetAllPlaybackState
        /// zeroes the PIDs.
        /// </summary>
        internal static (HashSet<uint> pids, HashSet<string> names) CollectSpawnedVesselInfo()
        {
            var pids = new HashSet<uint>();
            var names = new HashSet<string>();

            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                uint pid = RecordingStore.CommittedRecordings[i].SpawnedVesselPersistentId;
                if (pid != 0)
                {
                    pids.Add(pid);
                    if (!string.IsNullOrEmpty(RecordingStore.CommittedRecordings[i].VesselName))
                        names.Add(RecordingStore.CommittedRecordings[i].VesselName);
                }
            }

            for (int i = 0; i < RecordingStore.CommittedTrees.Count; i++)
            {
                foreach (var rec in RecordingStore.CommittedTrees[i].Recordings.Values)
                {
                    if (rec.SpawnedVesselPersistentId != 0)
                    {
                        pids.Add(rec.SpawnedVesselPersistentId);
                        if (!string.IsNullOrEmpty(rec.VesselName))
                            names.Add(rec.VesselName);
                    }
                }
            }

            if (!RecordingStore.SuppressLogging && pids.Count > 0)
                ParsekLog.Info("RecordingStore",
                    $"CollectSpawnedVesselInfo: {pids.Count} PID(s), {names.Count} name(s)");

            return (pids, names);
        }

        // One-shot cleanup data: populated in ParsekScenario.OnLoad revert/rewind path
        // (before spawn state reset), consumed and cleared in ParsekFlight.OnFlightReady.
        internal static HashSet<uint> PendingCleanupPids { get; set; }

        internal static HashSet<string> PendingCleanupNames { get; set; }

        // BUG-H: pids of vessels present in the revert-target launch/prelaunch quicksave
        // (FlightDriver.PreLaunchState / PostInitState). A vessel whose pid is in this set
        // pre-existed the reverted launch and must NEVER be stripped/recovered — it belongs
        // to an unrelated mission, not the reverted flight. Populated in the OnLoad revert
        // path, consumed and cleared in ParsekFlight.OnFlightReady (belt-and-suspenders
        // cleanup). Null when there is no active revert scope.
        internal static HashSet<uint> PendingRevertPreExistingPids { get; set; }

        /// <summary>
        /// Bug #134: after the rewind strip has already removed orphaned spawned
        /// protoVessels from the loaded flight state, the pending one-shot cleanup
        /// data must be dropped so <c>ParsekFlight.OnFlightReady</c> does not re-run
        /// with the overbroad all-recording name set and destroy freshly-spawned past
        /// vessels. The revert path's alreadyHasCleanupData guard then sees null and
        /// collects fresh data if it needs any.
        /// Called from <c>ParsekScenario.HandleRewindOnLoad</c> right after the strip.
        /// </summary>
        internal static void ClearPendingCleanupAfterRewindStrip()
        {
            PendingCleanupPids = null;
            PendingCleanupNames = null;
            PendingRevertPreExistingPids = null;
            ParsekLog.Info("Rewind",
                "OnLoad: cleared PendingCleanupPids/Names after strip - " +
                "prevents OnFlightReady from destroying freshly-spawned past vessels");
        }

        /// <summary>
        /// The armed-cleanup trigger <c>ParsekFlight.OnFlightReady</c> tests before
        /// running its belt-and-suspenders orphaned-vessel recovery. Kept next to
        /// <see cref="ClearPendingCleanupAfterRewindStrip"/> so the clear and the gate
        /// it silences read the same two fields.
        /// </summary>
        internal static bool ShouldRunPendingCleanupOnFlightReady()
        {
            return PendingCleanupPids != null || PendingCleanupNames != null;
        }

        /// <summary>
        /// Checks whether the player can rewind to a given recording's launch point.
        /// </summary>
        internal static bool CanRewind(Recording rec, out string reason, bool isRecording)
        {
            string resolvedSave = RecordingStore.GetRewindSaveFileName(rec);
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            string savePath = RecordingPaths.ResolveSaveScopedPath(
                RecordingPaths.BuildRewindSaveRelativePath(resolvedSave));
            bool saveExists = !string.IsNullOrEmpty(savePath) && File.Exists(savePath);
            return CanRewindWithResolvedSaveState(resolvedSave, saveExists, out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanRewind"/> once tree-root save resolution and file
        /// existence have already been computed by the caller.
        /// </summary>
        internal static bool CanRewindWithResolvedSaveState(
            string resolvedSave, bool saveExists, out string reason, bool isRecording)
        {
            if (!CanRewindPreFileCheck(resolvedSave, out reason, isRecording))
                return false;

            if (!saveExists)
            {
                reason = "Rewind save file missing";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        internal static bool CanRewindPreFileCheck(string resolvedSave, out string reason, bool isRecording)
        {
            if (RewindContext.IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (string.IsNullOrEmpty(resolvedSave))
            {
                reason = "No rewind save available";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before rewinding";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            if (PendingTreeSlot.HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 3.7% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        /// <summary>
        /// Validates whether the player can fast-forward to a future recording's start.
        /// Subset of CanRewind — no save file required (FF advances UT, doesn't load a save).
        /// </summary>
        internal static bool CanFastForward(Recording rec, out string reason, bool isRecording)
        {
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

            return CanFastForwardAtUT(rec, Planetarium.GetUniversalTime(), out reason, isRecording);
        }

        /// <summary>
        /// Testable core of <see cref="CanFastForward"/> with the current UT supplied by the caller.
        /// </summary>
        internal static bool CanFastForwardAtUT(Recording rec, double now, out string reason, bool isRecording)
        {
            if (!CanFastForwardPreRuntime(rec, out reason, isRecording))
                return false;

            if (now >= rec.StartUT)
            {
                reason = "Recording is not in the future";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        internal static bool CanFastForwardPreRuntime(Recording rec, out string reason, bool isRecording)
        {
            if (RewindContext.IsRewinding)
            {
                reason = "Rewind already in progress";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (rec == null || rec.Points.Count == 0)
            {
                reason = "Recording not available";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            var scenario = ParsekScenario.Instance;
            // Cascade overload: a parent-anchored debris child whose parent
            // was retired must also refuse fast-forward; without this, the
            // child stays interactive in the recordings table even though
            // its trajectory belongs to a retired re-fly fork.
            if (EffectiveState.IsRewindRetired(
                    rec,
                    RecordingStore.CommittedRecordings,
                    object.ReferenceEquals(null, scenario) ? null : scenario.RecordingRewindRetirements))
            {
                reason = "Recording was rewound out of timeline";
                return false;
            }

            if (isRecording)
            {
                reason = "Stop recording before fast-forwarding";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            if (PendingTreeSlot.HasPendingTree)
            {
                reason = "Merge or discard pending tree first";
                // Per-frame logging removed (was 20% of all log output); reason returned to caller
                return false;
            }

            reason = "";
            // Per-frame logging removed; reason returned to caller
            return true;
        }

        /// <summary>
        /// Clears the rewind context + the replay-target scope. The production callers are
        /// the load-failure paths inside <see cref="ExecuteRewindSaveLoad"/>, which is the
        /// ONLY place a rewind can unwind itself: the flags otherwise live until
        /// <c>HandleRewindOnLoad</c>'s <c>EndRewind()</c> in the reloaded scene.
        ///
        /// <para>Internal (not private) for ONE seam-abort consumer: the test-command
        /// applier <c>ParsekTestCommandAddon.TryCompleteInvokeRewindToLaunch</c>'s
        /// rewind-timeout branch. A reload that never settles reaches neither of the two
        /// clearing paths, so <c>RewindContext.IsRewinding</c> would stay armed for the
        /// rest of the process - the next <c>ParsekScenario.OnLoad</c> would take the
        /// rewind branch, and every later <see cref="CanRewind"/> would refuse. The verb
        /// that aborts the seam resets what the abort stranded.</para>
        /// </summary>
        internal static void ResetRewindFlags()
        {
            RewindContext.EndRewind();
            SceneEntryVesselScope.ClearRewindReplayTargetScope();
            RewindCarry.ClearRewindCarriedRewindPoints("rewind-flags-reset");
            RewindCarry.ClearRewindCarriedStagedLists("rewind-flags-reset");
        }

        /// <summary>
        /// Initiates a rewind to the given recording's launch point.
        /// Sets rewind flags, copies the save file to the root saves dir (KSP's LoadGame
        /// doesn't support subdirectory paths), loads it, then deletes the temp copy.
        /// </summary>
        internal static void InitiateRewind(Recording rec)
        {
            // Resolve the rewind save owner — may be the tree root for branch recordings.
            // All save-related fields (filename, resources, UT) come from the owner,
            // since the quicksave was captured at the owner's launch.
            var owner = RecordingStore.GetRewindRecording(rec);
            if (owner == null)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"InitiateRewind: no rewind owner for '{rec.VesselName}' (id={rec.RecordingId})");
                return;
            }

            bool isTreeBranch = owner != rec;
            if (isTreeBranch && !RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind via tree root: branch '{rec.VesselName}' -> root '{owner.VesselName}' " +
                    $"save={owner.RewindSaveFileName}");

            // Refuse rewind during an active re-fly merge journal. The rewind carries the
            // RecordingSupersedes list the merge journal is in the middle of writing
            // across the load; running both concurrently could leave the ledger
            // half-committed across the LoadGame boundary. Half-fix is worse than
            // no-fix — let MergeJournalOrchestrator.RunFinisher resolve the journal
            // on the next OnLoad first, then the user can rewind.
            var scenarioForJournalCheck = ParsekScenario.Instance;
            if (IsMergeJournalUnfinished(scenarioForJournalCheck?.ActiveMergeJournal))
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot rewind during an active re-fly merge — finish the merge first " +
                        $"(journalPhase={scenarioForJournalCheck.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot rewind during an active re-fly merge", 3f);
                return;
            }

            // A plain rewind taken while a Re-Fly session is live ends that session here, before
            // the rewind is armed and before the load (operator ruling 2026-09-24; the reasons
            // it cannot wait for the rewind's OnLoad are on the callee).
            MergeDialog.TryDiscardLiveReFlySessionForRewind(
                ParsekFlight.Instance?.ActiveTreeForSerialization, "Rewind",
                out string droppedLiveTreeId, out bool droppedTreeRestored);

            BeginRewindForOwner(owner);

            // Shared load: pre-process (strip + lead-time windback) keyed by the owner, and
            // log the owner tree's supersede rows the rewind keeps.
            bool loaded = ExecuteRewindSaveLoad(
                owner.RewindSaveFileName,
                preProcessOwner: owner,
                supersedeAuditOwner: owner,
                messageLabel: "Rewind");
            if (!loaded)
                UndoLiveTreeDropAfterFailedRewind(droppedLiveTreeId, droppedTreeRestored, "Rewind");
        }

        /// <summary>
        /// True while a re-fly merge journal exists and has not reached its Complete phase:
        /// both rewind entry points refuse then, so the journal finishes on the next OnLoad
        /// instead of straddling the rewind's LoadGame.
        /// </summary>
        internal static bool IsMergeJournalUnfinished(MergeJournal journal)
        {
            return journal != null && journal.Phase != MergeJournal.Phases.Complete;
        }

        /// <summary>
        /// A rewind whose load failed stays in the flight scene. When ending the live Re-Fly
        /// session armed the scene exit to drop the live tree, disarm it, and when the tree was
        /// also put back in the committed store take it out again, so the flight keeps one live
        /// tree (minus the discarded attempt) instead of a tree that is live and committed at
        /// once. The session itself stays ended.
        /// </summary>
        internal static void UndoLiveTreeDropAfterFailedRewind(
            string treeId, bool restoredToCommitted, string label)
        {
            if (string.IsNullOrEmpty(treeId))
                return;
            PendingTreeSlot.TryConsumeNextTreeSceneExitCommitSuppression(GameScenes.FLIGHT, out _);
            bool removed = restoredToCommitted
                && RecordingStore.RemoveCommittedTreeById(treeId, $"{label} load failed");
            ParsekLog.Warn("Rewind",
                $"{label} load failed after ending the live Re-Fly session: tree={treeId} " +
                $"returned to the flight scene (removedFromCommitted={removed}); the session stays ended");
        }

        // internal (not private) so RewindLoggingTests can drive the real
        // baseline-argument selection instead of re-issuing BeginRewind inline.
        internal static void BeginRewindForOwner(Recording owner)
        {
            var reserved = new BudgetSummary
            {
                reservedFunds = owner.RewindReservedFunds,
                reservedScience = owner.RewindReservedScience,
                reservedReputation = owner.RewindReservedRep
            };

            // Baseline resources from the owner's pre-launch snapshot.
            // The rewind save was captured at the same moment as PreLaunch values.
            RewindContext.BeginRewind(owner.StartUT, reserved,
                owner.PreLaunchFunds, owner.PreLaunchScience, owner.PreLaunchReputation);
            SceneEntryVesselScope.SetRewindReplayTargetScope(owner);

            if (!RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Rewind-to-Launch initiated to UT {owner.StartUT} " +
                    $"(save: {owner.RewindSaveFileName}). Plain launch rewind via parsek_rw_* quicksave; " +
                    $"this is NOT a Re-Fly (no RewindPoint / ReFlySessionMarker / MergeJournal)");
        }

        /// <summary>
        /// Shared load mechanics for both rewind entry points (<see cref="InitiateRewind"/>
        /// and <see cref="InitiateRewindToCareerStart"/>). Copies the Parsek/Saves snapshot to
        /// the root saves dir (KSP's LoadGame cannot read subdirectory paths), optionally
        /// pre-processes it, loads it via <see cref="GamePersistence.LoadGame"/>, deletes the
        /// temp copy, captures the adjusted UT, optionally drops supersede relations rewound
        /// out of existence, swaps in the loaded game, and loads the Space Center. On any
        /// failure resets the rewind flags and removes the temp copy. Returns true when the
        /// scene load was initiated.
        ///
        /// <para>Callers own the precondition gates (merge-journal refusal, supersede-count
        /// refusal) and the rewind-context setup (<see cref="BeginRewindForOwner"/> vs the
        /// UT-0 reset) BEFORE calling this; this helper is purely the mechanical save-copy /
        /// load / scene-swap wrapper that the two entry points used to duplicate.</para>
        /// </summary>
        /// <param name="saveFileName">Parsek/Saves snapshot base name (no .sfs extension).</param>
        /// <param name="preProcessOwner">When non-null, runs <see cref="PreProcessRewindSave"/>
        /// with that owner's strip set plus the rewind-to-launch lead-time windback. Null skips
        /// pre-processing (the pristine career-start snapshot, which must not be wound back
        /// below UT 0).</param>
        /// <param name="supersedeAuditOwner">When non-null, logs the supersede rows of that
        /// owner's tree the rewind reaches once the adjusted UT is known; every row is kept.
        /// Null skips the audit (the career-start path refuses up front when any supersede
        /// relation exists).</param>
        /// <param name="messageLabel">Message-text prefix for the adjusted-UT / failure logs
        /// ("Rewind" vs "Warp-to-game-start"); the subsystem tag stays "Rewind" either way.</param>
        private static bool ExecuteRewindSaveLoad(
            string saveFileName,
            Recording preProcessOwner,
            Recording supersedeAuditOwner,
            string messageLabel)
        {
            string tempCopyName = null;
            double rewindSaveOwnUT = double.NaN;
            // The OnLoad this load leads to does NOT read the rewind save's scenario node:
            // SpaceCenterMain.Start reloads persistent.sfs from disk, so the RP list it
            // restores is whatever the last persistent write held. Carry the in-memory
            // list across instead (see ReinstallRewindCarriedRewindPointsAfterLoad).
            RewindCarry.CaptureRewindPointsForRewind(ParsekScenario.Instance, messageLabel);
            try
            {
                string savesDir = BuildRewindSavesDir(KSPUtil.ApplicationRootPath, HighLogic.SaveFolder);

                string sourcePath = Path.Combine(savesDir,
                    RecordingPaths.BuildRewindSaveRelativePath(saveFileName));
                tempCopyName = saveFileName;
                string tempPath = BuildRewindTempSavePath(savesDir, tempCopyName);

                // Copy from Parsek/Saves/ to the root saves dir so LoadGame can find it.
                File.Copy(sourcePath, tempPath, true);

                if (preProcessOwner != null)
                {
                    // Pre-process the save file before KSP parses it:
                    // 1. Remove recorded vessel + any EVA child vessels (other vessels stay intact)
                    // 2. Wind back UT by the rewind-to-launch lead time so the player can
                    //    regain control on the pad before launch.
                    // The owner strip removes the owner's own vessel by name AND launch guid,
                    // so an earlier launch of the same craft still standing in the quicksave
                    // (same name, different guid) is kept as committed history.
                    var ownerStrip = RewindSpawnScope.BuildRewindOwnerStrip(preProcessOwner);
                    // Collect spawned vessel PIDs for PID-based stripping (belt-and-suspenders
                    // alongside name matching, catches renamed vessels or debris)
                    // The PID set is scoped to vessels a replaying recording will re-produce:
                    // committed history the rewind keeps (an earlier tree's spawned vessel)
                    // stays in the save and therefore in the quicksave whitelist.
                    RewindContext.SetHistoricalSpawnKeepRecordingIds(null);
                    rewindSaveOwnUT = PreProcessRewindSave(
                        tempPath, ownerStrip, RewindSpawnScope.ResolveRewindStripSpawnedPids,
                        RecordingStore.RewindToLaunchLeadTimeSeconds, RewindSpawnScope.ResolveRewindClaimedVesselPids);
                }

                // Parsek's time travel keeps the current difficulty settings (owner ruling Q1):
                // captured before the load, installed before the game is written or started.
                ConfigNode liveParameters = DifficultySettingsCarry.CaptureLive(messageLabel);
                Game game = GamePersistence.LoadGame(tempCopyName, HighLogic.SaveFolder, true, false);

                // Delete the temp copy (file already parsed into Game object)
                TryDeleteFileQuietly(tempPath);

                if (game == null)
                {
                    ResetRewindFlags();
                    if (!RecordingStore.SuppressLogging)
                        ParsekLog.Error("Rewind",
                            $"{messageLabel} failed: LoadGame returned null for save '{saveFileName}'. " +
                            $"Flags reset: IsRewinding={RewindContext.IsRewinding}, RewindUT={RewindContext.RewindUT}, RewindAdjustedUT={RewindContext.RewindAdjustedUT}");
                    return false;
                }

                // Capture the adjusted UT from the (possibly preprocessed) save.
                RewindContext.SetAdjustedUT(game.flightState.universalTime);

                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: adjustedUT={RewindContext.RewindAdjustedUT:F1}, " +
                        $"rewindUT={RewindContext.RewindUT:F1}, flags=[IsRewinding={RewindContext.IsRewinding}]");

                // A rewind moves the clock and keeps every supersede row of the rewound tree
                // (owner ruling 2026-10-24): log what it keeps. Runs AFTER SetAdjustedUT so the
                // scope reads the post-load UT.
                if (supersedeAuditOwner != null)
                    RewindCarry.LogSupersedesKeptAcrossRewind(supersedeAuditOwner, RewindContext.RewindAdjustedUT);

                // Same stale-persistent reason as the RP capture above, for the supersede,
                // rewind-retirement and tombstone lists and the merge journal.
                RewindCarry.CaptureRewindStagedListsForRewind(ParsekScenario.Instance, messageLabel);

                // Same reason, for the rewind save's own route copy: OnLoad will read persistent's
                // ROUTES, of unknown age, so the go-back loop-position restore in
                // HandleRewindOnLoad reads this save's from the parsed game's scenario protos,
                // which still hold the file's nodes. That copy is as of the save's own UT, before
                // the lead-time windback the parsed clock already carries, and the go-back route
                // reconcile keys its cutoff to it.
                Logistics.RouteLoadReconcile.CaptureRewindSaveRoutes(game.scenarios,
                    Logistics.RouteLoadReconcile.ResolveRewindSaveOwnUT(
                        rewindSaveOwnUT, game.flightState.universalTime),
                    messageLabel);

                // The ledger held-through UT the rewind save carries (a save written inside an
                // earlier Rewind-to-Launch lead window): its world is as of that UT, so the new
                // floor HandleRewindOnLoad sets rises to it (LedgerHeldWindow.ResolveAtRewind).
                RewindContext.SetRewindSaveLedgerHeldThroughUT(
                    LedgerHeldWindow.ReadSavedFromScenarios(game.scenarios));

                // The replay-scope latch the rewind save carries: a recording that replayed when
                // that save was written replays again from its UT, even after a load since
                // reset memory's latch (archived todo REPLAY-SCOPE-LOST-ON-SAVE-LOAD). Memory's is kept.
                // PreProcessRewindSave already merged it before its strip; this covers the
                // un-preprocessed path and is a no-op otherwise.
                PlaybackScopeTracker.UnionFromScenarios(game.scenarios, messageLabel);

                DifficultySettingsCarry.ApplyTo(game, liveParameters, messageLabel);

                // The Space Center start reloads persistent.sfs, not CurrentGame: put the rewind
                // game there so the rewind lands in the rewind save's own world.
                WriteRewindGameAsPersistent(game, messageLabel);

                HighLogic.CurrentGame = game;
                HighLogic.LoadScene(GameScenes.SPACECENTER);

                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"{messageLabel}: loading save '{saveFileName}' into SpaceCenter");
                return true;
            }
            catch (Exception ex)
            {
                ResetRewindFlags();

                DeleteTemporaryRewindSaveCopy(tempCopyName);

                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"{messageLabel} failed: {ex.Message}. " +
                        $"Flags reset: IsRewinding={RewindContext.IsRewinding}, RewindUT={RewindContext.RewindUT}, RewindAdjustedUT={RewindContext.RewindAdjustedUT}");
                return false;
            }
        }

        /// <summary>
        /// Resets the game to its career-start snapshot (UT 0): reloads the pristine
        /// snapshot captured at career creation, restoring initial resources/facilities/clock
        /// while KEEPING the in-memory recordings as future ghosts (the OnLoad rewind branch
        /// skips the .sfs recording reload). Used by "Warp to time" for targets at/before the
        /// first launch so Year 1 / Day 1 is a true reset rather than landing at the earliest
        /// launch. Mirrors <see cref="InitiateRewind"/> minus the strip / lead-time windback
        /// (the snapshot is pristine) and minus owner-specific budget / replay scope.
        ///
        /// <para>Refuses when re-fly supersede relations exist: a UT-0 reset would otherwise
        /// leave superseded originals hidden. The caller selects the earliest-launch rewind
        /// path instead in that case (the owner-keyed rewind is the tested route).</para>
        /// </summary>
        internal static bool InitiateRewindToCareerStart(string saveFileName)
        {
            if (string.IsNullOrEmpty(saveFileName))
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("Rewind", "InitiateRewindToCareerStart: null/empty save name");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (IsMergeJournalUnfinished(scenario?.ActiveMergeJournal))
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Error("Rewind",
                        $"Cannot warp to game start during an active re-fly merge " +
                        $"(journalPhase={scenario.ActiveMergeJournal.Phase})");
                ParsekLog.ScreenMessage("Cannot warp to game start during an active re-fly merge", 3f);
                return false;
            }

            int supersedeCount = scenario?.RecordingSupersedes?.Count ?? 0;
            if (supersedeCount > 0)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"InitiateRewindToCareerStart refused: {supersedeCount} supersede relation(s) present " +
                        "(UT-0 reset would hide superseded originals); caller should use earliest-launch rewind");
                return false;
            }

            // Same live Re-Fly session end as InitiateRewind.
            MergeDialog.TryDiscardLiveReFlySessionForRewind(
                ParsekFlight.Instance?.ActiveTreeForSerialization, "Warp-to-game-start",
                out string droppedLiveTreeId, out bool droppedTreeRestored);

            // UT-0 reset: no owner, no reserved budget, no baseline. ApplyRewindResourceAdjustment
            // recalcs the ledger at the adjusted UT (~0), which restores pristine career resources.
            RewindContext.BeginRewind(0.0, default(BudgetSummary), 0, 0, 0f);
            RewindContext.SetQuicksaveVesselPids(null);   // no PreProcessRewindSave -> no whitelist
            SceneEntryVesselScope.ClearRewindReplayTargetScope();               // no specific owner to replay-scope
            if (!RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Warp-to-game-start initiated to UT 0 (snapshot: {saveFileName}). Keeps in-memory " +
                    "recordings as future ghosts; NOT a Re-Fly.");

            // Pristine snapshot: no PreProcessRewindSave (no future vessels to strip, and it
            // must NOT be wound back below UT 0) and no supersede audit (refused above when any
            // supersede relation exists).
            bool loaded = ExecuteRewindSaveLoad(
                saveFileName,
                preProcessOwner: null,
                supersedeAuditOwner: null,
                messageLabel: "Warp-to-game-start");
            if (!loaded)
                UndoLiveTreeDropAfterFailedRewind(droppedLiveTreeId, droppedTreeRestored, "Warp-to-game-start");
            return loaded;
        }

        private static void DeleteTemporaryRewindSaveCopy(string tempCopyName)
        {
            // Clean up temp copy on failure
            if (tempCopyName == null)
                return;

            try
            {
                string savesDir = BuildRewindSavesDir(KSPUtil.ApplicationRootPath, HighLogic.SaveFolder);
                DeleteFileIfPresent(BuildRewindTempSavePath(savesDir, tempCopyName));
            }
            catch { }
        }

        /// <summary>
        /// The save folder's directory under the install's <c>saves</c> root; a null root or
        /// folder reads as empty. LoadGame reads only this directory, never a subdirectory.
        /// </summary>
        internal static string BuildRewindSavesDir(string applicationRootPath, string saveFolder)
        {
            return Path.Combine(applicationRootPath ?? "", "saves", saveFolder ?? "");
        }

        /// <summary>The temporary root-level copy of a rewind save that LoadGame reads.</summary>
        internal static string BuildRewindTempSavePath(string savesDir, string tempCopyName)
        {
            return Path.Combine(savesDir, tempCopyName + ".sfs");
        }

        /// <summary>Deletes the file when it exists; a missing file is not an error.</summary>
        internal static void DeleteFileIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        internal static void TryDeleteFileQuietly(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        /// <summary>
        /// Modifies the temp rewind save file before KSP parses it:
        /// removes the recorded vessel and winds back UT so the player
        /// has time to reach the launch pad before the ghost appears.
        /// </summary>
        internal static void PreProcessRewindSave(string sfsPath, string vesselName, double leadTime)
        {
            PreProcessRewindSave(sfsPath, new HashSet<string> { vesselName }, leadTime);
        }

        internal static void PreProcessRewindSave(string sfsPath, HashSet<string> vesselNames, double leadTime)
        {
            PreProcessRewindSave(sfsPath, vesselNames, (HashSet<uint>)null, leadTime);
        }

        /// <summary>
        /// Extended overload that strips vessels by both name AND persistent ID.
        /// PID matching catches renamed vessels or debris that name matching misses.
        /// The .sfs ConfigNode reliably has "persistentId" as a text field.
        /// </summary>
        internal static void PreProcessRewindSave(
            string sfsPath, HashSet<string> vesselNames, HashSet<uint> vesselPids, double leadTime)
        {
            PreProcessRewindSave(sfsPath, vesselNames, _ => vesselPids, leadTime);
        }

        /// <summary>
        /// Resolver overload: the PID strip set is resolved from the ADJUSTED (wound-back) UT,
        /// which is only known once the save's own UT is parsed. The plain rewind passes
        /// <see cref="RewindSpawnScope.ResolveRewindStripSpawnedPids"/> so a spawned vessel of committed history
        /// that no replaying recording will re-produce stays in the save (and so in the
        /// quicksave whitelist). The resolver receives NaN when the save's UT is missing or
        /// unparseable.
        /// </summary>
        internal static void PreProcessRewindSave(
            string sfsPath, HashSet<string> vesselNames,
            Func<double, HashSet<uint>> resolveVesselPids, double leadTime)
        {
            PreProcessRewindSave(
                sfsPath, RewindSpawnScope.RewindOwnerStrip.FromNames(vesselNames), resolveVesselPids, leadTime);
        }

        /// <summary>
        /// Owner-strip overload: the name half of the strip is identity-aware
        /// (<see cref="RewindSpawnScope.ClassifyRewindOwnerStripVessel"/>), so a vessel carrying the owner's name
        /// but a conclusively different launch guid (an earlier launch of the same craft) stays
        /// in the save and in the quicksave whitelist.
        /// Returns the save's own UT as written, before the lead-time windback (the moment every
        /// scenario node in the file describes), or NaN when the save has no parseable UT.
        /// </summary>
        internal static double PreProcessRewindSave(
            string sfsPath, RewindSpawnScope.RewindOwnerStrip ownerStrip,
            Func<double, HashSet<uint>> resolveVesselPids, double leadTime,
            Func<double, HashSet<uint>> resolveClaimedVesselPids = null)
        {
            if (ownerStrip == null)
                ownerStrip = new RewindSpawnScope.RewindOwnerStrip();
            ConfigNode root = ConfigNode.Load(sfsPath);
            if (root == null)
                return double.NaN;

            ConfigNode gameNode = root.HasNode("GAME") ? root.GetNode("GAME") : root;

            // The rewind save's replay-scope latch joins memory's BEFORE the strip resolvers run:
            // they decide which recordings replay after the rewind (WillReplayAfterRewind), and a
            // recording replaying when this save was written replays again from its UT (todo
            // REPLAY-SCOPE-LOST-ON-SAVE-LOAD).
            PlaybackScopeTracker.UnionFromGameNode(gameNode, "rewind save pre-process");

            ConfigNode flightState = gameNode.GetNode("FLIGHTSTATE");
            if (flightState == null)
            {
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Warn("Rewind",
                        $"PreProcessRewindSave: no FLIGHTSTATE node in save '{sfsPath}'");
                root.Save(sfsPath);
                return double.NaN;
            }

            double saveOwnUT;
            double adjustedUT = WindBackFlightStateUT(flightState, leadTime, out saveOwnUT);

            // Remove vessels matching name OR persistentId
            HashSet<uint> vesselPids = resolveVesselPids != null
                ? resolveVesselPids(adjustedUT)
                : null;
            StripRewindSaveVessels(flightState, ownerStrip, vesselPids);

            // Capture PIDs of surviving vessels in the quicksave.
            // Used by StripFuturePrelaunchVessels to whitelist known-good PRELAUNCH
            // vessels and strip only unknown ones that appeared after the rewind point.
            var survivingPids = CollectSurvivingVesselPids(flightState);
            RewindContext.SetQuicksaveVesselPids(survivingPids.Count > 0 ? survivingPids : null);
            if (!RecordingStore.SuppressLogging)
                ParsekLog.Info("Rewind",
                    $"Captured {survivingPids.Count} surviving vessel PID(s) from quicksave");

            // The landing scene reloads persistent.sfs, so a vessel a committed future claims
            // lands in its latest (post-claim) form; keep the rewind save's own copy of each
            // claimed vessel for the ghost that stands in for it until the claim.
            if (resolveClaimedVesselPids != null)
            {
                HashSet<uint> claimedPids = resolveClaimedVesselPids(adjustedUT);
                int heldCopies = ClaimedVesselHoldGhost.CaptureRewindSaveVessels(flightState, claimedPids);
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"Captured {heldCopies} rewind-save copy(ies) of {claimedPids?.Count ?? 0} " +
                        "vessel(s) a committed future claims");
            }

            root.Save(sfsPath);
            return saveOwnUT;
        }

        /// <summary>
        /// Winds the FLIGHTSTATE clock back by <paramref name="leadTime"/>, never below 0, and
        /// writes it round-trip invariant. Returns the wound-back UT and gives the save's own
        /// UT in <paramref name="saveOwnUT"/>; both are NaN (and the node is untouched) when
        /// the UT value is missing or unparseable.
        /// </summary>
        internal static double WindBackFlightStateUT(ConfigNode flightState, double leadTime, out double saveOwnUT)
        {
            string utStr = flightState.GetValue("UT");
            double ut;
            double adjustedUT = double.NaN;
            saveOwnUT = double.NaN;
            if (!string.IsNullOrEmpty(utStr) &&
                double.TryParse(utStr, NumberStyles.Any, CultureInfo.InvariantCulture, out ut))
            {
                saveOwnUT = ut;
                double newUT = Math.Max(0, ut - leadTime);
                adjustedUT = newUT;
                flightState.SetValue("UT", newUT.ToString("R", CultureInfo.InvariantCulture));
                if (!RecordingStore.SuppressLogging)
                    ParsekLog.Info("Rewind",
                        $"UT adjusted: {ut:F1} → {newUT:F1} (lead time {leadTime}s)");
            }
            else if (!RecordingStore.SuppressLogging)
            {
                ParsekLog.Warn("Rewind",
                    $"PreProcessRewindSave: missing or invalid UT value '{utStr ?? "(null)"}' in FLIGHTSTATE");
            }
            return adjustedUT;
        }

        /// <summary>
        /// Removes from FLIGHTSTATE every vessel the owner strip claims (by name or owner
        /// guid) and every vessel whose persistentId is in <paramref name="vesselPids"/>, keeps
        /// an earlier launch of the owner's craft, and logs one summary of the counts.
        /// </summary>
        internal static void StripRewindSaveVessels(
            ConfigNode flightState, RewindSpawnScope.RewindOwnerStrip ownerStrip, HashSet<uint> vesselPids)
        {
            bool hasPids = vesselPids != null && vesselPids.Count > 0;
            int removedByName = 0;
            int removedByGuid = 0;
            int removedByPid = 0;
            int keptOtherLaunch = 0;
            var vesselNodes = flightState.GetNodes("VESSEL");
            var vesselGuids = ReadVesselNodeGuids(vesselNodes);
            bool ownerAnchored = RewindSpawnScope.RewindOwnerStripIsAnchored(ownerStrip, vesselGuids);
            for (int i = vesselNodes.Length - 1; i >= 0; i--)
            {
                string name = Recording.ResolveLocalizedName(vesselNodes[i].GetValue("name"));
                RewindSpawnScope.RewindOwnerStripDecision ownerDecision = RewindSpawnScope.ClassifyRewindOwnerStripVessel(
                    name, vesselGuids[i], ownerStrip, ownerAnchored);
                if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripName
                    || ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripOwnerGuid)
                {
                    flightState.RemoveNode(vesselNodes[i]);
                    if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.StripName)
                        removedByName++;
                    else
                        removedByGuid++;
                    continue;
                }

                if (ownerDecision == RewindSpawnScope.RewindOwnerStripDecision.KeepOtherLaunch)
                {
                    keptOtherLaunch++;
                    if (!RecordingStore.SuppressLogging)
                        ParsekLog.Info("Rewind",
                            $"Rewind owner strip: keeping vessel '{name}' (pid={vesselNodes[i].GetValue("persistentId") ?? "(none)"}, " +
                            $"guid={vesselGuids[i]}) in rewind save: owner's name but a different launch " +
                            $"(owner guid={ownerStrip.OwnerGuid})");
                }

                if (hasPids && IsVesselNodePidIn(vesselNodes[i], vesselPids))
                {
                    flightState.RemoveNode(vesselNodes[i]);
                    removedByPid++;
                }
            }

            if (!RecordingStore.SuppressLogging)
            {
                string namesStr = string.Join(", ", ownerStrip.AllNames());
                ParsekLog.Info("Rewind",
                    $"Stripped {removedByName + removedByGuid + removedByPid} vessel(s) from save " +
                    $"({removedByName} by name [{namesStr}], {removedByGuid} by owner guid, " +
                    $"{removedByPid} by PID; kept {keptOtherLaunch} other launch(es) of the owner's craft)");
            }
        }

        /// <summary>Each VESSEL node's launch guid, by index (null where none is readable).</summary>
        internal static string[] ReadVesselNodeGuids(ConfigNode[] vesselNodes)
        {
            var vesselGuids = new string[vesselNodes.Length];
            for (int g = 0; g < vesselNodes.Length; g++)
                vesselGuids[g] = VesselLaunchIdentity.TryReadVesselGuid(vesselNodes[g]);
            return vesselGuids;
        }

        /// <summary>
        /// True when the VESSEL node's persistentId parses (invariant integer) to a member of
        /// <paramref name="pids"/>.
        /// </summary>
        internal static bool IsVesselNodePidIn(ConfigNode vesselNode, HashSet<uint> pids)
        {
            string pidStr = vesselNode.GetValue("persistentId");
            uint pid;
            return pidStr != null
                && uint.TryParse(pidStr, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out pid)
                && pids.Contains(pid);
        }

        /// <summary>
        /// The persistentIds of the VESSEL nodes left in FLIGHTSTATE, skipping any that do not
        /// parse: the quicksave whitelist for the post-load prelaunch strip.
        /// </summary>
        internal static HashSet<uint> CollectSurvivingVesselPids(ConfigNode flightState)
        {
            var survivingPids = new HashSet<uint>();
            var survivingNodes = flightState.GetNodes("VESSEL");
            for (int s = 0; s < survivingNodes.Length; s++)
            {
                uint spid;
                if (uint.TryParse(survivingNodes[s].GetValue("persistentId"), out spid))
                    survivingPids.Add(spid);
            }
            return survivingPids;
        }
    }
}
