using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Pending and committed tree restore (with the two restore coroutines), the vessel-switch
    // stash, the fresh-rollout refusal and restore suppression.
    public partial class ParsekFlight
    {
        /// <summary>
        /// Copies rewind metadata from a live recorder onto the tree root using its
        /// CaptureAtStop when present, otherwise the recorder's in-memory fallback fields.
        /// Centralized so every recorder-backed commit path keeps the same rewind budget
        /// and pre-launch baseline semantics.
        /// </summary>
        internal static void CopyRewindSaveToRoot(RecordingTree tree, FlightRecorder sourceRecorder,
            string logTag = null)
        {
            if (sourceRecorder == null) return;

            CopyRewindSaveToRoot(
                tree,
                sourceRecorder.CaptureAtStop,
                recorderFallbackSave: sourceRecorder.RewindSaveFileName,
                recorderFallbackReservedFunds: sourceRecorder.RewindReservedFunds,
                recorderFallbackReservedScience: sourceRecorder.RewindReservedScience,
                recorderFallbackReservedRep: sourceRecorder.RewindReservedRep,
                recorderFallbackPreLaunchFunds: sourceRecorder.PreLaunchFunds,
                recorderFallbackPreLaunchScience: sourceRecorder.PreLaunchScience,
                recorderFallbackPreLaunchRep: sourceRecorder.PreLaunchReputation,
                logTag: logTag);
        }

        /// <summary>
        /// Copies the rewind save filename and reserved budget from a CaptureAtStop (or
        /// recorder fallback) to the tree's root recording. Called from every flush/commit
        /// site so the R button and rewind budget resolve correctly regardless of which
        /// child recorder is active at commit time.
        /// T59: without this, EVA branch recordings lose the rewind save because the EVA
        /// recorder never captures one — the parent's CaptureAtStop is the only source.
        /// </summary>
        internal static void CopyRewindSaveToRoot(RecordingTree tree, Recording captureAtStop,
            string recorderFallbackSave = null,
            double recorderFallbackReservedFunds = 0,
            double recorderFallbackReservedScience = 0,
            float recorderFallbackReservedRep = 0,
            double recorderFallbackPreLaunchFunds = 0,
            double recorderFallbackPreLaunchScience = 0,
            float recorderFallbackPreLaunchRep = 0,
            string logTag = null)
        {
            if (tree == null || string.IsNullOrEmpty(tree.RootRecordingId)) return;

            string rewindSave = captureAtStop?.RewindSaveFileName ?? recorderFallbackSave;
            if (string.IsNullOrEmpty(rewindSave)) return;

            Recording rootRec;
            if (!tree.Recordings.TryGetValue(tree.RootRecordingId, out rootRec)) return;

            // Only set if root doesn't already have a rewind save (first recorder wins)
            if (string.IsNullOrEmpty(rootRec.RewindSaveFileName))
            {
                rootRec.RewindSaveFileName = rewindSave;
                ParsekLog.Info("Flight",
                    $"{logTag ?? "CopyRewindSaveToRoot"}: copied rewind save '{rewindSave}' " +
                    $"to root recording '{tree.RootRecordingId}'");
            }

            // Copy reserved budget if not already set (same first-wins rule).
            // InitiateRewind reads these from the root recording via GetRewindRecording.
            if (rootRec.RewindReservedFunds == 0
                && rootRec.RewindReservedScience == 0 && rootRec.RewindReservedRep == 0)
            {
                rootRec.RewindReservedFunds = captureAtStop?.RewindReservedFunds ?? recorderFallbackReservedFunds;
                rootRec.RewindReservedScience = captureAtStop?.RewindReservedScience ?? recorderFallbackReservedScience;
                rootRec.RewindReservedRep = captureAtStop?.RewindReservedRep ?? recorderFallbackReservedRep;
            }

            // Copy pre-launch budget if not already set.
            if (rootRec.PreLaunchFunds == 0
                && rootRec.PreLaunchScience == 0 && rootRec.PreLaunchReputation == 0)
            {
                rootRec.PreLaunchFunds = captureAtStop?.PreLaunchFunds ?? recorderFallbackPreLaunchFunds;
                rootRec.PreLaunchScience = captureAtStop?.PreLaunchScience ?? recorderFallbackPreLaunchScience;
                rootRec.PreLaunchReputation = captureAtStop?.PreLaunchReputation ?? recorderFallbackPreLaunchRep;
            }
        }

        /// <summary>
        /// Takes back the committed tree of the active vessel when a committed recording
        /// matches it (copy-on-write clone) and binds a recorder. When the target recording's
        /// snapshot does not hold the live vessel's part persistentIds (a Parsek spawn
        /// regenerates them), the flight continues in a new switch-continuation segment under
        /// the target instead of recording into it, because ghost playback resolves part events
        /// against the recording's own snapshot. <paramref name="callerBuildsOwnSegment"/> is
        /// true only for <see cref="TryRouteCommittedSpawnedClone"/>, which attaches its own
        /// segment right after this returns.
        /// </summary>
        private bool TryRestoreCommittedTreeForSpawnedActiveVessel(bool callerBuildsOwnSegment = false)
        {
            if (activeTree != null || recorder != null || restoringActiveTree)
                return false;
            if (PendingTreeSlot.HasPendingTree
                || ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady
                    != ParsekScenario.ActiveTreeRestoreMode.None)
            {
                return false;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint activeVesselPid = activeVessel != null ? activeVessel.persistentId : 0;
            if (activeVesselPid == 0 || GhostMapPresence.IsGhostMapVessel(activeVesselPid))
                return false;

            if (IsCommittedSpawnedRestoreSuppressedForVessel(
                    committedSpawnedRestoreSuppressedPid, activeVesselPid))
            {
                // Standing condition re-reached by the 1 Hz Update retry: rate-limited
                // per pid. Returning before TryTakeCommittedTreeForSpawnedVesselRestore
                // keeps the copy-on-write clone and its restore attempt unarmed.
                ParsekLog.VerboseRateLimited("Flight",
                    "committed-spawned-restore-suppressed-" + activeVesselPid,
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: skipping vessel " +
                    $"'{activeVessel.vesselName}' pid={activeVesselPid} - committed-tree " +
                    "restore suppressed (a test-command DiscardTree, or its committed recording " +
                    "runs past the live clock)");
                return false;
            }

            if (ShouldSkipCommittedTreeRestoreForFreshLaunch(activeVesselPid, freshRolloutVesselPid))
            {
                // Standing condition, not an event: the per-frame Update retry re-reaches
                // this branch for the life of the scene (111 identical Info lines in one
                // collected session). Per the batch-counting convention it belongs on the
                // rate-limited channel with a per-pid key - the pid identity matters, so
                // one line per fresh-rollout vessel rather than one shared key.
                ParsekLog.VerboseRateLimited("Flight",
                    "fresh-rollout-skip-" + activeVesselPid,
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: skipping fresh-rollout " +
                    $"vessel '{activeVessel.vesselName}' pid={activeVesselPid} " +
                    "(matches captured scene-entry pid) - new mission gets its own tree");
                return false;
            }

            string activeVesselGuid = activeVessel.id != Guid.Empty
                ? activeVessel.id.ToString("N")
                : null;
            double liveUT = SafeCurrentUtForResumeAnchor();
            if (!TryTakeCommittedTreeForSpawnedVesselRestore(
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId,
                    out CommittedSpawnedVesselRestoreAction action,
                    activeVesselGuid,
                    liveUT))
            {
                // A match refused only because its recording runs past the live clock (a
                // stock revert or a load put the vessel back before its committed future):
                // leave the vessel idle for this scene, as a DiscardTree does, so the 1 Hz
                // retry does not resume it the moment the clock passes the recording's end.
                if (!double.IsNaN(liveUT)
                    && !TryFindCommittedTreeForSpawnedVessel(
                        RecordingStore.CommittedTrees,
                        activeVesselPid,
                        out _,
                        out _,
                        activeVesselGuid,
                        liveUT)
                    && TryFindCommittedTreeForSpawnedVessel(
                        RecordingStore.CommittedTrees,
                        activeVesselPid,
                        out _,
                        out _,
                        activeVesselGuid))
                {
                    BeginCommittedSpawnedRestoreSuppression(
                        ref committedSpawnedRestoreSuppressedPid,
                        activeVesselPid,
                        activeVessel.vesselName,
                        hasActiveTree: activeTree != null,
                        hasRecorder: recorder != null,
                        reason: "committed-recording-ahead-of-clock");
                }
                return false;
            }

            activeTree = committedTree;
            EnsureBackgroundRecorderAttached("TryRestoreCommittedTreeForSpawnedActiveVessel");

            bool restored = false;
            bool continuedInSegment = false;
            if (action == CommittedSpawnedVesselRestoreAction.PromoteFromBackground)
                backgroundRecorder?.OnVesselRemovedFromBackground(activeVesselPid);
            if (!callerBuildsOwnSegment)
            {
                activeTree.Recordings.TryGetValue(targetRecordingId, out Recording targetRec);
                ResumedPartCoverage coverage = ResumedSpawnPartCoverage.Evaluate(
                    targetRec, CollectLivePartPersistentIds(activeVessel));
                if (coverage.Covered)
                {
                    ParsekLog.Verbose("Flight",
                        $"TryRestoreCommittedTreeForSpawnedActiveVessel: rec={targetRecordingId} " +
                        $"pid={activeVesselPid} resumes in place (parts {coverage.Reason}, " +
                        $"livePids={coverage.LivePartCount} snapshotPids={coverage.SnapshotPartCount} " +
                        $"snapshot={coverage.SnapshotSource})");
                }
                else
                {
                    continuedInSegment = TryContinueRestoredSpawnInNewSegment(
                        targetRecordingId, activeVessel, action, coverage, out restored);
                }
            }

            if (!continuedInSegment)
            {
                if (action == CommittedSpawnedVesselRestoreAction.ResumeActiveRecording)
                {
                    restored = ResumeCommittedActiveRecording(targetRecordingId, activeVessel);
                }
                else
                {
                    PromoteRecordingFromBackground(targetRecordingId, activeVessel);
                    restored = recorder != null && recorder.IsRecording;
                }
            }

            if (!restored)
            {
                ParsekLog.Warn("Flight",
                    $"TryRestoreCommittedTreeForSpawnedActiveVessel: tree '{activeTree.TreeName}' " +
                    $"matched vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}, " +
                    "but recorder attach failed; tree remains restored for late recovery");
                RecorderStateLog.RecState("CommittedSpawnedRestore:failed", CaptureRecorderState());
                return false;
            }

            ParsekLog.Info("Flight",
                $"TryRestoreCommittedTreeForSpawnedActiveVessel: restored tree '{activeTree.TreeName}' " +
                $"for vessel '{activeVessel.vesselName}' pid={activeVesselPid} via {action}" +
                (continuedInSegment
                    ? $" (continues in new segment '{activeTree.ActiveRecordingId}')"
                    : ""));
            // Surface a screen message mirroring the launch and post-switch auto-record
            // paths. OnFlightReady fires before KSP's ScreenMessages UI queue is ready
            // to display messages; calling ScreenMessage directly here gets the toast
            // swallowed by the scene-load transition. Defer to a coroutine that waits
            // a short duration so the UI has settled — the launch path's (auto) toast
            // works because it posts from OnVesselSituationChange after physics starts,
            // well after scene-load.
            StartCoroutine(DeferredResumeScreenMessage());
            RecorderStateLog.RecState("CommittedSpawnedRestore:post", CaptureRecorderState());
            return true;
        }

        private System.Collections.IEnumerator DeferredResumeScreenMessage()
        {
            yield return new WaitForSeconds(0.5f);
            ScreenMessage("Recording STARTED (resume)", 4f);
        }

        /// <summary>
        /// The live vessel's part persistentIds (zero skipped), or an empty list when the
        /// vessel or its part list is unavailable.
        /// </summary>
        private static List<uint> CollectLivePartPersistentIds(Vessel vessel)
        {
            var pids = new List<uint>();
            if (vessel == null || vessel.parts == null)
                return pids;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part part = vessel.parts[i];
                if (part != null && part.persistentId != 0)
                    pids.Add(part.persistentId);
            }
            return pids;
        }

        /// <summary>
        /// The restore matched a committed recording whose snapshot does not hold the live
        /// vessel's part persistentIds (a Parsek spawn regenerated them). Recording into it
        /// would write part events its ghost cannot resolve, so attach a new
        /// switch-continuation segment under it (the plan's step 4 for flying a committed
        /// spawned vessel), bind the recorder to the segment and arm a session for it, which
        /// gives the segment scoped Discard and the scene-exit no-op auto-discard. Returns
        /// false (nothing changed, the caller resumes in place as before) when the segment
        /// cannot be attached; <paramref name="restored"/> reports the recorder bind.
        /// </summary>
        private bool TryContinueRestoredSpawnInNewSegment(
            string targetRecordingId,
            Vessel activeVessel,
            CommittedSpawnedVesselRestoreAction action,
            ResumedPartCoverage coverage,
            out bool restored)
        {
            restored = false;
            if (activeTree == null || activeVessel == null || string.IsNullOrEmpty(targetRecordingId))
                return false;

            uint livePid = activeVessel.persistentId;
            double ut = SafeCurrentUtForResumeAnchor();
            if (double.IsNaN(ut))
            {
                ParsekLog.Warn("Flight",
                    $"Resumed spawn segment refused: rec={targetRecordingId} pid={livePid} " +
                    "reason=no-clock - recording into the target as before");
                return false;
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                targetRecordingId,
                livePid,
                activeVessel.vesselName ?? "Unknown",
                ut,
                SwitchSegmentEntryReason.ResumedSpawnedVessel,
                Guid.Empty,
                sessionId,
                newRecordingId,
                newBranchPointId,
                u => BuildSwitchSegmentBoundaryPoint(activeVessel, u),
                sourceVesselPersistentId: 0u);
            if (!creation.Created)
            {
                ParsekLog.Warn("Flight",
                    $"Resumed spawn segment refused: rec={targetRecordingId} pid={livePid} " +
                    $"reason={creation.FailureReason ?? "<null>"} - recording into the target as before");
                return false;
            }

            // The promote path removes the live pid from the background map; the segment
            // replaces that promotion, so do the same here.
            activeTree.BackgroundMap?.Remove(livePid);

            ParsekLog.Info("Flight",
                string.Format(CultureInfo.InvariantCulture,
                    "Resumed spawn continues in a new segment: rec={0} segment={1} vessel='{2}' pid={3} " +
                    "action={4} livePids={5} snapshotPids={6} uncovered={7} snapshot={8} ut={9}",
                    targetRecordingId,
                    newRecordingId,
                    activeVessel.vesselName ?? "<unnamed>",
                    livePid,
                    action,
                    coverage.LivePartCount,
                    coverage.SnapshotPartCount,
                    coverage.UncoveredPartCount,
                    coverage.SnapshotSource ?? "none",
                    ut.ToString("R", CultureInfo.InvariantCulture)));

            // No recorder is live here (the restore runs only when none is), so the bind
            // starts the segment's recorder without flushing anything into the target.
            restored = BindLiveRecorderToSwitchSegment(
                activeVessel, targetRecordingId, newRecordingId,
                routeTag: "resumed-spawn", switchUT: ut);

            ArmSwitchSegmentSession(
                SwitchSegmentEntryReason.ResumedSpawnedVessel,
                Guid.Empty,
                sessionId,
                activeTree.Id,
                creation.ParentRecordingId,
                newRecordingId,
                ut,
                livePid,
                sourcePid: 0u,
                committedTreeId: activeTree.Id);
            return true;
        }

        private bool ResumeCommittedActiveRecording(string recordingId, Vessel activeVessel)
        {
            if (activeTree == null || activeVessel == null || string.IsNullOrEmpty(recordingId))
                return false;

            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;
            recorder.StartRecording(isPromotion: true);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"ResumeCommittedActiveRecording: StartRecording returned IsRecording=false for " +
                    $"recording '{recordingId}' pid={activeVessel.persistentId}");
                recorder = null;
                activeTree.ActiveRecordingId = null;
                activeTree.BackgroundMap[activeVessel.persistentId] = recordingId;
                backgroundRecorder?.OnVesselBackgrounded(activeVessel.persistentId);
                return false;
            }

            PrepareSessionStateForRecorderStart("ResumeCommittedActiveRecording");

            // BUG #2: anchor the live-segment duration the pre-switch dialog will show.
            // The resumed committed recording's Points are appended in place, so its
            // StartUT is the original (multi-year-ago) launch UT — currentUT - StartUT
            // would still render the whole span. Capture the UT at which THIS recorder
            // session began appending so currentUT - liveResumeSessionStartUT yields the
            // short segment the player just flew.
            liveResumeSessionStartUT = SafeCurrentUtForResumeAnchor();
            ParsekLog.Verbose("Flight",
                $"liveResumeSessionStartUT set reason=ResumeCommittedActiveRecording " +
                $"recId={recordingId} " +
                $"ut={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)}");

            ParsekLog.Info("Flight",
                $"ResumeCommittedActiveRecording: resumed committed recording '{recordingId}' " +
                $"on vessel '{activeVessel.vesselName}' pid={activeVessel.persistentId}");
            return true;
        }

        /// <summary>
        /// The persistentIds of every vessel in <c>FlightGlobals.Vessels</c>, or null when the
        /// vessel list cannot be read (xUnit, no FlightGlobals), so a caller never mistakes an
        /// unreadable scene for an empty one.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static HashSet<uint> TryBuildLiveVesselPidSet()
        {
            List<Vessel> vessels;
            try { vessels = FlightGlobals.Vessels; }
            catch (TypeInitializationException) { return null; }
            catch (MissingMethodException) { return null; }
            catch (System.Security.SecurityException) { return null; }
            if (vessels == null)
                return null;
            var pids = new HashSet<uint>();
            for (int i = 0; i < vessels.Count; i++)
            {
                if (vessels[i] != null)
                    pids.Add(vessels[i].persistentId);
            }
            return pids;
        }

        /// <summary>
        /// Flight-ready consume of the fresh recording OnLoad armed after declining a saved
        /// active tree this session discarded (QUICKLOAD-INTO-DISCARDED-FLIGHT-RECORDS-AGAIN).
        /// Starts through <see cref="StartRecording"/>, the auto-record entry, which builds a new
        /// tree with fresh ids because no tree is active here. Called on every OnFlightReady path
        /// so the request never outlives this scene.
        /// </summary>
        private void ConsumeFreshRecordingAfterDiscardedRestore(bool restoreScheduled)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            ParsekScenario.ConsumeFreshRecordingAfterDiscard(
                restoreScheduled,
                recorderLive: recorder != null && recorder.IsRecording,
                hasActiveTree: activeTree != null,
                hasActiveVessel: v != null && !GhostMapPresence.IsGhostMapVessel(v.persistentId),
                startRecording: () =>
                {
                    StartRecording(suppressStartScreenMessage: true);
                    if (IsRecording)
                        Log("Auto-record started (quickload into a discarded flight)");
                    return IsRecording;
                });
        }

        /// <summary>
        /// Quickload-resume path (Bug C fix). Waits for the active vessel to load,
        /// matches it by name against the pending-Limbo tree's active recording,
        /// updates the tree's vessel PID (KSP can regenerate PIDs across quickload),
        /// constructs a fresh <see cref="FlightRecorder"/>, and calls
        /// <c>StartRecording(isPromotion: true)</c> so recording resumes appending to
        /// the same chain segment that was active at quicksave time.
        ///
        /// <para>The <c>isPromotion: true</c> flag invokes bug A's seed-event skip
        /// so no duplicate DeployableExtended / LightOn / etc. events fire at the
        /// quickload UT.</para>
        ///
        /// <para>If the vessel can't be found within 3 seconds (name change, EVA
        /// boarded between save and load, etc.), the tree is left in pending-Limbo
        /// and a warning logged. The player can manually trigger the merge dialog
        /// via scene exit if they want to commit it as-is.</para>
        /// </summary>
        IEnumerator RestoreActiveTreeFromPending()
        {
            // #267: reentrancy guard — prevents OnVesselSwitchComplete, OnVesselWillDestroy,
            // FinalizeTreeOnSceneChange from mutating activeTree/recorder during the yield window.
            restoringActiveTree = true;
            try
            {
            // NOTE: body intentionally not re-indented to minimize diff
            RecorderStateLog.RecState("Restore:start", CaptureRecorderState());
            // Re-Fly retry carve-out: when the previous Re-Fly attempt was
            // post-destruction finalized before the player clicked Esc >
            // Revert > Retry from Rewind Point, the in-memory pending tree
            // is in Finalized state (set by ShowPostDestructionTreeMergeDialog
            // / CommitTreeSceneExit). TryRestoreActiveTreeNode keeps the
            // Finalized tree as-is (#290d) and no Quickload restore gets
            // scheduled, so the new in-place fork that AtomicMarkerWrite
            // just attached to the same tree has no recorder bound. Accept
            // the Finalized state here when a Re-Fly session is active so
            // the marker-swap path below picks up the new fork and binds a
            // recorder to it. Without this carve-out, the OnFlightReady
            // merge-dialog skip leaves the player in a "fresh" attempt with
            // no recording happening at all.
            // The coroutine is called both from the existing Limbo dispatch
            // and from the OnFlightReady Re-Fly-retry carve-out (which
            // upgrades restoreMode based on ShouldUpgradeRestoreModeForReFlyRetry).
            // Re-read InPlaceContinuation here rather than trusting a
            // dispatch-time snapshot — between StartCoroutine and the body
            // executing, other code paths could in theory mutate the marker.
            // The synchronous flow has the marker stable in practice, but
            // the explicit re-read keeps this independent of dispatcher
            // assumptions.
            bool acceptFinalizedForReFly = ShouldAcceptFinalizedPendingTreeForReFlyRetry(
                hasPendingTree: PendingTreeSlot.HasPendingTree,
                pendingTreeIsFinalized:
                    PendingTreeSlot.PendingTreeStateValue == PendingTreeState.Finalized,
                reFlyInPlaceContinuationActive:
                    ParsekScenario.IsReFlyInPlaceContinuationActive());
            if (!PendingTreeSlot.HasPendingTree
                || (PendingTreeSlot.PendingTreeStateValue != PendingTreeState.Limbo
                    && !acceptFinalizedForReFly))
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPending: no pending-Limbo tree, skipping");
                yield break;
            }
            if (acceptFinalizedForReFly)
            {
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: accepting Finalized pending tree " +
                    $"'{PendingTreeSlot.PendingTree?.TreeName}' — active Re-Fly session " +
                    "needs a recorder bound to the freshly-attached fork");
            }

            var tree = PendingTreeSlot.PendingTree;
            string activeRecId = tree.ActiveRecordingId;

            // Bug #585 follow-up (PR #558 P1 review): gate marker read on
            // RewindInvokeContext consumption. In the async-FLIGHT-load path,
            // ParsekScenario.OnLoad schedules our restore for OnFlightReady
            // BEFORE RewindInvoker.RunStripActivateMarker has had a chance to
            // run AtomicMarkerWrite -- RunStripActivateMarker is itself
            // deferred via WaitForFlightReadyAndInvoke until
            // FlightGlobals.ready flips, which races with the
            // GameEvents.onFlightReady fire that triggers us. If we read the
            // marker before AtomicMarkerWrite completes we see it as null,
            // fall through to no-swap, and the wait loop targets the stale
            // pre-rewind ActiveRecordingId -- exactly the bug #585 race.
            // Yield until the pending invocation context clears (or a bounded
            // timeout) so the marker write is guaranteed to have completed.
            if (RewindInvokeContext.Pending)
            {
                int markerWaitFrame = 0;
                const int MaxMarkerWaitFrames = 300;
                while (RewindInvokeContext.Pending && markerWaitFrame < MaxMarkerWaitFrames)
                {
                    markerWaitFrame++;
                    yield return null;
                }
                if (RewindInvokeContext.Pending)
                {
                    ParsekLog.Warn("Flight",
                        $"RestoreActiveTreeFromPending: timed out waiting for RewindInvokeContext " +
                        $"to clear after {MaxMarkerWaitFrames} frame(s); proceeding without marker swap " +
                        $"(bug #585 follow-up: marker write race)");
                }
                else
                {
                    ParsekLog.Verbose("Flight",
                        $"RestoreActiveTreeFromPending: waited {markerWaitFrame} frame(s) for " +
                        $"RewindInvokeContext to clear before reading ActiveReFlySessionMarker");
                }
            }

            // Bug #585: in-place continuation Re-Fly carve-out. The rewind
            // quicksave's ActiveRecordingId still points at the pre-rewind
            // active vessel (just killed by PostLoadStripper). The live
            // ReFlySessionMarker pins the recording the player wants to keep
            // recording into; for an in-place continuation
            // (OriginChildRecordingId == ActiveReFlyRecordingId) we must swap
            // the wait target to the marker's recording. Without the swap the
            // wait loop targets a dead pid, times out at 3s, and the tree
            // stays in Limbo for the rest of the session.
            string preMarkerActiveRecId = activeRecId;
            string preMarkerActiveName = activeRecId != null
                && tree.Recordings.TryGetValue(activeRecId, out var preMarkerActiveRec)
                ? preMarkerActiveRec?.VesselName : null;
            uint preMarkerActivePid = activeRecId != null
                && tree.Recordings.TryGetValue(activeRecId, out var preMarkerActiveRec2)
                ? (preMarkerActiveRec2?.VesselPersistentId ?? 0u) : 0u;
            var marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            // Issue #734 race fix: AtomicMarkerWrite usually attaches the
            // in-place fork to the pending tree at marker-write time, but
            // on async FLIGHT loads both AtomicMarkerWrite and this restore
            // coroutine defer to onFlightReady; if this coroutine fires
            // first (or AtomicMarkerWrite ran on a different tree handle),
            // the fork lives in CommittedRecordings but is not yet in
            // tree.Recordings. ResolveInPlaceContinuationTarget would then
            // refuse the swap with reason="marker-recording-missing-from-tree"
            // and the recorder's flush would warn / data-loss. Reconcile
            // here from the committed list before the swap runs. The
            // helper is idempotent so it is a no-op when the eager path
            // already attached.
            ReconcileInPlaceForkIntoTreeIfNeeded(tree, marker);

            var markerSwap = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker,
                tree.Id,
                activeRecId,
                recId =>
                {
                    if (string.IsNullOrEmpty(recId)) return null;
                    if (!tree.Recordings.TryGetValue(recId, out var rec) || rec == null)
                        return null;
                    return (rec.VesselName, rec.VesselPersistentId);
                });
            if (markerSwap.ShouldSwap)
            {
                tree.ActiveRecordingId = markerSwap.TargetRecordingId;
                activeRecId = markerSwap.TargetRecordingId;
                // Bug #585 follow-up (PR #558 P2 review): the tree was loaded
                // with BackgroundMap rebuilt against the OLD ActiveRecordingId,
                // so the NEW active recording may still appear as a background
                // entry in tree.BackgroundMap. EnsureBackgroundRecorderAttached
                // later seeds the BackgroundRecorder from this map -- without
                // a rebuild, the recording would be tracked as both active
                // (live recorder) and background (BackgroundRecorder).
                // RebuildBackgroundMap re-runs IsBackgroundMapEligible against
                // the swapped ActiveRecordingId and excludes it from the map.
                int bgEntriesBefore = tree.BackgroundMap.Count;
                tree.RebuildBackgroundMap();
                int bgEntriesAfter = tree.BackgroundMap.Count;
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: in-place continuation marker swapped target " +
                    $"rec='{preMarkerActiveRecId ?? "<null>"}'->\'{markerSwap.TargetRecordingId}\' " +
                    $"vessel='{preMarkerActiveName ?? "<null>"}'->\'{markerSwap.TargetVesselName ?? "<null>"}\' " +
                    $"pid={preMarkerActivePid}->{markerSwap.TargetVesselPersistentId} " +
                    $"sess={marker?.SessionId ?? "<no-id>"} " +
                    $"bgMapEntries={bgEntriesBefore}->{bgEntriesAfter}");
            }
            else if (marker != null)
            {
                ParsekLog.Verbose("Flight",
                    $"RestoreActiveTreeFromPending: marker present but no swap " +
                    $"(reason={markerSwap.Reason ?? "<none>"} sess={marker.SessionId ?? "<no-id>"})");
            }

            if (string.IsNullOrEmpty(activeRecId)
                || !tree.Recordings.TryGetValue(activeRecId, out var activeRec))
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: pending tree '{tree.TreeName}' has no resolvable " +
                    $"activeRecordingId — leaving in Limbo");
                yield break;
            }

            string targetName = activeRec.VesselName;
            uint targetPid = activeRec.VesselPersistentId;
            ParsekLog.Info("Flight",
                $"RestoreActiveTreeFromPending: waiting for vessel '{targetName}' (pid={targetPid}) to load " +
                $"(activeRecId={activeRecId})");

            // Wait up to 3 seconds for the current active vessel to match.
            // Primary match = PID (locale-proof, available immediately even when
            // vesselName is still a raw #autoLOC key). Bug #290b.
            // Secondary match = vessel name, only for identity-less legacy recordings (no PID
            // and no launch guid; QuickloadResumeMatchGuard.MayMatchByNameFallback).
            // Tertiary (edge case 3): if the active recording was an EVA kerbal who got
            // boarded between quicksave and quickload, match against the parent recording's
            // vessel by walking ParentRecordingId up the EVA chain.
            //
            // BDOCK-1 flight-16 guard (INV4-PARTEVENT-PID red): every acceptance is
            // gated through QuickloadResumeMatchGuard. A same-session launch_vessel
            // FLIGHT->FLIGHT reload can be misclassified as a quickload (stale
            // vesselSwitchPending flag); the new scene's active vessel is then a FRESH
            // ROLLOUT of (typically) the same craft file, and the name fallback happily
            // matches it — remapping the stashed tree's recording onto a different
            // physical launch and corrupting it with foreign-pid part events. A fresh
            // rollout is rejected outright (recording-independent, break immediately);
            // a conclusive RecordedVesselGuid-vs-Vessel.id mismatch skips the pid/name
            // match for that recording while still allowing the EVA-parent walk to
            // match a parent whose guid agrees.
            Recording matchedRec = activeRec;
            string matchedRecId = activeRecId;
            float deadline = UnityEngine.Time.time + 3f;
            Vessel matched = null;
            bool loggedGuidReject = false;
            var loggedParentGuidRejects = new HashSet<string>();
            uint refusedFreshRolloutPid = 0;
            while (UnityEngine.Time.time < deadline)
            {
                var v = FlightGlobals.ActiveVessel;
                if (v != null)
                {
                    string liveGuid = v.id != Guid.Empty ? v.id.ToString("N") : null;

                    if (QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                            v.persistentId, SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid))
                    {
                        ParsekLog.Info("Flight",
                            $"RestoreActiveTreeFromPending: refusing to adopt fresh-rollout vessel " +
                            $"'{v.vesselName}' pid={v.persistentId} liveGuid={liveGuid ?? "<none>"} " +
                            $"for recording '{activeRecId}' (recordedPid={targetPid}, " +
                            $"recordedGuid={activeRec.RecordedVesselGuid ?? "<none>"}) — a fresh " +
                            "launch is a different physical launch; not adopting");
                        refusedFreshRolloutPid = v.persistentId;
                        break;
                    }

                    bool activeRecGuidRejected = QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                        activeRec.RecordedVesselGuid, liveGuid);
                    if (activeRecGuidRejected && !loggedGuidReject)
                    {
                        loggedGuidReject = true;
                        ParsekLog.Info("Flight",
                            $"RestoreActiveTreeFromPending: active vessel '{v.vesselName}' " +
                            $"pid={v.persistentId} liveGuid={liveGuid ?? "<none>"} conclusively " +
                            $"differs from recording '{activeRecId}' recordedGuid=" +
                            $"{activeRec.RecordedVesselGuid ?? "<none>"} — skipping pid/name match " +
                            "(EVA-parent fallback still evaluated)");
                    }

                    // Primary: PID match (locale-proof, available immediately)
                    if (!activeRecGuidRejected && targetPid != 0 && v.persistentId == targetPid)
                    {
                        matched = v;
                        ParsekLog.Verbose("Flight",
                            $"RestoreActiveTreeFromPending: PID match pid={targetPid}");
                        break;
                    }
                    // Secondary: name match, ONLY for an identity-less legacy recording (no
                    // pid and no launch guid). A recording with a pid or guid is matched by
                    // identity alone: a renamed vessel keeps both, and a same-named vessel of
                    // another launch must never be adopted.
                    if (!activeRecGuidRejected
                        && QuickloadResumeMatchGuard.MayMatchByNameFallback(
                            targetPid, activeRec.RecordedVesselGuid)
                        && v.vesselName == targetName)
                    {
                        matched = v;
                        ParsekLog.Verbose("Flight",
                            $"RestoreActiveTreeFromPending: name match '{targetName}'");
                        break;
                    }
                    // Tertiary: walk ParentRecordingId chain and try each parent's PID/name
                    Recording probe = activeRec;
                    int walkDepth = 0;
                    while (walkDepth < 5 && !string.IsNullOrEmpty(probe?.ParentRecordingId))
                    {
                        if (!tree.Recordings.TryGetValue(probe.ParentRecordingId, out probe))
                            break;
                        if (probe != null &&
                            QuickloadResumeMatchGuard.LaunchGuidConclusivelyDiffers(
                                probe.RecordedVesselGuid, liveGuid))
                        {
                            // Guard skip must be logged (review of PR #1345): a
                            // guid-rejected parent otherwise declines silently and
                            // the coroutine ends in the generic not-active Warn
                            // with no trace this gate fired. Once per parent, not
                            // per polling frame (review of PR #1371) - this walk
                            // re-runs every frame of the 3 s wait, mirroring the
                            // loggedGuidReject latch on the active-rec reject above.
                            if (loggedParentGuidRejects.Add(probe.RecordingId ?? string.Empty))
                                ParsekLog.Info("Flight",
                                    $"RestoreActiveTreeFromPending: parent-walk guid gate " +
                                    $"rejected rec={ParsekFlightGhostPositioner.ShortRecordingId(probe.RecordingId)} " +
                                    $"(recordedGuid differs conclusively from live vessel)");
                            walkDepth++;
                            continue;
                        }
                        if (probe != null && (
                            (probe.VesselPersistentId != 0 && v.persistentId == probe.VesselPersistentId) ||
                            (QuickloadResumeMatchGuard.MayMatchByNameFallback(
                                probe.VesselPersistentId, probe.RecordedVesselGuid)
                             && v.vesselName == probe.VesselName)))
                        {
                            matched = v;
                            matchedRec = probe;
                            matchedRecId = probe.RecordingId;
                            ParsekLog.Info("Flight",
                                $"RestoreActiveTreeFromPending: fallback match — active vessel pid={v.persistentId} " +
                                $"matches parent recording '{matchedRecId}' (original active was '{targetName}', " +
                                $"EVA kerbal likely boarded between F5 and F9)");
                            // Flip the tree's active-recording pointer to the parent
                            tree.ActiveRecordingId = matchedRecId;
                            break;
                        }
                        walkDepth++;
                    }
                    if (matched != null) break;
                }
                yield return null;
            }

            // FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME / -KEPT-TREE-PENDING: a tree refused
            // for the fresh rollout is resolved here so nothing parked lingers for a later
            // stash to overwrite or a merge dialog to offer over committed recordings: an
            // idle resumed copy of a committed tree (BDOCK-1's re-adopted station) is
            // discarded, every other tree is committed. A Re-Fly-owned tree, one under an
            // active merge journal or a replaced slot falls through to the Limbo give-up.
            if (matched == null
                && refusedFreshRolloutPid != 0
                && IsFreshRolloutRefusedTreeResolved(DisposeFreshRolloutRefusedPendingTree(
                        "quickload-restore", tree, refusedFreshRolloutPid)))
            {
                yield break;
            }

            if (matched == null)
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: vessel '{targetName}' (and no EVA parent fallback) " +
                    "not active within 3s — leaving tree in Limbo (user can trigger merge dialog via scene exit)");

                // R1-EMPTY-PROVISIONAL detection point 1. The generic Limbo warn
                // above says nothing about the Re-Fly session, so a give-up here
                // has always been indistinguishable from an ordinary failed
                // quickload resume. If a session IS live, no recorder is bound to
                // its provisional at this moment, and unless something binds one
                // later the merge will have nothing to supersede the origin with.
                // Observation only: raise loudly, do not change control flow.
                var giveUpMarker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
                UnboundProvisionalRaise giveUp =
                    ReFlyProvisionalBinding.EvaluateRestoreGiveUp(giveUpMarker, tree?.Id);
                if (giveUp.ShouldRaise)
                {
                    ParsekLog.Warn("ReFlySession",
                        $"outcome=unbound-refly-provisional reason={giveUp.Reason} " +
                        $"sess={giveUpMarker.SessionId ?? "<no-id>"} " +
                        $"provisional={giveUpMarker.ActiveReFlyRecordingId ?? "<none>"} " +
                        $"markerTree={giveUpMarker.TreeId ?? "<none>"} " +
                        $"attemptedTree={tree?.Id ?? "<none>"} " +
                        $"attemptedRec={activeRecId ?? "<none>"} " +
                        $"attemptedVessel='{targetName}' attemptedPid={targetPid} — the re-fly " +
                        "restore gave up, so NOTHING is recording into this session's " +
                        "provisional. Unless a recorder binds to it before the merge, the " +
                        "re-fly will supersede nothing and the origin branch stays effective");
                    try
                    {
                        ParsekLog.ScreenMessage(
                            "Parsek: re-fly attempt is not recording; it will not replace "
                            + "the original", 8f);
                    }
                    catch (System.NullReferenceException) { /* xUnit: no KSP UI */ }
                    catch (System.MissingMethodException) { /* xUnit: no KSP UI */ }
                    catch (System.TypeInitializationException) { /* xUnit: no KSP UI */ }
                }
                yield break;
            }

            // After a parent-fallback match, the rest of the coroutine uses matchedRec/matchedRecId
            activeRec = matchedRec;
            activeRecId = matchedRecId;
            RecorderStateLog.RecState("Restore:matched", CaptureRecorderState());

            // PID remap: KSP may have regenerated the persistentId across quickload.
            // Tree.BackgroundMap is keyed by PID, so update any old-PID entries too.
            uint oldPid = activeRec.VesselPersistentId;
            uint newPid = matched.persistentId;
            if (oldPid != newPid)
            {
                activeRec.VesselPersistentId = newPid;
                if (oldPid != 0 && tree.BackgroundMap.TryGetValue(oldPid, out var bgRecId))
                {
                    tree.BackgroundMap.Remove(oldPid);
                    if (bgRecId != activeRecId)
                        tree.BackgroundMap[newPid] = bgRecId;
                }
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPending: PID remap {oldPid} → {newPid} for '{targetName}'");
            }

            // Pop the tree out of pending-Limbo (non-destructive — preserves sidecar files)
            // and re-install as the live active tree.
            activeTree = PendingTreeSlot.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPending: PopPendingTree returned null after we verified the tree exists");
                yield break;
            }
            // PopPendingTree just emptied the main pending slot; a saved pending
            // tree from the same save can now move back unless another path filled it.
            PendingTreeSlot.PromoteSavedPendingTreeAfterActiveRestore(
                "RestoreActiveTreeFromPending");

            // Construct a fresh FlightRecorder pointed at the restored tree.
            recorder = new FlightRecorder();
            recorder.ActiveTree = activeTree;

            // Restore recorder state persisted in the PARSEK_ACTIVE_TREE node
            if (!string.IsNullOrEmpty(ParsekScenario.pendingActiveTreeResumeRewindSave))
            {
                recorder.SetRewindSaveFileNameForRestore(
                    ParsekScenario.pendingActiveTreeResumeRewindSave,
                    "quickload-resume from PARSEK_ACTIVE_TREE");
                ParsekScenario.pendingActiveTreeResumeRewindSave = null;
            }

            // Bug fix: clear any stale Destroyed terminal verdict on the
            // recording we're about to resume. A transient NullSolver event
            // earlier in the recording (e.g. stage decouple) may have made
            // TryFinalizeRecording fall back to live-orbit and stamp Destroyed
            // via the sub-surface path. Without this clear, FinalizerCache's
            // already-classified-destroyed short-circuit declines every refresh
            // for the rest of the session, and the vessel stays labelled
            // "Destroyed" in the STASH UI even though the player is flying it
            // again post-rewind. The stamp lives on the Recording, so resetting
            // the in-memory FinalizationCache via StartRecording is not enough.
            IncompleteBallisticSceneExitFinalizer.ClearStaleDestroyedTerminalForResume(
                activeRec,
                "RestoreActiveTreeFromPending");

            // Start recording as a promotion (isPromotion: true) so the Bug A seed-event
            // skip kicks in — no duplicate DeployableExtended / LightOn / etc. events at
            // the quickload UT. The new recorder's buffers start empty and will append
            // to activeTree.Recordings[activeRecId] on the next chain boundary or
            // scene exit flush.
            recorder.StartRecording(isPromotion: true);
            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPending: StartRecording returned IsRecording=false for " +
                    $"'{targetName}' — restore incomplete");
                yield break;
            }

            PrepareSessionStateForRecorderStart("RestoreActiveTreeFromPending");

            // Re-attach the BackgroundRecorder for the tree.
            EnsureBackgroundRecorderAttached("RestoreActiveTreeFromPending");

            ParsekLog.Info("Flight",
                $"RestoreActiveTreeFromPending: resumed recording tree '{activeTree.TreeName}' " +
                $"activeRec='{activeRecId}' vessel='{targetName}' pid={newPid} at UT={Planetarium.GetUniversalTime():F1}");
            RecorderStateLog.RecState("Restore:after-start", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }

            // M1 (PR #876 final review): dispatch any deferred consume now that
            // the restored tree is live. DispatchConsumeIntentIfArmed at the
            // top of OnFlightReady skips when ScheduleActiveTreeRestoreOnFlightReady
            // is non-None to avoid racing the restore. Re-invoking here picks
            // up an intent that survived the F5 -> reload round-trip and
            // attaches the new switch segment to the just-restored tree.
            DispatchConsumeIntentIfArmed();
        }

        /// <summary>
        /// Pure guard for <see cref="RestoreActiveTreeFromPendingForVesselSwitch"/>: true
        /// when the new scene's active vessel is the scene-entry fresh rollout, which a
        /// vessel-switch reload can never produce.
        /// </summary>
        internal static bool ShouldRefuseVesselSwitchRestoreForFreshRollout(
            uint activeVesselPid, uint sceneEntryFreshRolloutPid)
        {
            return QuickloadResumeMatchGuard.IsFreshRolloutCandidate(
                activeVesselPid, sceneEntryFreshRolloutPid);
        }

        /// <summary>
        /// What happens to the pending tree after a restore coroutine refused to put it
        /// on the scene-entry fresh rollout (todo FRESH-LAUNCH-REFUSED-TREE-PENDING-LIFETIME).
        /// </summary>
        internal enum FreshRolloutRefusedTreeDisposition
        {
            /// <summary>No pending tree; nothing to do.</summary>
            NoPendingTree = 0,
            /// <summary>A resumed copy of a committed tree with meaningless post-load content: discard it.</summary>
            DiscardResumedCommittedCopy = 1,
            /// <summary>
            /// No committed tree shares its id: a genuinely new tree, or a clean resumed
            /// copy whose committed original a save-loaded active node detached (the
            /// pending tree is then the only holder of that history). Commit it.
            /// </summary>
            CommitNotACommittedCopy = 2,
            /// <summary>A Re-Fly session or invocation owns the pending tree: keep it.</summary>
            KeepReFlyOwned = 3,
            /// <summary>
            /// A resumed copy that recorded something meaningful after the load: commit it
            /// over its committed original (the normal same-id replace commit).
            /// </summary>
            CommitMeaningfulResumedCopy = 4,
            /// <summary>A merge journal is active (the finisher owns tree state): keep it.</summary>
            KeepMergeJournalActive = 5,
            /// <summary>The pending slot no longer holds the tree the coroutine refused: keep it.</summary>
            KeepSlotReplaced = 6,
            /// <summary>
            /// The commit ran but the pending slot still holds the tree (the commit threw
            /// or refused): kept pending, as before the operator ruling.
            /// </summary>
            KeepCommitFailed = 7,
        }

        /// <summary>
        /// True when the refusal resolved the refused tree (discarded or committed), so
        /// the calling coroutine has nothing left to do with it. Every Keep outcome
        /// leaves the tree pending and the caller keeps its pre-ruling behaviour.
        /// </summary>
        internal static bool IsFreshRolloutRefusedTreeResolved(
            FreshRolloutRefusedTreeDisposition disposition)
        {
            return disposition == FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy
                || disposition == FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy
                || disposition == FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy;
        }

        /// <summary>
        /// Pure decision for a pending tree refused for a fresh rollout. The guards come
        /// first: a replaced slot, a Re-Fly session or invocation, or an active merge journal
        /// keeps the tree pending exactly as before. Past the guards a RESUMED copy of a
        /// committed tree (the copy-on-write clone a committed-tree restore made, identified
        /// by its id still being in the committed store) whose post-load content is
        /// meaningless (<see cref="RefusedResumedCopyTail"/>) is discarded; every other
        /// tree is committed at the refusal (operator ruling 2026-09-26, todo
        /// FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING).
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition ClassifyFreshRolloutRefusedTree(
            RecordingTree pendingTree,
            bool slotHoldsRefusedTree,
            bool committedTreeWithSameIdPresent,
            bool reFlyOwnsPendingTree,
            bool mergeJournalActive,
            bool postLoadContentNoOp)
        {
            if (pendingTree == null)
                return FreshRolloutRefusedTreeDisposition.NoPendingTree;
            if (!slotHoldsRefusedTree)
                return FreshRolloutRefusedTreeDisposition.KeepSlotReplaced;
            if (reFlyOwnsPendingTree)
                return FreshRolloutRefusedTreeDisposition.KeepReFlyOwned;
            if (mergeJournalActive)
                return FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive;
            if (string.IsNullOrEmpty(pendingTree.Id) || !committedTreeWithSameIdPresent)
                return FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy;
            if (!postLoadContentNoOp)
                return FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy;
            return FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy;
        }

        /// <summary>
        /// Shared tail of both fresh-rollout refusals (<see cref="RestoreActiveTreeFromPending"/>
        /// and <see cref="RestoreActiveTreeFromPendingForVesselSwitch"/>). For a discardable
        /// resumed copy it runs the merge dialog's whole-tree Discard steps: the discard
        /// primitive <see cref="ParsekScenario.DiscardPendingTreeAndRecalculate"/> (keeps every
        /// committed-overlap recording's sidecars and events, deletes only pending-only files,
        /// purges same-id event tails past the committed cutoffs, clears the restore attempt;
        /// its <c>TreeDiscardPurge</c> clears a merge journal scoped to the tree), then clears a
        /// switch-segment session bound to the tree, then refreshes <c>persistent.sfs</c> when
        /// the copy was already serialized. The committed original is never touched. Every
        /// other unguarded tree is committed through
        /// <see cref="CommitFreshRolloutRefusedPendingTree"/>. Returns the disposition; the
        /// caller keeps its existing behaviour for every Keep outcome.
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition DisposeFreshRolloutRefusedPendingTree(
            string refusalSite, RecordingTree refusedTree, uint refusedVesselPid)
        {
            RecordingTree pending = PendingTreeSlot.PendingTree;
            bool slotMatches = refusedTree != null && ReferenceEquals(pending, refusedTree);
            RecordingTree committedTree = RecordingStore.FindCommittedTreeById(pending?.Id);
            RefusedResumedCopyTail.Result tail = pending != null && committedTree != null
                ? RefusedResumedCopyTail.Evaluate(pending, committedTree)
                : new RefusedResumedCopyTail.Result { TailFromUT = double.NaN, TailToUT = double.NaN };
            bool reFlyOwns = ParsekScenario.IsReFlySessionActiveForQuickloadDiscard();
            var scenario = ParsekScenario.Instance;
            bool journalActive = !object.ReferenceEquals(null, scenario)
                && scenario.ActiveMergeJournal != null;
            var disposition = ClassifyFreshRolloutRefusedTree(
                pending, slotMatches, committedTree != null, reFlyOwns, journalActive, tail.IsNoOp);
            string site = refusalSite ?? "<unknown>";
            var ic = CultureInfo.InvariantCulture;
            string span = double.IsNaN(tail.TailFromUT)
                ? "none"
                : tail.TailFromUT.ToString("F1", ic) + ".." + tail.TailToUT.ToString("F1", ic) +
                  " (" + (tail.TailToUT - tail.TailFromUT).ToString("F1", ic) + "s)";

            switch (disposition)
            {
                case FreshRolloutRefusedTreeDisposition.DiscardResumedCommittedCopy:
                {
                    string treeId = pending.Id;
                    int total = pending.Recordings?.Count ?? 0;
                    bool serialized = PendingTreeSlot.PendingTreeSerializedForSave;
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): discarding resumed copy of committed tree " +
                        $"'{pending.TreeName}' (id={treeId}, state={PendingTreeSlot.PendingTreeStateValue}, " +
                        $"recordings={total}, droppedSpan={span}, droppedPoints={tail.TailPointCount}, " +
                        $"restoreAttemptArmed={CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(treeId)}, " +
                        $"serialized={serialized}, refusedPid={refusedVesselPid}) - committed history " +
                        "stays as committed, only the idle resumed seconds after the load are dropped");
                    ParsekScenario.DiscardPendingTreeAndRecalculate(
                        "fresh-rollout refusal discarded resumed committed copy tree=" + treeId +
                        " site=" + site);

                    // MergeDiscard's scoped switch-segment step is not run here, so a session
                    // bound to this tree would outlive it and resolve the committed tree on
                    // the next load (deferred merge dialog). Clear it.
                    var session = object.ReferenceEquals(null, scenario)
                        ? null : scenario.ActiveSwitchSegmentSession;
                    if (session != null && string.Equals(session.TreeId, treeId, StringComparison.Ordinal))
                        scenario.ClearSwitchSegmentSession(
                            "fresh-rollout refusal discarded resumed committed copy tree=" + treeId);

                    // A serialized copy sits in persistent.sfs as a resume node; rewrite it.
                    // quicksave.sfs is deliberately NOT refreshed (MergeDiscard does both): this
                    // discard is automatic, and overwriting the player's quicksave without a
                    // player action would destroy their F5 point.
                    if (serialized)
                        TreeDiscardService.RefreshPersistentSaveAfterDiscard(
                            "fresh-rollout refusal discard", total);
                    break;
                }
                case FreshRolloutRefusedTreeDisposition.CommitMeaningfulResumedCopy:
                    disposition = CommitFreshRolloutRefusedPendingTree(
                        site, pending, disposition, refusedVesselPid,
                        "meaningful-resumed-copy postLoadSpan=" + span +
                        " postLoadPoints=" + tail.TailPointCount.ToString(ic) +
                        " reason=" + (tail.KeepReason ?? "<none>"));
                    break;
                case FreshRolloutRefusedTreeDisposition.CommitNotACommittedCopy:
                    disposition = CommitFreshRolloutRefusedPendingTree(
                        site, pending, disposition, refusedVesselPid,
                        "not-a-committed-copy (a genuinely new tree, or a resumed copy whose " +
                        "committed original a save-loaded active node detached)");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepReFlyOwned:
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): keeping pending tree '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}) - a Re-Fly session owns it; not discarded");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepMergeJournalActive:
                    ParsekLog.Info("Flight",
                        $"Fresh-rollout refusal ({site}): keeping pending tree '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}) - merge journal " +
                        $"{scenario.ActiveMergeJournal.JournalId ?? "<no-id>"} is active; not discarded");
                    break;
                case FreshRolloutRefusedTreeDisposition.KeepSlotReplaced:
                    ParsekLog.Warn("Flight",
                        $"Fresh-rollout refusal ({site}): pending slot now holds '{pending.TreeName}' " +
                        $"(id={pending.Id ?? "<none>"}), not the refused tree " +
                        $"'{refusedTree?.TreeName ?? "<null>"}' (id={refusedTree?.Id ?? "<none>"}); not discarded");
                    break;
                default:
                    ParsekLog.Verbose("Flight",
                        $"Fresh-rollout refusal ({site}): no pending tree to dispose");
                    break;
            }
            return disposition;
        }

        /// <summary>
        /// Commits a refused pending tree at the refusal (operator ruling 2026-09-26, todo
        /// FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING) instead of leaving it pending for the next
        /// <see cref="PendingTreeSlot.StashPendingTree"/> to overwrite. It takes the same route
        /// a Limbo stash takes when FLIGHT is left under autoMerge
        /// (<c>ParsekScenario.AutoCommitPendingTreeOutsideFlight</c>'s Limbo-preserving
        /// full-fidelity branch): <see cref="MergeDialog.BuildDefaultVesselDecisions(RecordingTree)"/>
        /// then <see cref="MergeDialog.MergeCommit"/>, which runs CommitPendingTree +
        /// MarkTreeAsApplied, the optimizer, the ledger commit, the crew swap, the
        /// switch-segment / restore-attempt clear and OnTreeCommitted. A resumed copy that
        /// shares its tree id with a committed tree goes through
        /// <see cref="TreeCommitService.CommitTree"/>'s same-id replace branch, exactly as a
        /// resumed committed tree does when it is committed at scene exit: the committed
        /// tree slot and each shared recording slot are replaced in place, so no second tree
        /// with that id and no duplicate recording is created. The quicksave is not
        /// refreshed: this commit is automatic, and overwriting the player's quicksave with
        /// no player action would destroy their F5 point. <c>persistent.sfs</c> is written
        /// by the next ordinary save. Returns <paramref name="disposition"/> when the pending
        /// slot no longer holds the tree, else <see cref="FreshRolloutRefusedTreeDisposition.KeepCommitFailed"/>.
        /// </summary>
        internal static FreshRolloutRefusedTreeDisposition CommitFreshRolloutRefusedPendingTree(
            string site,
            RecordingTree pending,
            FreshRolloutRefusedTreeDisposition disposition,
            uint refusedVesselPid,
            string kindDetail)
        {
            string treeId = pending.Id ?? "<none>";
            int total = pending.Recordings?.Count ?? 0;
            PendingTreeState stateAtCommit = PendingTreeSlot.PendingTreeStateValue;
            bool serialized = PendingTreeSlot.PendingTreeSerializedForSave;
            RecordingTree committedTwin = RecordingStore.FindCommittedTreeById(pending.Id);
            int committedTwinRecordings = committedTwin?.Recordings?.Count ?? 0;
            try
            {
                var decisions = MergeDialog.BuildDefaultVesselDecisions(pending);
                int spawnCount = 0;
                foreach (var v in decisions.Values)
                    if (v) spawnCount++;
                ParsekLog.Info("Flight",
                    $"Fresh-rollout refusal ({site}): committing kept tree '{pending.TreeName}' " +
                    $"(id={treeId}, state={stateAtCommit}, recordings={total}, spawnable={spawnCount}, " +
                    $"committedTwinRecordings={(committedTwin != null ? committedTwinRecordings.ToString(CultureInfo.InvariantCulture) : "none")}, " +
                    $"serialized={serialized}, refusedPid={refusedVesselPid}, kind={kindDetail}) - " +
                    "committed at the refusal as a scene exit would, instead of left pending");
                MergeDialog.MergeCommit(pending, decisions, spawnCount,
                    refreshQuicksaveAfterCommit: false);
            }
            catch (Exception ex)
            {
                ParsekLog.Error("Flight",
                    $"Fresh-rollout refusal ({site}): commit of kept tree '{pending.TreeName}' " +
                    $"(id={treeId}) threw {ex.GetType().Name}: {ex.Message}");
            }

            if (ReferenceEquals(PendingTreeSlot.PendingTree, pending))
            {
                ParsekLog.Warn("Flight",
                    $"Fresh-rollout refusal ({site}): kept tree '{pending.TreeName}' (id={treeId}) is " +
                    "still pending after the commit attempt; left pending");
                return FreshRolloutRefusedTreeDisposition.KeepCommitFailed;
            }

            RecordingTree committedNow = RecordingStore.FindCommittedTreeById(pending.Id);
            if (committedNow != null && !ReferenceEquals(committedNow, pending))
            {
                // CommitTree's duplicate-skip: the committed original stays as it was and
                // the copy's post-load content is not in the committed store.
                ParsekLog.Warn("Flight",
                    $"Fresh-rollout refusal ({site}): committed store kept its original tree " +
                    $"'{committedNow.TreeName}' (id={treeId}) over the refused copy " +
                    "(CommitTree duplicate-skip); the copy's post-load content was not committed");
            }
            else
            {
                ParsekLog.Info("Flight",
                    $"Fresh-rollout refusal ({site}): committed kept tree '{pending.TreeName}' " +
                    $"(id={treeId}, {(committedTwin != null ? "replaced committed original" : "added as a committed tree")}, " +
                    $"committedTrees={RecordingStore.CommittedTrees.Count}, " +
                    $"committedRecordings={RecordingStore.CommittedRecordings.Count})");
            }
            return disposition;
        }

        /// <summary>
        /// Undo <see cref="ApplyPreTransitionForVesselSwitch"/> on a stashed tree: put the
        /// recording the stash moved into <c>BackgroundMap</c> back as
        /// <c>ActiveRecordingId</c>, which leaves the tree in exactly the shape the plain
        /// Limbo stash (<see cref="StashActiveTreeAsPendingLimbo"/>) produces. Used when the
        /// vessel-switch restore refuses a fresh rollout, so the refused tree continues on
        /// the name- and launch-guid-gated quickload-restore path instead of staying a
        /// <c>LimboVesselSwitch</c> that a later load would reinstall on whatever vessel is
        /// active. Returns false (tree untouched) when there is no in-memory record of the
        /// move or the tree no longer matches it (outsider-chain stash, a tree reloaded
        /// from disk, or a BackgroundMap entry that changed since).
        /// </summary>
        internal static bool TryRevertPreTransitionForVesselSwitch(
            RecordingTree tree, out string restoredActiveRecordingId)
        {
            restoredActiveRecordingId = null;
            if (tree == null || !string.IsNullOrEmpty(tree.ActiveRecordingId))
                return false;
            string recId = tree.VesselSwitchPreTransitionActiveRecordingId;
            uint pid = tree.VesselSwitchPreTransitionVesselPid;
            if (string.IsNullOrEmpty(recId) || pid == 0 || !tree.Recordings.ContainsKey(recId))
                return false;
            if (!tree.BackgroundMap.TryGetValue(pid, out string mapped) || mapped != recId)
                return false;
            tree.BackgroundMap.Remove(pid);
            tree.ActiveRecordingId = recId;
            tree.VesselSwitchPreTransitionActiveRecordingId = null;
            tree.VesselSwitchPreTransitionVesselPid = 0;
            restoredActiveRecordingId = recId;
            return true;
        }

        /// <summary>
        /// Bug #266: vessel-switch restore coroutine. The pending tree was pre-transitioned
        /// at stash time (recorder flushed, old active recording moved into BackgroundMap,
        /// ActiveRecordingId nulled), so the restore is much simpler than the quickload
        /// path: pop the tree, install as <c>activeTree</c>, attach a fresh
        /// <c>BackgroundRecorder</c>, and inspect the new active vessel:
        /// <list type="bullet">
        ///   <item>If its PID is in <c>tree.BackgroundMap</c> (round-trip — the player
        ///   returned to a vessel that's part of this tree), arm the watcher so the
        ///   tracked recording is only promoted after the first meaningful physical
        ///   modification.</item>
        ///   <item>Otherwise leave <c>recorder = null</c> and arm the same watcher for
        ///   outsider/start-fresh follow-up. The tree remains installed, but focus switch
        ///   alone still does not start recording.</item>
        /// </list>
        /// </summary>
        IEnumerator RestoreActiveTreeFromPendingForVesselSwitch()
        {
            // #267: reentrancy guard — same pattern as RestoreActiveTreeFromPending
            restoringActiveTree = true;
            try
            {
            // NOTE: body intentionally not re-indented to minimize diff
            RecorderStateLog.RecState("RestoreSwitch:start", CaptureRecorderState());
            if (!PendingTreeSlot.HasPendingTree
                || PendingTreeSlot.PendingTreeStateValue != PendingTreeState.LimboVesselSwitch)
            {
                ParsekLog.Verbose("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: no pending-LimboVesselSwitch tree, skipping");
                yield break;
            }

            // The tree this coroutine is about to act on; the fresh-rollout refusal
            // discards only if the pending slot still holds exactly this tree.
            RecordingTree stashedTree = PendingTreeSlot.PendingTree;

            // Wait up to ~3 seconds for FlightGlobals.ActiveVessel to populate.
            // Aligned with the existing quickload-resume coroutine
            // (RestoreActiveTreeFromPending, ~5470) so both restore paths use the
            // same wait budget — easier to reason about and tune in playtest.
            // Unlike the quickload path we don't name-match: we just need a
            // non-null active vessel so we can check whether it's in BackgroundMap.
            const float WaitBudgetSeconds = 3f;
            float deadline = UnityEngine.Time.time + WaitBudgetSeconds;
            while (UnityEngine.Time.time < deadline && FlightGlobals.ActiveVessel == null)
                yield return null;
            bool waitTimedOut = FlightGlobals.ActiveVessel == null;

            // FRESH-LAUNCH-JOINS-RESTORED-COMMITTED-TREE: a vessel-switch reload never
            // lands on a NEW_FROM_FILE / NEW_FROM_CRAFT_NODE rollout, so an active
            // vessel that IS the scene-entry fresh rollout means this FLIGHT->FLIGHT
            // reload was a new launch misread as a switch. Installing the stashed tree
            // here would record the launch as a parentless root inside that tree (a
            // committed tree's clone, when the stash came from a committed-tree
            // restore) and author its later dock as a same-tree merge. Refuse, exactly
            // as the quickload restore refuses a fresh rollout: the tree stays pending
            // and the launch gets its own tree on its first auto-record.
            var candidate = FlightGlobals.ActiveVessel;
            if (candidate != null
                && ShouldRefuseVesselSwitchRestoreForFreshRollout(
                    candidate.persistentId, SceneEntryVesselScope.SceneEntryFreshRolloutVesselPid))
            {
                ParsekLog.Info("Flight",
                    $"RestoreActiveTreeFromPendingForVesselSwitch: refusing to reinstall tree " +
                    $"'{PendingTreeSlot.PendingTree?.TreeName}' " +
                    $"(id={PendingTreeSlot.PendingTree?.Id ?? "<none>"}) for fresh-rollout vessel " +
                    $"'{candidate.vesselName}' pid={candidate.persistentId} - a new launch is not a " +
                    "vessel switch; the launch starts its own tree");
                // The refused tree is discarded (idle resumed committed copy) or committed
                // (every other tree) here, with or without a revertible pre-transition, so
                // nothing is left pending for a later load or stash to reinstall or overwrite.
                if (!IsFreshRolloutRefusedTreeResolved(DisposeFreshRolloutRefusedPendingTree(
                        "vessel-switch-restore", stashedTree, candidate.persistentId)))
                {
                    // A guarded tree stays pending. Make the refusal durable: a
                    // LimboVesselSwitch tree is written as an isActive node and a later load
                    // would route it back here, where the fresh-rollout pid is no longer
                    // captured, and reinstall it on the launched craft. Undo the stash's
                    // pre-transition so the tree is a plain Limbo stash that the guid-gated
                    // quickload restore owns.
                    if (TryRevertPreTransitionForVesselSwitch(
                            PendingTreeSlot.PendingTree, out string restoredRecId))
                    {
                        PendingTreeSlot.ConvertPendingVesselSwitchStashToLimbo(
                            "fresh-rollout refusal restored activeRecId=" + restoredRecId);
                    }
                    else
                    {
                        ParsekLog.Warn("Flight",
                            "RestoreActiveTreeFromPendingForVesselSwitch: refused tree has no " +
                            "revertible pre-transition (outsider-chain stash or reloaded tree) - it " +
                            "stays LimboVesselSwitch and is protected for this scene only " +
                            "(todo FRESH-LAUNCH-REFUSED-KEPT-TREE-PENDING)");
                    }
                }
                yield break;
            }

            // Pop the tree (non-destructive — preserves sidecar files) and install
            // as the live active tree.
            activeTree = PendingTreeSlot.PopPendingTree();
            if (activeTree == null)
            {
                ParsekLog.Warn("Flight",
                    "RestoreActiveTreeFromPendingForVesselSwitch: PopPendingTree returned null after " +
                    "we verified the tree exists");
                yield break;
            }
            // PopPendingTree just emptied the main pending slot; a saved pending
            // tree from the same save can now move back unless another path filled it.
            PendingTreeSlot.PromoteSavedPendingTreeAfterActiveRestore(
                "RestoreActiveTreeFromPendingForVesselSwitch");

            // Re-attach the BackgroundRecorder for the tree (the previous instance was
            // shut down in FinalizeTreeOnSceneChange before the scene reload).
            EnsureBackgroundRecorderAttached("RestoreActiveTreeFromPendingForVesselSwitch");

            // Inspect the new active vessel for promotion.
            var newActive = FlightGlobals.ActiveVessel;
            if (newActive == null)
            {
                // Wait budget exhausted with no ActiveVessel: log Warn so playtest
                // logs surface the missed arming opportunity. The tree is reinstalled but
                // any potential round-trip watcher cannot be tested at this point.
                // Subsequent OnVesselSwitchComplete events from the live game
                // will still drive the in-session watcher path.
                ParsekLog.Warn("Flight",
                    $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled, " +
                    $"but no active vessel resolved within {WaitBudgetSeconds:F0}s wait window " +
                    $"(timedOut={waitTimedOut}) — outsider state, recorder stays null. Round-trip " +
                    "arming (if any) will be missed; subsequent OnVesselSwitchComplete will recover.");
            }
            else
            {
                // PID-stability assumption: TS-mediated FLIGHT→FLIGHT vessel switches
                // do NOT regenerate persistentId. The protoVessel persists through the
                // scene reload with the same PID it had at stash time, so we can look
                // up BackgroundMap directly without the PID-remap dance the quickload
                // path needs (RestoreActiveTreeFromPending ~5520-5535). Verified in
                // logs/2026-04-09_f5f9_verify/KSP.log: pid 2708531065 survives the
                // reload unchanged. If a future KSP version regenerates PIDs on TS
                // switch, the round-trip arming will silently fail and the player
                // will land in the outsider branch — visible as a missing
                // post-switch armed log line.
                uint newPid = newActive.persistentId;
                if (activeTree.BackgroundMap.TryGetValue(newPid, out string bgRecId))
                {
                    // Round-trip case: the player returned to a vessel that this tree
                    // is already tracking, but post-switch auto-record now arms and waits
                    // for the first meaningful modification instead of promoting on focus
                    // change alone.
                    PostSwitchAutoRecorder.ArmPostSwitchAutoRecord(
                        newActive,
                        "restore completed on tracked tree member while idle",
                        trackedInActiveTree: true,
                        freshStartParentRecordingId: null);
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: armed tracked vessel '{newActive.vesselName}' " +
                        $"(pid={newPid}) from BackgroundMap — waiting for first modification after TS / scene-reload switch");
                }
                else
                {
                    // Outsider case: the new active vessel has no recording context in
                    // this tree. recorder stays null. Future switches back to a tree
                    // member will arm the tracked path and later promote on trigger.
                    ParsekLog.Info("Flight",
                        $"RestoreActiveTreeFromPendingForVesselSwitch: tree '{activeTree.TreeName}' reinstalled " +
                        $"as outsider for active vessel '{newActive.vesselName}' (pid={newPid}, not in " +
                        $"BackgroundMap) — recorder stays null until a post-switch trigger starts fresh or a later tracked switch promotes on trigger");
                    PostSwitchAutoRecorder.ArmPostSwitchAutoRecord(
                        newActive,
                        "restore completed on outsider while idle",
                        trackedInActiveTree: false,
                        freshStartParentRecordingId: null);
                }
            }

            RecorderStateLog.RecState("RestoreSwitch:after-install", CaptureRecorderState());
            } // try
            finally
            {
                restoringActiveTree = false;
            }

            // M1 (PR #876 final review): dispatch any deferred consume now that
            // the restored tree is live. DispatchConsumeIntentIfArmed at the
            // top of OnFlightReady skips when ScheduleActiveTreeRestoreOnFlightReady
            // is non-None to avoid racing the restore. Re-invoking here picks
            // up an intent that survived the F5 -> reload round-trip and
            // attaches the new switch segment to the just-restored tree.
            DispatchConsumeIntentIfArmed();
        }

        /// <summary>
        /// Flight→Flight stash path (Bug C fix): stashes the active tree into the
        /// pending-Limbo slot WITHOUT finalizing terminal state. ParsekScenario.OnLoad
        /// decides, based on revert vs quickload detection, whether to finalize-and-commit
        /// (real revert) or restore-and-resume (quickload / vessel switch).
        ///
        /// <para>Flushes recorder buffers into the tree so no trajectory data is lost.
        /// Closes background orbit segments. Aborts any pending split (continuation
        /// window state cannot survive a scene reload cleanly — edge case #4 in the
        /// design doc).</para>
        /// </summary>
        internal void StashActiveTreeAsPendingLimbo(double commitUT)
        {
            if (activeTree == null) return;

            ParsekLog.Info("Flight",
                $"StashActiveTreeAsPendingLimbo: stashing tree '{activeTree.TreeName}' at UT={commitUT:F1} " +
                $"(activeRecId={activeTree.ActiveRecordingId ?? "<null>"})");
            RecorderStateLog.RecState("StashTreeLimbo:entry", CaptureRecorderState());

            // Flush active recorder's buffered points/events into the active tree's
            // current recording, without setting terminal state. FinalizeRecorderForLimbo
            // is a minimal version of FinalizeTreeRecordings that stops at flushing.
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Copy rewind save to the root recording so the R button works after revert.
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "StashTreeLimbo");
            }

            // Close background recorder orbit segments and flush their data into tree
            // recordings. Does NOT set terminal state.
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // Abort any pending split (undock / EVA / joint break continuation window).
            // The continuation state cannot survive a scene reload cleanly — if the
            // split was real, the debris vessel exists in the saved scene and will be
            // rediscovered via normal OnVesselCreate events on resume.
            if (pendingSplitRecorder != null)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeAsPendingLimbo: discarding pendingSplitRecorder " +
                    $"(recId={pendingSplitRecorder.CaptureAtStop?.RecordingId ?? "<null>"}) — " +
                    "continuation window cannot survive scene reload");
                pendingSplitRecorder = null;
                pendingSplitInProgress = false;
            }

            // #268: Belt-and-braces snapshot capture while vessels are still alive.
            // FinalizePendingLimboTreeForRevert relies on FindVesselByPid at OnLoad time
            // to re-snapshot via the #289 block. If vessels are unloaded before that
            // (edge case: late OnLoad timing, non-FLIGHT destination), this pre-capture
            // ensures the merge dialog can still offer respawn for stable-terminal leaves.
            // Only fills null snapshots — does NOT overwrite existing ones.
            //
            // Bug #271: also capture for the active recording even if non-leaf. In
            // always-tree mode with breakup-continuous design, the active recording
            // may have ChildBranchPointId set but still needs a snapshot for spawn-at-end.
            int snapshotsCaptured = 0;
            string activeRecId = activeTree.ActiveRecordingId;
            foreach (var kvp in activeTree.Recordings)
            {
                var rec = kvp.Value;
                bool isActiveRec = rec.RecordingId == activeRecId;
                if (rec.ChildBranchPointId != null && !isActiveRec
                    && !RecordingTree.ChildBranchLeavesVesselLineOpen(
                        rec, activeTree.Recordings, activeTree.BranchPoints))
                    continue;
                if (rec.VesselPersistentId == 0) continue;

                Vessel v = FlightRecorder.FindVesselByPid(rec.VesselPersistentId);
                if (v != null && rec.VesselSnapshot == null)
                {
                    ConfigNode freshSnapshot = FlightRecorder.TryBackupSnapshot(v);
                    if (freshSnapshot != null)
                    {
                        rec.VesselSnapshot = freshSnapshot;
                        if (rec.GhostVisualSnapshot == null)
                            rec.GhostVisualSnapshot = freshSnapshot.CreateCopy();
                        snapshotsCaptured++;
                    }
                }
            }
            if (snapshotsCaptured > 0)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeAsPendingLimbo: captured {snapshotsCaptured} snapshot(s) " +
                    $"for null-snapshot leaves");
            }

            // Stash as pending-Limbo. NO FinalizeTreeRecordings — terminal state deferred
            // to OnLoad's dispatch once it knows whether this is a revert or a quickload.
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.Limbo);

            ParsekLog.Info("Flight",
                $"StashActiveTreeAsPendingLimbo: stashed tree '{activeTree.TreeName}' as Limbo " +
                $"({activeTree.Recordings.Count} recording(s)) — OnLoad will dispatch");
            RecorderStateLog.RecState("StashTreeLimbo:post", CaptureRecorderState());
        }

        /// <summary>
        /// Bug #266: returns true if the active tree can safely be pre-transitioned
        /// for a vessel-switch stash. Bails out on the same conditions that the
        /// in-session <see cref="OnVesselSwitchComplete"/> path bails on
        /// (<c>pendingTreeDockMerge</c>, pending split / chain-to-vessel windows),
        /// because those states have continuation logic that runs in <c>Update()</c>
        /// after the switch completes — and <c>Update()</c> never runs across a
        /// scene reload. Falling back to the legacy Limbo stash on these is the
        /// safer option (the safety net in <c>OnLoad</c> will finalize, matching
        /// pre-#266 behavior for these edge cases).
        /// </summary>
        internal bool CanPreTransitionForVesselSwitch()
        {
            if (activeTree == null) return false;
            if (pendingTreeDockMerge) return false;
            if (pendingSplitRecorder != null || pendingSplitInProgress) return false;
            if (recorder != null && recorder.ChainToVesselPending) return false;
            return true;
        }

        /// <summary>
        /// Pure decision for <see cref="DeferredJointBreakCheck"/>: true when a split
        /// recorder must resume on the vessel it was recording rather than on the active
        /// vessel, because focus moved off the recorded vessel (which still exists) while
        /// the recorder was stopped at the split. Resuming on the active vessel would record
        /// a different vessel into this recording (todo
        /// VN8-FOCUS-SWITCH-SPLIT-PARENT-LEG-RECORDS-NEW-VESSEL).
        /// </summary>
        internal static bool ShouldResumeSplitOnRecordedVessel(
            uint recordedVesselPid,
            bool recordedVesselAlive,
            uint activeVesselPid)
        {
            return recordedVesselPid != 0u
                && recordedVesselAlive
                && activeVesselPid != 0u
                && activeVesselPid != recordedVesselPid;
        }

        /// <summary>
        /// Pure: the pid of the vessel that survives a split as the parent recording's
        /// vessel. That is the vessel the active recorder records, which is not the active
        /// vessel when focus moved to a split child; the active vessel only stands in when
        /// no recorder is bound.
        /// </summary>
        internal static uint ResolveBreakupParentVesselPid(uint recorderVesselPid, uint activeVesselPid)
        {
            return recorderVesselPid != 0u ? recorderVesselPid : activeVesselPid;
        }

        /// <summary>
        /// Pure decision for the OnFlightReady fallback that opens the tree
        /// merge dialog. Returns <c>true</c> when the dialog should be shown:
        /// a pending tree exists, no restore coroutine owns it (#293), and
        /// no Re-Fly attempt owns it (in-place continuation marker or
        /// pending invoke). The Re-Fly guard prevents the fallback from
        /// firing immediately after an initial Re-Fly invocation or a
        /// Retry-from-Rewind-Point — those cases have a fresh fork attached
        /// to the pending tree, and the merge decision belongs to the
        /// scene-exit path once the attempt actually finishes.
        ///
        /// <para>
        /// Placeholder-mode Re-Fly markers (PID changed or chain orphaned
        /// at <c>AtomicMarkerWrite</c> time) deliberately do NOT skip — the
        /// recorder-restore carve-out cannot bind a recorder for that
        /// marker shape, so the merge-dialog fallback is the player's only
        /// recovery path. The caller passes
        /// <c>ParsekScenario.IsReFlyInPlaceContinuationActive() || RewindInvokeContext.Pending</c>
        /// to <paramref name="reFlyInPlaceContinuationActive"/>; the
        /// parameter name reflects the dominant case (the brief pending
        /// invoke window is a corner included for flicker safety).
        /// </para>
        /// </summary>
        internal static bool ShouldShowOnFlightReadyMergeDialog(
            bool hasPendingTree,
            bool restoringActiveTree,
            bool reFlyInPlaceContinuationActive)
        {
            return hasPendingTree
                && !restoringActiveTree
                && !reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Pure decision: should OnFlightReady upgrade
        /// <paramref name="restoreMode"/> from None to Quickload because a
        /// Re-Fly retry left a freshly-attached fork on a Finalized pending
        /// tree that no other code path will bind a recorder to.
        ///
        /// <para>
        /// The full Re-Fly retry path: the previous attempt was finalized
        /// post-destruction (via <c>ShowPostDestructionTreeMergeDialog</c>
        /// or <c>CommitTreeSceneExit</c>) before the player clicked Retry
        /// from Rewind Point. <c>TryRestoreActiveTreeNode</c> sees the
        /// in-memory Finalized tree and skips the .sfs replacement (#290d),
        /// so no Quickload schedule lands on <see cref="ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady"/>.
        /// <c>AtomicMarkerWrite</c> still attaches the new fork to that
        /// tree and sets <see cref="ParsekScenario.ActiveReFlySessionMarker"/>,
        /// but without a Quickload schedule the live vessel has no recorder
        /// bound to the new fork — the OnFlightReady merge-dialog skip then
        /// hides the symptom (no dialog) without resolving the underlying
        /// state.
        /// </para>
        ///
        /// <para>
        /// The Re-Fly check is the stricter
        /// <see cref="ParsekScenario.IsReFlyInPlaceContinuationActive"/>
        /// (marker set AND <c>InPlaceContinuation == true</c>), not the
        /// broader <see cref="ParsekScenario.IsReFlySessionActiveForQuickloadDiscard"/>.
        /// In placeholder mode (PID changed or chain orphaned, see
        /// <c>RewindInvoker.AtomicMarkerWrite</c> line 1096-1099), the
        /// marker swap in the restore coroutine returns
        /// <c>placeholder-pattern</c> with no swap; the wait loop then
        /// targets the pre-rewind PID, times out at 3 s, and yield-breaks.
        /// Firing the carve-out in that mode leaves the player stuck with
        /// no recorder AND no dialog. Refusing to upgrade keeps the original
        /// merge-dialog fallback as the recovery path.
        /// </para>
        /// </summary>
        internal static bool ShouldUpgradeRestoreModeForReFlyRetry(
            ParsekScenario.ActiveTreeRestoreMode restoreMode,
            bool hasPendingTree,
            bool pendingTreeIsFinalized,
            bool reFlyInPlaceContinuationActive)
        {
            return restoreMode == ParsekScenario.ActiveTreeRestoreMode.None
                && hasPendingTree
                && pendingTreeIsFinalized
                && reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Pure decision: should <see cref="RestoreActiveTreeFromPending"/>
        /// accept a Finalized pending tree (its usual gate is Limbo only)
        /// because the active Re-Fly session needs the recorder bound to
        /// the freshly-attached fork. Paired with
        /// <see cref="ShouldUpgradeRestoreModeForReFlyRetry"/> in the
        /// OnFlightReady dispatch — both gate on
        /// <see cref="ParsekScenario.IsReFlyInPlaceContinuationActive"/>
        /// (marker + InPlaceContinuation flag) rather than the broader
        /// invoke-pending helper, because the coroutine's marker swap
        /// (<see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>)
        /// is the load-bearing path that redirects the wait target to the
        /// new fork's vessel and PID. Placeholder-mode markers cannot
        /// satisfy that swap.
        /// </summary>
        internal static bool ShouldAcceptFinalizedPendingTreeForReFlyRetry(
            bool hasPendingTree,
            bool pendingTreeIsFinalized,
            bool reFlyInPlaceContinuationActive)
        {
            return hasPendingTree
                && pendingTreeIsFinalized
                && reFlyInPlaceContinuationActive;
        }

        /// <summary>
        /// Asks the committed-spawned restore (the 1 Hz Update retry and every other
        /// <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/> entry point) to
        /// leave the CURRENT active vessel idle. Called only by the M-A2
        /// <c>DiscardTree</c> test-command verb: without it, discarding the tree a
        /// recorded-fixture boot restored for its spawned vessel is undone within a second
        /// (the committed original is still in committed storage, so the retry re-clones
        /// it), and a lane's later <c>StartRecording</c> is refused. Player discard paths
        /// never call this: each is followed by a vessel switch or a scene change, and a
        /// live committed vessel re-adopting its tree is the intended product rule.
        /// </summary>
        internal void SuppressCommittedSpawnedRestoreForActiveVessel(string reason)
        {
            Vessel activeVessel = FlightGlobals.ActiveVessel;
            BeginCommittedSpawnedRestoreSuppression(
                ref committedSpawnedRestoreSuppressedPid,
                activeVessel != null ? activeVessel.persistentId : 0u,
                activeVessel != null ? activeVessel.vesselName : null,
                hasActiveTree: activeTree != null,
                hasRecorder: recorder != null,
                reason: reason);
        }

        /// <summary>
        /// Pure: true when the committed-spawned restore must skip
        /// <paramref name="activeVesselPid"/> because a DiscardTree suppression names it.
        /// A zero pid on either side never matches.
        /// </summary>
        internal static bool IsCommittedSpawnedRestoreSuppressedForVessel(
            uint suppressedPid, uint activeVesselPid)
        {
            return suppressedPid != 0
                && activeVesselPid != 0
                && suppressedPid == activeVesselPid;
        }

        /// <summary>
        /// Pure: why an armed suppression ends, or null to keep it. It ends once anything
        /// owns a live tree or recorder (a StartRecording, a restore coroutine, an
        /// adoption), because the idle request is then over, and once the active vessel is
        /// a DIFFERENT vessel (an EVA, a switch), so a later legitimate restore of another
        /// vessel, or of this one after switching back, still runs. A zero active pid is a
        /// transient mid-switch read and keeps the suppression.
        /// </summary>
        internal static string ResolveCommittedSpawnedRestoreSuppressionClearReason(
            uint suppressedPid, uint activeVesselPid, bool hasActiveTree, bool hasRecorder)
        {
            if (suppressedPid == 0) return null;
            if (hasActiveTree || hasRecorder) return "live-tree-or-recorder";
            if (activeVesselPid != 0 && activeVesselPid != suppressedPid)
                return "active-vessel-changed";
            return null;
        }

        /// <summary>
        /// Arms the suppression for <paramref name="activeVesselPid"/> and logs it. Refuses
        /// (and logs why) when there is no active vessel or something still owns a live
        /// tree or recorder, since the per-frame clear would drop it on the next frame.
        /// Returns whether it armed.
        /// </summary>
        internal static bool BeginCommittedSpawnedRestoreSuppression(
            ref uint suppressedPid,
            uint activeVesselPid,
            string activeVesselName,
            bool hasActiveTree,
            bool hasRecorder,
            string reason)
        {
            if (activeVesselPid == 0)
            {
                ParsekLog.Info("Flight",
                    $"CommittedSpawnedRestoreSuppression: not armed reason={reason} " +
                    "(no active vessel)");
                return false;
            }
            if (hasActiveTree || hasRecorder)
            {
                ParsekLog.Info("Flight",
                    $"CommittedSpawnedRestoreSuppression: not armed reason={reason} " +
                    $"pid={activeVesselPid} (live tree={hasActiveTree} recorder={hasRecorder})");
                return false;
            }
            uint previous = suppressedPid;
            suppressedPid = activeVesselPid;
            ParsekLog.Info("Flight",
                $"CommittedSpawnedRestoreSuppression: armed pid={activeVesselPid} " +
                $"vessel='{activeVesselName ?? "<null>"}' reason={reason} " +
                $"previousPid={previous} - committed-tree restore skips this vessel until a " +
                "live tree or recorder exists, the active vessel changes, or the scene ends");
            return true;
        }

        /// <summary>
        /// Per-frame clear for the suppression (no-op when none is armed). Logs once when
        /// it clears. Returns the clear reason, or null when the suppression stays.
        /// </summary>
        internal static string TickCommittedSpawnedRestoreSuppression(
            ref uint suppressedPid,
            uint activeVesselPid,
            bool hasActiveTree,
            bool hasRecorder)
        {
            string clearReason = ResolveCommittedSpawnedRestoreSuppressionClearReason(
                suppressedPid, activeVesselPid, hasActiveTree, hasRecorder);
            if (clearReason == null) return null;
            ParsekLog.Info("Flight",
                $"CommittedSpawnedRestoreSuppression: cleared pid={suppressedPid} " +
                $"reason={clearReason} activePid={activeVesselPid}");
            suppressedPid = 0;
            return clearReason;
        }

        internal static bool ShouldAttemptCommittedSpawnedRestoreInUpdate(
            bool hasActiveTree,
            bool hasRecorder,
            bool isRestoringActiveTree,
            bool hasPendingTree,
            ParsekScenario.ActiveTreeRestoreMode restoreMode,
            float currentUnscaledTime,
            float nextRetryAt)
        {
            if (hasActiveTree || hasRecorder) return false;
            if (isRestoringActiveTree) return false;
            if (hasPendingTree) return false;
            if (restoreMode != ParsekScenario.ActiveTreeRestoreMode.None) return false;
            return currentUnscaledTime >= nextRetryAt;
        }

        // KSP's craft-file-derived persistentId is deterministic enough that
        // re-launching the same .craft tends to recycle the previous mission's pid,
        // and Re-Fly merges preserve the prior SpawnedVesselPersistentId
        // (TreeCommitService.PreserveLiveRuntimeFieldsOnReplace). Without this gate,
        // a fresh VAB/SPH launch would silently attach to the prior committed tree
        // and merge two distinct missions under one auto-generated group.
        //
        // The guard has two parts:
        //   1. IsFreshLaunchStartupBehaviour gates the Start-time capture of
        //      freshRolloutVesselPid on FlightDriver.StartupBehaviour. KSP sets
        //      StartupBehaviour by the editor's Launch handler / save-loader /
        //      revert path before the FLIGHT scene transitions in, and the value
        //      is stable for the entire scene (no missionTime expiry, no event
        //      race). NEW_FROM_FILE and NEW_FROM_CRAFT_NODE are both fresh
        //      rollouts; RESUME_SAVED_FILE and RESUME_SAVED_CACHE leave the
        //      capture pid at 0 so this guard does nothing.
        //   2. ShouldSkipCommittedTreeRestoreForFreshLaunch is a pure pid-match
        //      against the captured rollout pid. This identity component is what
        //      lets a mid-scene switch to another already-spawned committed
        //      vessel (with a different pid) still resume its recording.
        // Verified against the repro at logs/2026-05-13_1850_kerbal-x-merge-bug:
        // KSP.log line 53466-53467 shows the VAB craft loader running ("Loading
        // ship from file: ...Auto-Saved Ship.craft"), which is FlightDriver's
        // NEW_FROM_FILE branch (Assembly-CSharp/FlightDriver.cs:334-345).
        internal static bool IsFreshLaunchStartupBehaviour(
            FlightDriver.StartupBehaviours startupBehaviour)
        {
            return startupBehaviour == FlightDriver.StartupBehaviours.NEW_FROM_FILE
                || startupBehaviour == FlightDriver.StartupBehaviours.NEW_FROM_CRAFT_NODE;
        }

        internal static bool ShouldSkipCommittedTreeRestoreForFreshLaunch(
            uint activeVesselPid, uint freshRolloutVesselPid)
        {
            return activeVesselPid != 0 && activeVesselPid == freshRolloutVesselPid;
        }

        // Bug (post-#876 playtest 2026-05-17): the kerbal-x-grouping-bug repro.
        // The matcher historically only recognised Parsek-spawned recordings
        // (VesselSpawned=true with SpawnedVesselPersistentId equal to the live
        // PID). Fresh-launched vessels (NEW_FROM_FILE etc.) commit recordings
        // with VesselSpawned=false and SpawnedVesselPersistentId=0 but their
        // VesselPersistentId equals the live PID of the vessel currently
        // sitting in the save. When the player Map-Switch-To'd a far Kerbal X
        // (committed mission, never Parsek-spawned), TryFindCommittedTree-
        // MatchingVessel's broader gate routed into the committed-clone
        // branch but this helper rejected the recording, so the consume path
        // fell through to StartStandaloneContinuationSegment and authored a
        // brand-new "Kerbal X" tree disjoint from the original mission tree.
        // The new tree got its own auto-group, so the segment appeared
        // outside the mission group in the UI.
        //
        // The fix: also match recordings whose VesselPersistentId equals the
        // live PID when the recording was not Parsek-spawned. ResolveLive-
        // TreeRecordingPidForRestore already falls back to VesselPersistentId
        // when SpawnedVesselPersistentId is zero, and PrepareCommittedTree-
        // RestoreForSpawnedVessel uses that helper, so the downstream
        // restore path resumes the live recording correctly. The clear-
        // prior-spawn-flags block in TryTakeCommittedTreeForSpawnedVessel-
        // Restore is gated on VesselSpawned || SpawnedVesselPersistentId!=0,
        // so the direct-PID match leaves the recording's identity fields
        // untouched.
        internal static bool TryFindCommittedTreeForSpawnedVessel(
            IReadOnlyList<RecordingTree> committedTrees,
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId,
            string activeVesselGuid = null,
            double liveUT = double.NaN)
        {
            tree = null;
            recordingId = null;
            if (committedTrees == null || activeVesselPid == 0)
                return false;

            int refusedOtherLaunchStamps = 0;
            int refusedAheadOfClock = 0;
            for (int t = committedTrees.Count - 1; t >= 0; t--)
            {
                RecordingTree candidate = committedTrees[t];
                if (candidate?.Recordings == null || candidate.Recordings.Count == 0)
                    continue;

                Recording bestMatch = null;
                foreach (Recording rec in candidate.Recordings.Values)
                {
                    if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                        continue;

                    // A genuine Parsek spawn pid is KSP-unique, but an adoption stamp is the
                    // craft-baked pid, so the spawn match goes through the launch-identity gate.
                    bool spawnedMatch = IsCommittedSpawnStampMatch(rec, activeVesselPid, activeVesselGuid);
                    if (!spawnedMatch && IsOtherLaunchSpawnStamp(rec, activeVesselPid, activeVesselGuid))
                        refusedOtherLaunchStamps++;
                    // #976-class: the direct VesselPersistentId match is the craft-baked pid, which
                    // a relaunch of the same craft reuses (notably on RESUME/quickload where the
                    // editor-only fresh-rollout guard above is inactive). Require the same launch
                    // (guid); a null/unknown guid falls back to today's pid-only behavior.
                    bool directMatch = !rec.VesselSpawned
                        && rec.SpawnedVesselPersistentId == 0
                        && rec.VesselPersistentId != 0
                        && rec.VesselPersistentId == activeVesselPid
                        && VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, activeVesselPid, activeVesselGuid);
                    if (!spawnedMatch && !directMatch)
                        continue;

                    if (!IsCommittedSpawnedRecordingRestorable(candidate, rec))
                        continue;

                    if (IsCommittedRecordingAheadOfLiveClock(rec, liveUT))
                    {
                        refusedAheadOfClock++;
                        continue;
                    }

                    if (bestMatch == null
                        || IsPreferredCommittedSpawnedRestoreCandidate(candidate, rec, bestMatch))
                    {
                        bestMatch = rec;
                    }
                }

                if (bestMatch != null)
                {
                    tree = candidate;
                    recordingId = bestMatch.RecordingId;
                    LogRefusedOtherLaunchSpawnStamps(
                        "TryFindCommittedTreeForSpawnedVessel", activeVesselPid, activeVesselGuid,
                        refusedOtherLaunchStamps);
                    LogRefusedAheadOfClockRestores(activeVesselPid, liveUT, refusedAheadOfClock);
                    return true;
                }
            }

            LogRefusedOtherLaunchSpawnStamps(
                "TryFindCommittedTreeForSpawnedVessel", activeVesselPid, activeVesselGuid,
                refusedOtherLaunchStamps);
            LogRefusedAheadOfClockRestores(activeVesselPid, liveUT, refusedAheadOfClock);
            return false;
        }

        /// <summary>
        /// Pure: true when <paramref name="rec"/> already holds recorded data later than
        /// <paramref name="liveUT"/>, so the live vessel has not yet reached the end of the
        /// recording it would continue. Resuming there writes samples behind the recording's
        /// own end and double-covers it (VN-5: Revert to Launch put the pad craft back at
        /// UT 25.64 and it resumed its committed pad recording, which ran to 26.88, at 25.68).
        /// Measured on recorded data only (points, track sections, non-predicted orbit
        /// segments): a predicted tail is extrapolation, not something the vessel did. A NaN
        /// clock (headless callers) never refuses.
        /// </summary>
        internal static bool IsCommittedRecordingAheadOfLiveClock(Recording rec, double liveUT)
        {
            if (rec == null || double.IsNaN(liveUT) || double.IsInfinity(liveUT))
                return false;
            if (!TryGetLatestRecordedUT(rec, out double latestUT))
                return false;
            return latestUT > liveUT + CommittedRestoreAheadOfClockToleranceSeconds;
        }

        /// <summary>
        /// Pure: the latest UT a recording holds recorded (not predicted) data for.
        /// </summary>
        internal static bool TryGetLatestRecordedUT(Recording rec, out double latestUT)
        {
            latestUT = double.NegativeInfinity;
            bool found = false;
            if (rec.Points != null && rec.Points.Count > 0)
            {
                latestUT = rec.Points[rec.Points.Count - 1].ut;
                found = true;
            }
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    // A section with no samples of its own (flat-fallback) is metadata: a
                    // frameless tail trim leaves its endUT at the trim cut, past the last
                    // flat point, and the flat points already carry its data.
                    if (!TrackSectionHoldsRecordedSamples(rec.TrackSections[i]))
                        continue;
                    double end = rec.TrackSections[i].endUT;
                    if (double.IsNaN(end) || double.IsInfinity(end))
                        continue;
                    if (!found || end > latestUT)
                        latestUT = end;
                    found = true;
                }
            }
            if (rec.OrbitSegments != null)
            {
                for (int i = 0; i < rec.OrbitSegments.Count; i++)
                {
                    OrbitSegment seg = rec.OrbitSegments[i];
                    if (seg.isPredicted || double.IsNaN(seg.endUT) || double.IsInfinity(seg.endUT))
                        continue;
                    if (!found || seg.endUT > latestUT)
                        latestUT = seg.endUT;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>
        /// Pure: true when a track section carries recorded samples of its own (frames,
        /// body-fixed frames or a non-predicted orbit checkpoint).
        /// </summary>
        internal static bool TrackSectionHoldsRecordedSamples(TrackSection section)
        {
            if (section.frames != null && section.frames.Count > 0)
                return true;
            if (section.bodyFixedFrames != null && section.bodyFixedFrames.Count > 0)
                return true;
            if (section.checkpoints != null)
            {
                for (int i = 0; i < section.checkpoints.Count; i++)
                {
                    if (!section.checkpoints[i].isPredicted)
                        return true;
                }
            }
            return false;
        }

        private static void LogRefusedAheadOfClockRestores(uint activeVesselPid, double liveUT, int refused)
        {
            if (refused <= 0)
                return;
            // Re-reached by the 1 Hz Update retry until the suppression the caller arms
            // takes over: rate-limited per pid.
            ParsekLog.VerboseRateLimited("Flight",
                "committed-restore-ahead-of-clock-" + activeVesselPid.ToString(CultureInfo.InvariantCulture),
                $"TryFindCommittedTreeForSpawnedVessel: refused {refused.ToString(CultureInfo.InvariantCulture)} " +
                $"committed recording(s) for pid={activeVesselPid.ToString(CultureInfo.InvariantCulture)} " +
                $"whose recorded data runs past the live clock " +
                $"ut={liveUT.ToString("F2", CultureInfo.InvariantCulture)} (the vessel is behind its own " +
                "recorded future; resuming would record over it)");
        }

        internal static bool TryTakeCommittedTreeForSpawnedVesselRestore(
            uint activeVesselPid,
            out RecordingTree tree,
            out string recordingId,
            out CommittedSpawnedVesselRestoreAction action,
            string activeVesselGuid = null,
            double liveUT = double.NaN)
        {
            tree = null;
            recordingId = null;
            action = CommittedSpawnedVesselRestoreAction.None;

            if (!TryFindCommittedTreeForSpawnedVessel(
                    RecordingStore.CommittedTrees,
                    activeVesselPid,
                    out RecordingTree committedTree,
                    out string targetRecordingId,
                    activeVesselGuid,
                    liveUT))
            {
                return false;
            }

            RecordingTree liveTree = RecordingTree.DeepClone(committedTree);
            if (liveTree == null)
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"recording '{targetRecordingId}' for pid={activeVesselPid}, but could not clone the tree");
                return false;
            }

            CommittedSpawnedVesselRestoreAction preparedAction =
                PrepareCommittedTreeRestoreForSpawnedVessel(
                    liveTree,
                    targetRecordingId,
                    activeVesselPid);
            if (preparedAction == CommittedSpawnedVesselRestoreAction.None)
            {
                ParsekLog.Warn("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: matched tree '{committedTree.TreeName}' " +
                    $"recording '{targetRecordingId}' for pid={activeVesselPid}, but could not prepare a restore action");
                return false;
            }

            CommittedTreeRestoreAttempt.ArmCommittedTreeRestoreAttempt(
                committedTree,
                "TryTakeCommittedTreeForSpawnedVesselRestore copy-on-write");
            // A save made inside an attempt that resumed in place holds no active tree, so its
            // held-back events wait for this arm (design-load-restores-the-save.md 4.3).
            ParsekScenario.ResolveHeldSaveAttemptEventsOnArm(committedTree.Id);

            // The recording we're about to resume carries VesselSpawned=true and a
            // non-zero SpawnedVesselPersistentId from its prior commit (set by the KSC
            // adoption path). While the tree was committed, those flags correctly meant
            // "a real vessel already represents this recording". Now the tree is live
            // again and will be re-committed at next scene exit; the merge dialog's
            // CanPersistVessel → ShouldSpawnAtRecordingEnd chain reads VesselSpawned
            // as "already spawned, don't persist again" and defaults the leaf to
            // ghost-only, which nulls the VesselSnapshot at commit time. Subsequent
            // KSC spawns then skip with "no vessel snapshot". Clearing the flags here
            // lets the next commit re-evaluate spawn eligibility from scratch. Before
            // clearing the spawned PID, intentionally promote the matched live vessel PID
            // into the recording's normal VesselPersistentId. Downstream tree-finalization,
            // snapshot refresh, and rollout-adoption paths key off VesselPersistentId and
            // must follow the vessel the player actually re-entered here, not the older
            // source PID that originally produced the committed recording.
            // Vessel names (design 6.2): the live vessel (a Parsek spawn, fresh guid) continues
            // the resumed recording's identity. Linked BEFORE the spawn pid is cleared below:
            // after that nothing but this link joins the two guids. Only through the
            // genuine-spawn branch (the backfill's rule): an adoption-stamp match is a pid the
            // craft bakes, never proof enough for a permanent link.
            if (liveTree.Recordings != null
                && liveTree.Recordings.TryGetValue(targetRecordingId, out Recording linkRec)
                && linkRec != null
                && VesselLaunchIdentity.IsGenuineSpawnPidMatch(linkRec, activeVesselPid))
            {
                VesselNameStore.AddLink(activeVesselGuid, linkRec.RecordedVesselGuid,
                    VesselIdentityLinkKind.Resume,
                    "resume rec=" + targetRecordingId + " pid=" + activeVesselPid.ToString(CultureInfo.InvariantCulture));
            }

            if (liveTree.Recordings != null
                && liveTree.Recordings.TryGetValue(targetRecordingId, out Recording resumedRec)
                && resumedRec != null
                && (resumedRec.VesselSpawned || resumedRec.SpawnedVesselPersistentId != 0))
            {
                uint priorSpawnedPid = resumedRec.SpawnedVesselPersistentId;
                uint priorVesselPid = resumedRec.VesselPersistentId;
                resumedRec.VesselPersistentId = activeVesselPid;
                resumedRec.VesselSpawned = false;
                resumedRec.SpawnedVesselPersistentId = 0;
                ParsekLog.Info("Flight",
                    $"TryTakeCommittedTreeForSpawnedVesselRestore: cleared prior spawn flags on " +
                    $"resumed recording '{targetRecordingId}' (wasSpawnedPid={priorSpawnedPid}, " +
                    $"vesselPid={priorVesselPid}->{activeVesselPid}) so the next commit re-evaluates " +
                    $"persist eligibility instead of defaulting to ghost-only");
            }

            tree = liveTree;
            recordingId = targetRecordingId;
            action = preparedAction;
            return true;
        }

        internal static CommittedSpawnedVesselRestoreAction PrepareCommittedTreeRestoreForSpawnedVessel(
            RecordingTree tree,
            string targetRecordingId,
            uint activeVesselPid)
        {
            if (tree == null || string.IsNullOrEmpty(targetRecordingId) || activeVesselPid == 0)
                return CommittedSpawnedVesselRestoreAction.None;
            if (!tree.Recordings.TryGetValue(targetRecordingId, out Recording targetRec) || targetRec == null)
                return CommittedSpawnedVesselRestoreAction.None;
            if (ResolveLiveTreeRecordingPidForRestore(targetRec) != activeVesselPid)
                return CommittedSpawnedVesselRestoreAction.None;
            if (!IsCommittedSpawnedRecordingRestorable(tree, targetRec))
                return CommittedSpawnedVesselRestoreAction.None;

            if (string.Equals(tree.ActiveRecordingId, targetRecordingId, StringComparison.Ordinal))
            {
                RemoveRecordingIdFromBackgroundMap(tree, targetRecordingId);
                return CommittedSpawnedVesselRestoreAction.ResumeActiveRecording;
            }

            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && tree.Recordings.TryGetValue(tree.ActiveRecordingId, out Recording oldActiveRec))
            {
                RemoveRecordingIdFromBackgroundMap(tree, tree.ActiveRecordingId);
                ApplyPreTransitionForVesselSwitch(
                    tree,
                    ResolveLiveTreeRecordingPidForRestore(oldActiveRec));
            }

            RemoveRecordingIdFromBackgroundMap(tree, targetRecordingId);
            return CommittedSpawnedVesselRestoreAction.PromoteFromBackground;
        }

        internal static uint ResolveLiveTreeRecordingPidForRestore(Recording rec)
        {
            if (rec == null)
                return 0;
            if (rec.SpawnedVesselPersistentId != 0)
                return rec.SpawnedVesselPersistentId;
            return rec.VesselPersistentId;
        }

        internal static bool IsCommittedSpawnedRecordingRestorable(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;
            if (string.Equals(tree.ActiveRecordingId, rec.RecordingId, StringComparison.Ordinal))
                return true;
            if (HasNextChainSegmentInTree(tree, rec))
                return false;
            if (rec.TerminalStateValue.HasValue)
            {
                TerminalState terminalState = rec.TerminalStateValue.Value;
                if (terminalState == TerminalState.Destroyed
                    || terminalState == TerminalState.Recovered
                    || terminalState == TerminalState.Docked
                    || terminalState == TerminalState.Boarded)
                {
                    return false;
                }
            }

            return string.IsNullOrEmpty(rec.ChildBranchPointId)
                || GhostPlaybackLogic.IsEffectiveLeafForVessel(rec, tree);
        }

        internal static bool HasNextChainSegmentInTree(RecordingTree tree, Recording rec)
        {
            if (tree == null || rec == null || string.IsNullOrEmpty(rec.ChainId))
                return false;

            int nextIdx = rec.ChainIndex + 1;
            foreach (Recording other in tree.Recordings.Values)
            {
                if (other == null || ReferenceEquals(other, rec))
                    continue;
                if (other.ChainId == rec.ChainId
                    && other.ChainIndex == nextIdx
                    && other.ChainBranch == rec.ChainBranch)
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool IsPreferredCommittedSpawnedRestoreCandidate(
            RecordingTree tree,
            Recording candidate,
            Recording currentBest)
        {
            bool candidateIsActive = tree != null
                && string.Equals(tree.ActiveRecordingId, candidate?.RecordingId, StringComparison.Ordinal);
            bool currentBestIsActive = tree != null
                && string.Equals(tree.ActiveRecordingId, currentBest?.RecordingId, StringComparison.Ordinal);
            if (candidateIsActive != currentBestIsActive)
                return candidateIsActive;

            int candidateChainIndex = candidate != null ? candidate.ChainIndex : int.MinValue;
            int currentBestChainIndex = currentBest != null ? currentBest.ChainIndex : int.MinValue;
            if (candidateChainIndex != currentBestChainIndex)
                return candidateChainIndex > currentBestChainIndex;

            int candidateOrder = candidate != null && candidate.TreeOrder >= 0 ? candidate.TreeOrder : int.MinValue;
            int currentBestOrder = currentBest != null && currentBest.TreeOrder >= 0 ? currentBest.TreeOrder : int.MinValue;
            if (candidateOrder != currentBestOrder)
                return candidateOrder > currentBestOrder;

            double candidateEnd = candidate != null ? candidate.EndUT : double.NegativeInfinity;
            double currentBestEnd = currentBest != null ? currentBest.EndUT : double.NegativeInfinity;
            if (candidateEnd != currentBestEnd)
                return candidateEnd > currentBestEnd;

            return string.CompareOrdinal(candidate?.RecordingId, currentBest?.RecordingId) > 0;
        }

        internal static int RemoveRecordingIdFromBackgroundMap(RecordingTree tree, string recordingId)
        {
            if (tree?.BackgroundMap == null || string.IsNullOrEmpty(recordingId))
                return 0;

            List<uint> keysToRemove = null;
            foreach (KeyValuePair<uint, string> entry in tree.BackgroundMap)
            {
                if (!string.Equals(entry.Value, recordingId, StringComparison.Ordinal))
                    continue;

                if (keysToRemove == null)
                    keysToRemove = new List<uint>();
                keysToRemove.Add(entry.Key);
            }

            if (keysToRemove == null)
                return 0;

            for (int i = 0; i < keysToRemove.Count; i++)
                tree.BackgroundMap.Remove(keysToRemove[i]);
            return keysToRemove.Count;
        }

        /// <summary>
        /// Bug #266: pure helper that mirrors the in-session
        /// <see cref="OnVesselSwitchComplete"/> BackgroundMap transition. Moves the
        /// tree's active recording into <c>BackgroundMap</c> and nulls
        /// <c>ActiveRecordingId</c>. Returns <c>true</c> if a BackgroundMap entry
        /// was actually inserted (valid <c>ActiveRecordingId</c> + non-zero PID),
        /// <c>false</c> in degenerate cases where the tree is still nulled but no
        /// background entry was added — the restore path will then treat the new
        /// active vessel as an outsider.
        ///
        /// <para>PID source priority: <paramref name="recorderVesselPid"/> (the
        /// live recorder PID at stash time) takes precedence over the tree
        /// recording's persisted <c>VesselPersistentId</c>, which may lag behind
        /// across docking / boarding edge cases.</para>
        /// </summary>
        internal static bool ApplyPreTransitionForVesselSwitch(
            RecordingTree tree, uint recorderVesselPid)
        {
            if (tree == null) return false;
            string oldActiveRecId = tree.ActiveRecordingId;
            uint oldVesselPid = recorderVesselPid;

            if (oldVesselPid == 0
                && !string.IsNullOrEmpty(oldActiveRecId)
                && tree.Recordings.TryGetValue(oldActiveRecId, out var oldRec))
            {
                oldVesselPid = oldRec.VesselPersistentId;
            }

            bool moved = false;
            if (!string.IsNullOrEmpty(oldActiveRecId) && oldVesselPid != 0)
            {
                tree.BackgroundMap[oldVesselPid] = oldActiveRecId;
                moved = true;
            }
            // In-memory record of the move, read only by
            // TryRevertPreTransitionForVesselSwitch; never serialized.
            tree.VesselSwitchPreTransitionActiveRecordingId = moved ? oldActiveRecId : null;
            tree.VesselSwitchPreTransitionVesselPid = moved ? oldVesselPid : 0;
            tree.ActiveRecordingId = null;
            return moved;
        }

        /// <summary>
        /// Bug #266: stash the active tree for a vessel-switch-induced FLIGHT→FLIGHT
        /// scene reload. Pre-transitions the tree at stash time so the restore
        /// coroutine just has to reinstall it: flushes the recorder, moves the
        /// active recording's vessel PID into <c>BackgroundMap</c>, nulls
        /// <c>ActiveRecordingId</c>, then stashes as <see cref="PendingTreeState.LimboVesselSwitch"/>.
        ///
        /// <para>This mirrors the in-session <see cref="OnVesselSwitchComplete"/> path
        /// at lines 1493–1534, except the <c>activeTree</c> reference must cross the
        /// scene boundary via <see cref="PendingTreeSlot.PendingTree"/> because the
        /// <see cref="ParsekFlight"/> singleton is destroyed on scene reload.</para>
        /// </summary>
        internal void StashActiveTreeForVesselSwitch(double commitUT)
        {
            if (activeTree == null) return;

            // Defensive: enforce the same invariants that CanPreTransitionForVesselSwitch
            // checks. This method is only safe to call when those guards are clear,
            // because the in-flight continuation states (dock merge / split / chain-to-
            // vessel) need Update() logic that won't run across a scene reload. If a
            // future change adds a new state to CanPreTransitionForVesselSwitch but
            // forgets the stash-side check, this re-validation logs a Warn and routes
            // to the legacy Limbo stash so we never silently ship a corrupt state.
            if (!CanPreTransitionForVesselSwitch())
            {
                ParsekLog.Warn("Flight",
                    "StashActiveTreeForVesselSwitch: pre-transition guards no longer hold " +
                    $"(pendingTreeDockMerge={pendingTreeDockMerge}, pendingSplitRecorder={pendingSplitRecorder != null}, " +
                    $"pendingSplitInProgress={pendingSplitInProgress}, " +
                    $"chainToVesselPending={(recorder != null && recorder.ChainToVesselPending)}) — " +
                    "falling back to legacy Limbo stash; OnLoad safety net will finalize");
                StashActiveTreeAsPendingLimbo(commitUT);
                return;
            }

            ParsekLog.Info("Flight",
                $"StashActiveTreeForVesselSwitch: pre-transitioning tree '{activeTree.TreeName}' " +
                $"at UT={commitUT:F1} (activeRecId={activeTree.ActiveRecordingId ?? "<null>"})");
            RecorderStateLog.RecState("StashTreeForSwitch:entry", CaptureRecorderState());

            // 1. Flush the recorder buffers into the active tree's current recording
            //    WITHOUT setting terminal state. Same shape as the in-session path.
            string oldActiveRecId = activeTree.ActiveRecordingId;
            uint oldVesselPid = 0;
            if (recorder != null)
            {
                if (recorder.IsRecording)
                {
                    if (recorder.IsBackgrounded)
                        recorder.IsRecording = false;
                    else
                        recorder.ForceStop();
                }
                FlushRecorderToTreeRecording(recorder, activeTree);

                // Copy rewind save + reserved budget to the root recording.
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "MergeCommitFlush");

                oldVesselPid = recorder.RecordingVesselId;
            }

            // 2. Close background recorder orbit segments and flush their data into
            //    tree recordings. Does NOT set terminal state.
            if (backgroundRecorder != null)
                backgroundRecorder.FinalizeAllForCommit(commitUT);

            // 3. Pre-transition: move the old active recording's PID into BackgroundMap,
            //    null ActiveRecordingId. From the BackgroundMap's point of view this is
            //    identical to the in-session OnVesselSwitchComplete transition at
            //    ParsekFlight.cs:1528–1536. Pure helper so unit tests can exercise the
            //    PID-priority logic without booting Unity.
            int bgPidsBefore = activeTree.BackgroundMap.Count;
            bool moved = ApplyPreTransitionForVesselSwitch(activeTree, oldVesselPid);
            if (moved)
            {
                ParsekLog.Info("Flight",
                    $"StashActiveTreeForVesselSwitch: moved active recording '{oldActiveRecId}' " +
                    $"to BackgroundMap (was {bgPidsBefore} entries, now " +
                    $"{activeTree.BackgroundMap.Count})");
            }
            else
            {
                // Expected, NOT a problem: this is the outsider-chain case where the
                // tree was already in outsider state at stash time (player switched
                // from an outsider vessel to another outsider, both via TS reload).
                // ActiveRecordingId is already null and there's no recording PID to
                // index. The tree restores cleanly with the existing BackgroundMap
                // entries from prior hops; the new active vessel is checked against
                // those in the restore coroutine.
                ParsekLog.Info("Flight",
                    $"StashActiveTreeForVesselSwitch: no active recording to move " +
                    $"(activeRecId='{oldActiveRecId ?? "<null>"}', oldPid={oldVesselPid}) — " +
                    "outsider-chain stash; tree carries existing BackgroundMap entries " +
                    $"({activeTree.BackgroundMap.Count}) across the reload");
            }

            // 4. Stash as LimboVesselSwitch. The OnLoad dispatch routes this state to
            //    the vessel-switch restore coroutine.
            PendingTreeSlot.StashPendingTree(activeTree, PendingTreeState.LimboVesselSwitch);

            ParsekLog.Info("Flight",
                $"StashActiveTreeForVesselSwitch: stashed tree '{activeTree.TreeName}' as " +
                $"LimboVesselSwitch ({activeTree.Recordings.Count} recording(s), " +
                $"{activeTree.BackgroundMap.Count} background entry(ies)) — OnLoad will dispatch");
            RecorderStateLog.RecState("StashTreeForSwitch:post", CaptureRecorderState());
        }
    }
}
