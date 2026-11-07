using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // Stock-action intent consume, switch continuation segments, missed-switch recovery and no-op
    // segment evaluation.
    public partial class ParsekFlight
    {
        internal struct MissedVesselSwitchRecoveryDiagnosticContext
        {
            public bool IsRecovery;
            public uint ActiveVesselPid;
            public uint RecorderVesselPid;
            public bool HasRecorder;
            public bool RecorderIsRecording;
            public bool RecorderIsBackgrounded;
            public bool RecorderChainToVesselPending;
            public bool ActiveVesselTrackedInBackground;
            public bool ActiveVesselAlreadyArmedForPostSwitchAutoRecord;
            public string ActiveTreeRecordingId;
            public int ActiveTreeBackgroundMapCount;

            internal string BuildRecStateRateLimitKey()
            {
                if (!IsRecovery) return null;

                return string.Format(CultureInfo.InvariantCulture,
                    "missed-vessel-switch|activePid={0}|recorderPid={1}|hasRecorder={2}|rec={3}/{4}|chain={5}|tracked={6}|armed={7}|activeRec={8}|bgCount={9}",
                    ActiveVesselPid,
                    RecorderVesselPid,
                    HasRecorder ? 1 : 0,
                    RecorderIsRecording ? 1 : 0,
                    RecorderIsBackgrounded ? 1 : 0,
                    RecorderChainToVesselPending ? 1 : 0,
                    ActiveVesselTrackedInBackground ? 1 : 0,
                    ActiveVesselAlreadyArmedForPostSwitchAutoRecord ? 1 : 0,
                    string.IsNullOrEmpty(ActiveTreeRecordingId) ? "-" : ActiveTreeRecordingId,
                    ActiveTreeBackgroundMapCount);
            }
        }

        /// <summary>
        /// Evaluates whether the armed <see cref="SwitchSegmentSession"/>'s
        /// resumed segment (Tracking-Station Fly / KSC marker Fly / map Switch-To)
        /// changed nothing meaningful and can be auto-discarded so we do not
        /// prolong the ghost state for a segment that did nothing. Same spirit as
        /// <see cref="RecordingOptimizer.TrimBoringTail"/> ("so the ghost finishes
        /// quickly and the real vessel spawns promptly") and the idle-on-pad
        /// auto-discard.
        ///
        /// <para>This method MUTATES like <see cref="IsActiveTreeIdleOnPad"/>: it
        /// flushes the live recorder into the active tree
        /// (<see cref="FlushRecorderIntoActiveTreeForSerialization"/>) so the
        /// segment recording carries its in-flight payload before the pure
        /// classifier reads it. Guards out the restore-coroutine,
        /// Re-Fly-session, and active-merge-journal states (never auto-discard
        /// across those), then delegates resolution + the no-op decision to
        /// <see cref="SwitchSegmentAttempts.TryClassifyActiveSwitchSegmentNoOp"/>.</para>
        /// </summary>
        internal bool TryEvaluateActiveSwitchSegmentNoOp(
            out string reason, out SwitchSegmentDisposition disposition)
        {
            reason = null;
            disposition = SwitchSegmentDisposition.None;

            if (restoringActiveTree)
            {
                reason = "restoring-active-tree";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario)
                || scenario.ActiveSwitchSegmentSession == null)
            {
                reason = "no-session";
                return false;
            }
            if (scenario.ActiveReFlySessionMarker != null)
            {
                reason = "refly-active";
                return false;
            }
            if (scenario.ActiveMergeJournal != null)
            {
                reason = "merge-journal-active";
                return false;
            }

            // B1: populate the segment recording from the live recorder buffers
            // before the classifier reads it (mirrors IsActiveTreeIdleOnPad). The
            // flush only populates activeTree.ActiveRecordingId; the classifier
            // verifies the segment IS that recording.
            //
            // Both callers run inside a Harmony prefix (the HighLogic.LoadScene
            // scene-exit prefix and the map OnSelect prefix). An exception here
            // would propagate out and abort the scene transition / the menu
            // click, so fail closed: log and return keep (no auto-discard).
            try
            {
                FlushRecorderIntoActiveTreeForSerialization();
                // A committed-restore clone is discarded whole on a no-op, so the
                // classifier also tail-checks every other member. Close the on-rails
                // background members' open segments first so that check sees them;
                // a LOADED background member accrues per-frame data the checkpoint
                // does not flush, so keep (the no-session resume's guard).
                bool committedClone = activeTree != null
                    && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id);
                if (committedClone && backgroundRecorder != null)
                {
                    if (backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateActiveSwitchSegmentNoOp: treeId={activeTree.Id} " +
                            "keep reason=loaded-background-member (committed-restore clone)");
                        return false;
                    }
                    backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
                }
                return SwitchSegmentAttempts.TryClassifyActiveSwitchSegmentNoOp(out reason, out disposition);
            }
            catch (System.Exception ex)
            {
                reason = "exception:" + ex.GetType().Name;
                disposition = SwitchSegmentDisposition.None;
                ParsekLog.Warn("SwitchSegment",
                    $"TryEvaluateActiveSwitchSegmentNoOp threw {ex.GetType().Name}: {ex.Message} " +
                    "- treating as keep (no auto-discard)");
                return false;
            }
        }

        /// <summary>
        /// Tears down a no-op STANDALONE resumed switch segment (the whole live
        /// active tree IS the no-op segment — a fresh tree with no committed
        /// history). Reuses the proven <see cref="AutoDiscardActiveTreeCore"/>
        /// body — identical to the idle-on-pad whole-tree teardown — which also
        /// clears any armed <see cref="SwitchSegmentSession"/>. CommittedRestoreClone
        /// is handled separately by
        /// <see cref="DiscardActiveSwitchSegmentAttemptRevertingLiveClone"/>
        /// (revert to the committed original); BgMemberOrMixed defers.
        /// </summary>
        internal void AutoDiscardNoOpStandaloneSwitchSegment(string reason)
        {
            AutoDiscardActiveTreeCore(
                reason: reason,
                screenMessage: "Recording discarded - vessel unchanged after switch",
                ledgerRecalcReason: "noop-switch-segment-discard",
                reapSidecars: true);
        }

        /// <summary>
        /// Scoped-discards the active switch segment and, when the segment lives
        /// in a still-live in-flight COMMITTED-RESTORE CLONE, tears the clone down
        /// so it is never serialized as the active tree — reverting cleanly to the
        /// committed original (which stays in <c>committedTrees</c> the whole
        /// in-flight session, copy-on-write). This is the fix for the data-loss
        /// path where an in-flight committed-clone discard left the clone live,
        /// it stranded into the Limbo pending slot on the next switch, and a later
        /// whole-tree discard wiped the committed mission.
        ///
        /// <para>Used by every PRE-STASH discard path (the in-flight pre-switch
        /// Discard handler and scene-exit Hook 1's committed-clone branch). The
        /// scene-exit MANUAL merge-dialog Discard is POST-stash and stays on its
        /// own (already-safe) path.</para>
        ///
        /// <para>The clone tree id is captured BEFORE the scoped discard, because
        /// <see cref="SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt"/>
        /// clears the restore attempt; the teardown then fires only when the
        /// captured clone is still the live <c>activeTree</c> afterward, so an
        /// unrelated active tree is never nulled. Standalone segments have a null
        /// clone id and keep their existing (untorn-down) behavior here — the
        /// caller routes Standalone to
        /// <see cref="AutoDiscardNoOpStandaloneSwitchSegment"/> when a full
        /// teardown is wanted.</para>
        /// </summary>
        internal RecordingStore.SwitchSegmentDiscardDisposition
            DiscardActiveSwitchSegmentAttemptRevertingLiveClone(
                string reason, string screenMessage, string ledgerRecalcReason,
                out string discardReason)
        {
            string cloneTreeId =
                activeTree != null
                && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id)
                    ? activeTree.Id
                    : null;

            var disposition = SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt(
                out discardReason);

            // In-flight (pre-stash) the committed clone is the live activeTree, not
            // the pending slot, so the scoped discard leaves it intact
            // (droppedPendingClone=False). Tear it down so it is never serialized
            // isActive; the committed original survives in committedTrees.
            if (cloneTreeId != null
                && activeTree != null
                && string.Equals(activeTree.Id, cloneTreeId, StringComparison.Ordinal))
            {
                ParsekLog.Info("SwitchSegment",
                    $"DiscardActiveSwitchSegmentAttemptRevertingLiveClone: tearing down live " +
                    $"committed-restore clone treeId={cloneTreeId} to revert to the committed " +
                    $"original (reason={reason})");
                AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason);
            }

            return disposition;
        }

        /// <summary>
        /// Evaluates the NO-SESSION committed-restore resume path: when you Fly /
        /// Switch-To a vessel that belongs to a committed Parsek mission via a
        /// scene reload, OnLoad resumes the committed tree in-place
        /// (<c>ResumeCommittedActiveRecording</c>) as a copy-on-write clone
        /// WITHOUT arming a <see cref="SwitchSegmentSession"/> (it sets
        /// <see cref="liveResumeSessionStartUT"/> instead). The session-based
        /// no-op auto-discard never sees this, so a do-nothing revisit of a
        /// tracked mission used to fall through to the merge dialog. This returns
        /// true when the resume TAIL (since the live-resume anchor) changed
        /// nothing meaningful, so the caller can revert the clone to the committed
        /// original via <see cref="AutoDiscardNoOpNoSessionCommittedResume"/>.
        ///
        /// <para>Guards out: an armed <see cref="SwitchSegmentSession"/> (the
        /// session path owns that), restore-coroutine, Re-Fly, and merge-journal.
        /// Requires the active tree to be the armed committed-restore clone, a
        /// finite resume anchor, and a resolvable active recording. MUTATES like
        /// <see cref="IsActiveTreeIdleOnPad"/>: flushes the recorder so the tail
        /// is readable. Fails closed (keep) on exception.</para>
        /// </summary>
        internal bool TryEvaluateNoSessionCommittedResumeNoOp(out string reason)
        {
            reason = null;

            if (restoringActiveTree)
            {
                reason = "restoring-active-tree";
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                reason = "no-scenario";
                return false;
            }
            if (scenario.ActiveSwitchSegmentSession != null)
            {
                reason = "has-session"; // the session path owns this
                return false;
            }
            if (scenario.ActiveReFlySessionMarker != null)
            {
                reason = "refly-active";
                return false;
            }
            if (scenario.ActiveMergeJournal != null)
            {
                reason = "merge-journal-active";
                return false;
            }

            if (activeTree == null)
            {
                reason = "no-active-tree";
                return false;
            }
            if (!CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(activeTree.Id))
            {
                reason = "not-committed-restore-clone";
                return false;
            }
            if (double.IsNaN(liveResumeSessionStartUT))
            {
                reason = "no-resume-anchor";
                return false;
            }
            if (activeTree.Recordings == null)
            {
                reason = "no-recordings";
                return false;
            }
            // The resume must be the live active recording (the in-place resume),
            // else we cannot reason about what the resume did.
            if (string.IsNullOrEmpty(activeTree.ActiveRecordingId)
                || !activeTree.Recordings.ContainsKey(activeTree.ActiveRecordingId))
            {
                reason = "no-active-recording";
                return false;
            }

            try
            {
                // Flush the foreground recorder, then checkpoint the background
                // recorder so any open on-rails orbit segments are closed into the
                // tree recordings — so the tail check below sees a background
                // member's real trajectory change (e.g. an SOI crossing flushes a
                // body-changed segment) instead of missing it.
                FlushRecorderIntoActiveTreeForSerialization();
                if (backgroundRecorder != null)
                {
                    // LOADED (physics-bubble) background members accrue per-frame
                    // data that CheckpointAllVessels does NOT flush, so they can't
                    // be cleanly tail-checked — defer (the BgMemberOrMixed analog,
                    // data-loss guard). On-rails members ARE flushed and evaluated.
                    if (backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        return false;
                    }
                    backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
                }

                // Tree-wide tail check: the resume is a no-op only if NO recording
                // in the clone (the foreground resumed recording AND every
                // on-rails background member) changed anything since the resume
                // anchor. Committed history (all before the anchor) passes
                // trivially. If any member's tail is meaningful, defer so a revert
                // cannot drop it.
                foreach (var kvp in activeTree.Recordings)
                {
                    Recording rec = kvp.Value;
                    if (rec == null) continue;
                    if (!SwitchSegmentNoOpClassifier.IsNoOpResumeTail(
                            rec, liveResumeSessionStartUT, out string keepReason))
                    {
                        reason = "member:" + (kvp.Key ?? "<null>") + ":" + (keepReason ?? "<none>");
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={activeTree.Id} " +
                            $"keep reason={reason}");
                        return false;
                    }
                }

                reason = "no-op";
                ParsekLog.Verbose("SwitchSegment",
                    $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={activeTree.Id} " +
                    $"sinceUT={liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"recordings={activeTree.Recordings.Count} noOp=True");
                return true;
            }
            catch (System.Exception ex)
            {
                reason = "exception:" + ex.GetType().Name;
                ParsekLog.Warn("SwitchSegment",
                    $"TryEvaluateNoSessionCommittedResumeNoOp threw {ex.GetType().Name}: " +
                    $"{ex.Message} - treating as keep");
                return false;
            }
        }

        /// <summary>
        /// Reverts a no-op no-session committed-restore resume: discards the
        /// committed-tree restore attempt (purging its same-id post-cutoff event
        /// tails, then clearing it, so the clone's suppressed events cannot become
        /// durable after the clear) and tears the live clone down via
        /// <see cref="AutoDiscardActiveTreeCore"/> (in-memory only - deletes no
        /// files, never touches <c>committedTrees</c>), so the committed original
        /// survives and the clone is never serialized active. The session path's
        /// restore-attempt discard is done by the scoped discard; here there is no
        /// session/segment to scoped-discard, so it is explicit. After this call
        /// the drop-site clear in AutoDiscardActiveTreeCore no-ops (the attempt is
        /// already gone).
        /// </summary>
        internal void AutoDiscardNoOpNoSessionCommittedResume(string reason)
        {
            CommittedTreeRestoreAttempt.DiscardCommittedTreeRestoreAttempt(
                "noop-no-session-committed-resume-revert",
                preserveIrreversibleTails: true);
            AutoDiscardActiveTreeWithMessage(
                reason: reason,
                screenMessage: "Recording discarded - vessel unchanged after switch",
                ledgerRecalcReason: "noop-no-session-committed-resume-discard");
        }

        /// <summary>
        /// Primary detection mechanism for vessel switches in tree mode.
        /// Handles the old-vessel transition to background and, when Parsek is idle,
        /// arms the post-switch watcher for the new active vessel instead of starting
        /// immediately on focus change alone. Fires regardless of on-rails state,
        /// unlike OnPhysicsFrame.
        /// </summary>
        void OnVesselSwitchComplete(Vessel newVessel)
        {
            bool newVesselIsGhost =
                newVessel != null && GhostMapPresence.IsGhostMapVessel(newVessel.persistentId);
            if (newVesselIsGhost) return;

            // #267: skip if restore coroutine is mid-yield — it owns activeTree/recorder
            if (restoringActiveTree)
            {
                ParsekLog.Warn("Flight",
                    $"OnVesselSwitchComplete: skipped — restore coroutine in progress " +
                    $"(vessel '{newVessel?.vesselName}')");
                return;
            }

            LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:entry",
                CaptureRecorderState(),
                currentVesselSwitchRecoveryDiagnosticContext);

            // Watch mode: re-target camera to ghost — KSP reparents pivot on vessel switch.
            // Don't early-return: tree vessel-switch logic below must still run so that
            // programmatic switches (e.g. docking absorbs a vessel) properly transition
            // the recorder state and background mapping.
            watchMode.OnVesselSwitchComplete();

            // Seed lastLandedUT when switching to a vessel already on the surface.
            // Spawned vessels (from recordings) are created directly in LANDED — no
            // onVesselSituationChange fires, so the settle timer is never initialized.
            // Without this, auto-record fails because settledTime computes as 0.
            if (newVessel != null &&
                (newVessel.situation == Vessel.Situations.LANDED ||
                 newVessel.situation == Vessel.Situations.SPLASHED))
            {
                lastLandedUT = Planetarium.GetUniversalTime();
                // Rate-limit per vessel pid: the per-frame missed-switch recovery
                // (Update → ReplayVesselSwitchCompleteForMissedSwitchRecovery)
                // re-invokes this path every frame when the recovery loop is
                // stuck, and the unrate-limited Verbose used to emit ~120 lines
                // per second per landed vessel.
                ParsekLog.VerboseRateLimited("Flight",
                    $"seeded-landed-ut-{newVessel.persistentId}",
                    $"OnVesselSwitchComplete: seeded lastLandedUT={lastLandedUT:F1} (vessel '{newVessel.vesselName}' already {newVessel.situation})");
            }

            if (newVessel == null)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("active vessel became null");
                return;
            }

            string freshStartParentRecordingId = activeTree != null
                ? activeTree.ActiveRecordingId
                : null;

            // Fix 1 (CRITICAL): Don't interfere with pending merge processing.
            // Dock-induced vessel switches must not arm/start before Update() runs the merge handler.
            if (pendingTreeDockMerge)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("pending tree dock merge");
                return;
            }

            // Don't arm/start during pending board merge — the board handler in Update() owns this.
            if (recorder != null && recorder.ChainToVesselPending)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("pending boarding merge");
                return;
            }

            // Boarding detection: onCrewBoardVessel fires before onVesselChange, but no
            // physics frame runs in between — so DecideOnVesselSwitch (in OnPhysicsFrame)
            // never gets a chance to set ChainToVesselPending. Detect this case here and
            // set the flag so HandleTreeBoardMerge runs in Update() (#248).
            if (recorder != null && recorder.IsRecording
                && pendingBoardingTargetPid != 0
                && recorder.RecordingStartedAsEva
                && newVessel.persistentId == pendingBoardingTargetPid)
            {
                recorder.ChainToVesselPending = true;
                ParsekLog.Info("Flight",
                    $"OnVesselSwitchComplete: detected boarding from EVA " +
                    $"(evaPid={recorder.RecordingVesselId}, targetPid={pendingBoardingTargetPid}) " +
                    $"— set ChainToVesselPending");
                return;
            }

            // If the current recorder is still active (OnPhysicsFrame didn't catch the switch,
            // e.g. because isOnRails was true), transition it to background now
            if (activeTree != null
                && recorder != null && recorder.IsRecording && !recorder.IsBackgrounded
                && recorder.RecordingVesselId != newVessel.persistentId)
            {
                EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(active)");
                recorder.TransitionToBackground();
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "OnVesselSwitch(active)");

                // Add old vessel to BackgroundMap
                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec = null;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                }
                activeTree.ActiveRecordingId = null;
                recorder.IsRecording = false;
                uint bgPid1 = recorder.RecordingVesselId;
                RecordingFinalizationCache bgCache1 = oldRec != null
                    ? recorder.GetFinalizationCacheForRecording(oldRec)
                    : recorder.FinalizationCache;
                recorder = null;
                backgroundRecorder?.OnVesselBackgrounded(bgPid1, inheritedFinalizationCache: bgCache1);
                Log($"Tree: onVesselSwitchComplete transitioned old recorder to background");
            }
            // OnPhysicsFrame already backgrounded the recorder (TransitionToBackgroundPending),
            // but Update() hasn't flushed it yet. Flush now so the post-switch watcher can
            // evaluate against committed recorder data and an up-to-date BackgroundMap.
            else if (activeTree != null
                && recorder != null && recorder.IsBackgrounded && recorder.TransitionToBackgroundPending)
            {
                EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(bg)");
                FlushRecorderToTreeRecording(recorder, activeTree);
                CopyRewindSaveToRoot(activeTree, recorder,
                    logTag: "OnVesselSwitch(bg)");

                string oldRecId = activeTree.ActiveRecordingId;
                Recording oldRec = null;
                uint bgPid2 = 0;
                if (oldRecId != null && activeTree.Recordings.TryGetValue(oldRecId, out oldRec)
                    && oldRec.VesselPersistentId != 0)
                {
                    activeTree.BackgroundMap[oldRec.VesselPersistentId] = oldRecId;
                    bgPid2 = oldRec.VesselPersistentId;
                }
                activeTree.ActiveRecordingId = null;
                RecordingFinalizationCache bgCache2 = oldRec != null
                    ? recorder.GetFinalizationCacheForRecording(oldRec)
                    : recorder.FinalizationCache;
                recorder = null;
                if (bgPid2 != 0) backgroundRecorder?.OnVesselBackgrounded(bgPid2, inheritedFinalizationCache: bgCache2);
                Log($"Tree: onVesselSwitchComplete flushed backgrounded recorder (recId={oldRecId})");
            }

            uint newPid = newVessel.persistentId;

            // Phase C consume: validate any armed stock-action intent marker
            // BEFORE the first-modification watcher arms, so a successful
            // immediate-start (committed-clone, BG-member, or standalone
            // continuation) disarms the watcher and the player gets exactly
            // one immediate-start path per UI click. The consume helper
            // returns NoIntent for `[`/`]` cycling, EVA, dock/undock — those
            // continue through the existing arm-decision flow below.
            var consumeResult = TryConsumeStockActionIntent(newVessel);
            if (consumeResult.StartedSegment)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("consumed-by-stock-action-segment");
                LogOnVesselSwitchCompleteRecState(
                    "OnVesselSwitchComplete:post",
                    CaptureRecorderState(),
                    currentVesselSwitchRecoveryDiagnosticContext);
                return;
            }

            bool trackedInActiveTree = activeTree != null
                && activeTree.BackgroundMap.ContainsKey(newPid);
            var armDecision = ParsekFlightPostSwitchAutoRecord.EvaluatePostSwitchAutoRecordArmDecision(
                ParsekFlightPostSwitchAutoRecord.ShouldArmPostSwitchAutoRecord(
                    ParsekSettings.Current?.autoRecordOnFirstModificationAfterSwitch != false,
                    IsRecording,
                    hasNewVessel: true,
                    newVesselIsGhost),
                trackedInActiveTree,
                newVessel.isEVA);

            // Bug #585: in-place continuation Re-Fly suppression. When the live
            // marker pins the new active vessel as the in-place continuation
            // target, the restore coroutine fired by OnFlightReady will resume
            // into the marker's recording. Arming the post-switch outsider
            // watcher here would log a misleading "outsider while idle" line
            // and risk a brief race where the watcher's first physics frame
            // captures a baseline before the coroutine binds the recorder.
            // The watcher already gates on IsRecording / restoringActiveTree
            // via EvaluatePostSwitchAutoRecordSuppression, so this is mostly a
            // diagnostics-clarity fix: we explicitly skip arming and log the
            // marker reason so the post-mortem in KSP.log reads as expected.
            if (armDecision != PostSwitchAutoRecordArmDecision.None
                && IsInPlaceContinuationArrivalForMarker(newPid))
            {
                ParsekLog.Info("Flight",
                    $"Post-switch auto-record suppressed: vessel='{newVessel.vesselName ?? "<unnamed>"}' " +
                    $"pid={newPid} reason=marker-in-place-continuation " +
                    $"sess={ParsekScenario.Instance?.ActiveReFlySessionMarker?.SessionId ?? "<no-id>"}");
                RecorderStateLog.RecState("OnVesselSwitchComplete:post", CaptureRecorderState());
                return;
            }

            switch (armDecision)
            {
                case PostSwitchAutoRecordArmDecision.ArmTrackedBackgroundMember:
                    PostSwitchAutoRecorder.ArmPostSwitchAutoRecord(
                        newVessel,
                        "vessel switch to tracked background member while idle",
                        trackedInActiveTree: true,
                        freshStartParentRecordingId);
                    break;

                case PostSwitchAutoRecordArmDecision.ArmOutsider:
                    PostSwitchAutoRecorder.ArmPostSwitchAutoRecord(
                        newVessel,
                        "vessel switch to outsider while idle",
                        trackedInActiveTree: false,
                        freshStartParentRecordingId);
                    break;
            }
            LogOnVesselSwitchCompleteRecState(
                "OnVesselSwitchComplete:post",
                CaptureRecorderState(),
                currentVesselSwitchRecoveryDiagnosticContext);
        }

        private void ReplayVesselSwitchCompleteForMissedSwitchRecovery(
            Vessel activeVessel,
            MissedVesselSwitchRecoveryDiagnosticContext recoveryDiagnosticContext)
        {
            var previousContext = currentVesselSwitchRecoveryDiagnosticContext;
            currentVesselSwitchRecoveryDiagnosticContext = recoveryDiagnosticContext;
            try
            {
                OnVesselSwitchComplete(activeVessel);
            }
            finally
            {
                currentVesselSwitchRecoveryDiagnosticContext = previousContext;
            }
        }

        internal static void LogOnVesselSwitchCompleteRecState(
            string phase,
            RecorderStateSnapshot snapshot,
            MissedVesselSwitchRecoveryDiagnosticContext recoveryDiagnosticContext)
        {
            if (!recoveryDiagnosticContext.IsRecovery)
            {
                RecorderStateLog.RecState(phase, snapshot);
                return;
            }

            RecorderStateLog.RecStateRateLimited(
                phase,
                snapshot,
                recoveryDiagnosticContext.BuildRecStateRateLimitKey(),
                MissedVesselSwitchRecoveryRecStateIntervalSeconds);
        }

        /// <summary>
        /// Bug #585: returns true when the live <see cref="ReFlySessionMarker"/>
        /// indicates an in-place continuation Re-Fly AND the just-activated
        /// vessel's <c>persistentId</c> matches the marker's active recording.
        /// In that state, post-switch auto-record arming is a no-op at best and
        /// a diagnostics nuisance at worst -- the
        /// <see cref="RestoreActiveTreeFromPending"/> coroutine will bind the
        /// recorder to the marker's recording once <see cref="OnFlightReady"/>
        /// fires.
        /// </summary>
        private bool IsInPlaceContinuationArrivalForMarker(uint newPid)
        {
            return IsInPlaceContinuationArrivalForMarker(
                newPid,
                ParsekScenario.Instance?.ActiveReFlySessionMarker,
                RecordingStore.CommittedRecordings);
        }

        /// <summary>
        /// Pure-static overload for unit testing: caller injects the marker
        /// and committed-recordings list rather than reading from
        /// <see cref="ParsekScenario.Instance"/> + <see cref="RecordingStore.CommittedRecordings"/>.
        /// Use raw committed-recordings read because the marker resolution at
        /// this point is a physical-identity correlation, not a supersede-aware
        /// ERS query (RewindInvoker.cs has the same ERS-exempt pattern in its
        /// FindRecordingById helper).
        /// </summary>
        internal static bool IsInPlaceContinuationArrivalForMarker(
            uint newPid,
            ReFlySessionMarker marker,
            System.Collections.Generic.IReadOnlyList<Recording> committedRecordings)
        {
            if (newPid == 0u) return false;
            if (marker == null) return false;
            // Accept both legacy in-place (active == origin) and the
            // post-#734 fork shape (InPlaceContinuation flag, active != origin).
            // Both keep the player on the SAME physical vessel; the
            // post-switch auto-record arming we want to suppress is the
            // same in either case.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
                return false;
            if (committedRecordings == null) return false;
            for (int i = 0; i < committedRecordings.Count; i++)
            {
                var rec = committedRecordings[i];
                if (rec == null) continue;
                if (!string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId, StringComparison.Ordinal))
                    continue;
                return rec.VesselPersistentId == newPid;
            }
            return false;
        }

        // ---------------------------------------------------------------------
        // Phase C: stock-action intent consume — validates an armed
        // StockActionIntentMarker on a confirmed focus change, picks one of
        // three entry branches (committed clone, BG member, standalone),
        // mutates the live tree through SwitchSegmentBuilder, arms a fresh
        // SwitchSegmentSession, clears the consumed marker. The
        // OnVesselSwitchComplete and OnFlightReady call sites disarm the
        // first-modification watcher on every Started* route so the watcher
        // doesn't fire a second immediate-start path. See
        // docs/dev/plans/segment-scoped-switch-fly-autorecord.md.
        // ---------------------------------------------------------------------

        /// <summary>
        /// 1:1 mapping from the stock UI action that armed the intent to the
        /// segment session's entry reason. The two enums are kept separate so
        /// the type system reflects "intent fired" vs "session started", but
        /// the value mapping is direct.
        /// </summary>
        internal static SwitchSegmentEntryReason MapIntentActionToEntryReason(StockActionType action)
        {
            switch (action)
            {
                case StockActionType.TrackingStationFly: return SwitchSegmentEntryReason.TrackingStationFly;
                case StockActionType.KscMarkerFly: return SwitchSegmentEntryReason.KscMarkerFly;
                case StockActionType.MapSwitchTo: return SwitchSegmentEntryReason.MapSwitchTo;
                default: return SwitchSegmentEntryReason.MapSwitchTo;
            }
        }

        /// <summary>
        /// Captures an initial boundary <see cref="TrajectoryPoint"/> for the
        /// just-activated vessel at the consume UT, delegating to the
        /// background recorder's well-tested absolute-point capture path.
        /// Returns a minimal stub when <paramref name="vessel"/> lacks a body
        /// (test fixtures, scene-load race) so the helper never throws.
        /// </summary>
        internal static TrajectoryPoint BuildSwitchSegmentBoundaryPoint(Vessel vessel, double ut)
        {
            if (vessel == null || vessel.mainBody == null)
            {
                return new TrajectoryPoint
                {
                    ut = ut,
                    bodyName = vessel?.mainBody?.name ?? "Unknown",
                    recordedGroundClearance = double.NaN,
                };
            }
            return BackgroundRecorder.CreateAbsoluteTrajectoryPointFromVessel(vessel, ut);
        }

        /// <summary>
        /// Phase C entry point: validates an armed
        /// <see cref="StockActionIntentMarker"/>, dispatches to one of three
        /// branch routines (committed-tree clone, BG-member continuation,
        /// standalone), or refuses with a logged reason. The caller is
        /// responsible for disarming the first-modification watcher on every
        /// <see cref="SwitchSegmentEntryRoute.StartedCommittedSpawnedClone"/>
        /// / <see cref="SwitchSegmentEntryRoute.StartedBgMemberContinuation"/>
        /// / <see cref="SwitchSegmentEntryRoute.StartedStandalone"/>
        /// / <see cref="SwitchSegmentEntryRoute.FellThroughBgLookupFailed"/> route.
        ///
        /// <para>Plan §"Behavior by Entry Path", §"Segment Creation" steps 2 and 9,
        /// and §"Two rapid switches semantics".</para>
        /// </summary>
        internal StockActionIntentConsumeResult TryConsumeStockActionIntent(Vessel newVessel)
        {
            var result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.NoIntent,
                DiagnosticReason = null,
            };
            var scenario = ParsekScenario.Instance;
            if (scenario == null)
                return result;

            StockActionIntentMarker marker = scenario.CurrentStockActionIntent;
            if (marker == null)
                return result;

            if (newVessel == null)
            {
                // Defensive: a null newVessel cannot match any target. Clear
                // with a logged reason so the marker doesn't survive into a
                // later spurious consume.
                // MED 6 (PR #876 review): use the dedicated Refused_NullVessel
                // route instead of Refused_TargetMismatch so log/test
                // observers can distinguish "no vessel at all" from
                // "wrong vessel" (which is a real PID divergence diagnostic).
                scenario.ClearStockActionIntent("consume-null-vessel");
                ParsekLog.Info("SwitchIntent",
                    $"refused: reason=consume-null-vessel intentId={marker.IntentId:D} " +
                    $"action={marker.Action} markerTargetPid={marker.TargetVesselPersistentId}");
                result.Route = SwitchSegmentEntryRoute.Refused_NullVessel;
                result.DiagnosticReason = "consume-null-vessel";
                return result;
            }

            uint newPid = newVessel.persistentId;

            bool missedSwitchRecoveryActive =
                currentVesselSwitchRecoveryDiagnosticContext.IsRecovery;

            uint activeSessionFocusedPid =
                scenario.ActiveSwitchSegmentSession?.FocusedVesselPersistentId ?? 0u;

            float currentRealtime = Time.realtimeSinceStartup;
            double currentUT = Planetarium.fetch != null
                ? Planetarium.GetUniversalTime()
                : marker.CapturedUT;

            // On-surface targets defer to the normal auto-record trigger instead
            // of starting a switch-fly segment the instant you fly to them. A
            // vessel that is PRELAUNCH (on the pad), LANDED (on the ground), or
            // SPLASHED (on the water) has not started flying yet. Without the
            // LANDED/SPLASHED arms, flying to a vessel already sitting on the
            // ground (a Parsek-spawned committed vessel, reported LANDED, or a
            // saved landed craft) started a recording immediately, which was then
            // discarded as idle-on-pad on scene exit — the "Jumping Flea on the
            // pad" report whose PRELAUNCH-only fix missed the LANDED case.
            bool targetIsOnSurface =
                newVessel.situation == Vessel.Situations.PRELAUNCH
                || newVessel.situation == Vessel.Situations.LANDED
                || newVessel.situation == Vessel.Situations.SPLASHED;

            var outcome = StockActionIntentConsumeDecision.Evaluate(
                marker,
                newPid,
                ParsekProcess.ProcessSessionId,
                currentRealtime,
                currentUT,
                missedSwitchRecoveryActive,
                activeSessionFocusedPid,
                targetIsOnSurface);

            if (outcome != StockActionIntentConsumeDecision.Outcome.Authorized
                && outcome != StockActionIntentConsumeDecision.Outcome.NoIntent)
            {
                string clearReason = StockActionIntentConsumeDecision.ClearReasonFor(outcome);
                string diag = StockActionIntentConsumeDecision.FormatRefusalDiagnostic(
                    outcome, marker, newPid);
                ParsekLog.Info("SwitchIntent", diag);
                if (!string.IsNullOrEmpty(clearReason))
                    scenario.ClearStockActionIntent(clearReason);
                result.Route = StockActionIntentConsumeDecision.RouteForRefusal(outcome);
                result.DiagnosticReason = clearReason;
                return result;
            }

            if (outcome == StockActionIntentConsumeDecision.Outcome.NoIntent)
                return result;

            // Authorized — pick the entry branch. Re-snapshot context now,
            // after the decision predicate, so any rapid second click that
            // raced the consume site still gets the up-to-date activeTree.
            double switchUT = currentUT;
            uint sourcePid = TryResolvePreSwitchVesselPid(newPid);

            // Two-rapid-switches different-target: close prior session.
            var priorSession = scenario.ActiveSwitchSegmentSession;
            if (priorSession != null
                && priorSession.FocusedVesselPersistentId != newPid)
            {
                ParsekLog.Info("SwitchSegment",
                    $"superseded-by-new-switch: prior sessionId={priorSession.SessionId:D} " +
                    $"priorFocusedPid={priorSession.FocusedVesselPersistentId} " +
                    $"newFocusedPid={newPid}");
                scenario.ClearSwitchSegmentSession("superseded-by-new-switch");
            }

            // Branch selection. Order matters: the committed-spawned-vessel
            // clone path takes precedence (plan §"Fly / Switch-To a committed
            // spawned vessel"), then BG-member (§"Goal 4"), then standalone
            // (§"Goal 5"). The clone branch may install activeTree if it
            // hasn't run yet for this scene.
            if (TryRouteCommittedSpawnedClone(newVessel, marker, switchUT, sourcePid, out result))
                return result;

            // After the clone branch refuses, activeTree may still be null
            // (e.g. unrelated vessel with no committed match). The BG-member
            // branch only fires when there IS a live activeTree containing
            // the focused PID in BackgroundMap.
            if (activeTree != null
                && activeTree.BackgroundMap != null
                && activeTree.BackgroundMap.ContainsKey(newPid))
            {
                return StartBgMemberContinuationSegment(newVessel, marker, switchUT, sourcePid);
            }

            return StartStandaloneContinuationSegment(newVessel, marker, switchUT, sourcePid,
                fallthroughFromBgLookup: false,
                fallthroughReason: null);
        }

        /// <summary>
        /// Best-effort pre-switch vessel PID. For Map Switch-To the prior
        /// active recorder still owns the source vessel; for TS Fly / KSC Fly
        /// we're entering FLIGHT fresh and no source exists, so 0 is returned.
        /// </summary>
        private uint TryResolvePreSwitchVesselPid(uint newPid)
        {
            if (recorder != null && recorder.RecordingVesselId != 0
                && recorder.RecordingVesselId != newPid)
            {
                return recorder.RecordingVesselId;
            }
            if (activeTree != null && !string.IsNullOrEmpty(activeTree.ActiveRecordingId))
            {
                Recording active;
                if (activeTree.Recordings.TryGetValue(activeTree.ActiveRecordingId, out active)
                    && active != null
                    && active.VesselPersistentId != 0
                    && active.VesselPersistentId != newPid)
                {
                    return active.VesselPersistentId;
                }
            }
            return 0u;
        }

        /// <summary>
        /// Attempts the committed-spawned-vessel clone branch (plan §"Fly /
        /// Switch-To a committed spawned vessel"). Returns true and writes
        /// <paramref name="result"/> when this branch fires (whether a segment
        /// is started or the helper refuses with a logged reason that should
        /// NOT fall through to BG / standalone). Returns false when the
        /// vessel does not match any committed tree — caller continues to the
        /// BG / standalone branches.
        ///
        /// <para>The #866 copy-on-write clone path may not have run yet (e.g.
        /// when the consume site fires from <see cref="OnVesselSwitchComplete"/>
        /// in a Map Switch-To inside the same FLIGHT scene). When activeTree
        /// is null and a committed tree matches the focused vessel, this
        /// helper invokes <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/>
        /// to install the clone, then attaches the continuation under it.</para>
        /// </summary>
        private bool TryRouteCommittedSpawnedClone(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            out StockActionIntentConsumeResult result)
        {
            result = default;
            uint newPid = newVessel.persistentId;

            // Path A: activeTree is already the #866 clone (e.g. consume fires
            // on OnFlightReady after restore installed it). Detect by tree
            // identity matching a committed tree's id but recordings list
            // matching the live mutable state.
            bool activeIsCommittedClone = activeTree != null
                && !string.IsNullOrEmpty(activeTree.Id)
                && RecordingStore.CommittedTrees != null
                && IsTreeIdInCommittedTrees(activeTree.Id);

            // Path B: activeTree is null but a committed tree matches the
            // focused PID. Pull the #866 clone through the same code path
            // the FlightReady seam uses (TryRestoreCommittedTreeForSpawnedActiveVessel)
            // and then attach the continuation.
            //
            // Bug 5 (post-#876 playtest 2026-05-17): the PID match has two
            // shapes — direct VesselPersistentId match (an already-live
            // committed vessel) and SpawnedVesselPersistentId match (a
            // Parsek-spawned vessel whose live PID was minted at spawn time
            // and stored on the committed recording by the spawn-at-end
            // pipeline). Before this fix, the lookup checked only
            // VesselPersistentId, so a Switch-To on a Parsek-spawned vessel
            // fell through to standalone routing — wrong root cause for the
            // segment created during the bug 5 playtest. We now also check
            // SpawnedVesselPersistentId. The downstream
            // TryRestoreCommittedTreeForSpawnedActiveVessel /
            // TryFindCommittedTreeForSpawnedVessel pipeline already keys off
            // SpawnedVesselPersistentId, so the restore + clone path is
            // unchanged once we route this way.
            if (!activeIsCommittedClone && activeTree == null)
            {
                bool committedMatchExists = TryFindCommittedTreeMatchingVessel(
                    newPid,
                    newVessel.id != Guid.Empty ? newVessel.id.ToString("N") : null);
                if (committedMatchExists)
                {
                    // L5 (PR #876 final review): only emit the
                    // `route=committed-spawned-clone` success log AFTER the
                    // restore actually succeeds. Previously the route log
                    // fired before the call and could be followed by
                    // `committed-spawned-clone-restore-failed-start-standalone`,
                    // making KSP.log readers see an aspirational intermediate
                    // route that never took effect. Emit the failure log
                    // directly on the restore-failure path without an earlier
                    // route line so the FINAL route is always the only one
                    // logged.
                    bool restored = TryRestoreCommittedTreeForSpawnedActiveVessel(
                        callerBuildsOwnSegment: true);
                    if (!restored || activeTree == null)
                    {
                        ParsekLog.Warn("SwitchSegment",
                            $"committed-spawned-clone-restore-failed-start-standalone " +
                            $"focusedPid={newPid} intentId={marker.IntentId:D}");
                        result = StartStandaloneContinuationSegment(
                            newVessel, marker, switchUT, sourcePid,
                            fallthroughFromBgLookup: false,
                            fallthroughReason: "committed-clone-restore-failed");
                        return true;
                    }
                    ParsekLog.Info("SwitchSegment",
                        $"route=committed-spawned-clone reason=focused-pid-matched-committed-tree " +
                        $"pid={newPid} intentId={marker.IntentId:D}");
                    activeIsCommittedClone = true;
                }
            }

            if (!activeIsCommittedClone)
                return false;

            // Path A after a restore that already continued in a new segment (the 1 Hz
            // restore retry can run before OnFlightReady's consume): the click takes over
            // that segment instead of stacking a second one under it.
            if (TryAdoptResumedSpawnSegmentForIntent(newVessel, marker, switchUT, sourcePid, out result))
                return true;

            // Walk to the terminal-leaf parent inside the clone (the resolver
            // is happy to walk any tree; the clone shares structure with the
            // committed parent except for the in-flight mutations).
            var resolution = SwitchSegmentBuilder.ResolveSwitchContinuationParent(
                activeTree, newPid);
            string parentRecId = resolution.Status
                == SwitchContinuationParentStatus.UniqueTerminalLeafFound
                    ? resolution.TerminalLeafRecordingId
                    : null;

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId BEFORE the helper mutates it so
            // BindLiveRecorderToSwitchSegment can flush a prior live recorder
            // (path A or B's restore left one bound to the parent leaf) into
            // the parent recording, not the new continuation.
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"committed-clone-creation-refused-start-standalone " +
                    $"failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                result = StartStandaloneContinuationSegment(
                    newVessel, marker, switchUT, sourcePid,
                    fallthroughFromBgLookup: false,
                    fallthroughReason: "committed-clone-helper-refused");
                return true;
            }

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: "committed-spawned-clone", switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: activeTree.Id,
                entryRoute: SwitchSegmentEntryRoute.StartedCommittedSpawnedClone);

            ParsekScenario.Instance.ClearStockActionIntent("consumed-into-segment");
            ParsekLog.Info("SwitchSegment",
                $"route=committed-spawned-clone intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={activeTree.Id ?? "<null>"} " +
                $"parentRecId={parentRecId ?? "<standalone>"} segmentRecId={newRecordingId} " +
                $"sourcePid={sourcePid} focusedPid={newPid}");

            result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedCommittedSpawnedClone,
                NewRecordingId = newRecordingId,
                NewBranchPointId = newBranchPointId,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
            return true;
        }

        /// <summary>
        /// Pure: true when <paramref name="session"/> is the session
        /// <see cref="TryContinueRestoredSpawnInNewSegment"/> armed for the live tree's
        /// current active recording on <paramref name="focusedPid"/>, so a stock Fly /
        /// Switch-To consumed for the same vessel adopts that segment.
        /// </summary>
        internal static bool IsAdoptableResumedSpawnSession(
            SwitchSegmentSession session,
            string activeTreeId,
            string activeRecordingId,
            uint focusedPid)
        {
            return session != null
                && session.EntryReason == SwitchSegmentEntryReason.ResumedSpawnedVessel
                && focusedPid != 0
                && session.FocusedVesselPersistentId == focusedPid
                && !string.IsNullOrEmpty(activeTreeId)
                && string.Equals(session.TreeId, activeTreeId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(activeRecordingId)
                && string.Equals(session.ActiveSegmentRecordingId, activeRecordingId,
                    StringComparison.Ordinal);
        }

        private bool TryAdoptResumedSpawnSegmentForIntent(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            out StockActionIntentConsumeResult result)
        {
            result = default;
            ParsekScenario scenario = ParsekScenario.Instance;
            SwitchSegmentSession session = scenario?.ActiveSwitchSegmentSession;
            if (newVessel == null || activeTree == null
                || !IsAdoptableResumedSpawnSession(
                    session, activeTree.Id, activeTree.ActiveRecordingId, newVessel.persistentId))
            {
                return false;
            }

            string segmentId = activeTree.ActiveRecordingId;
            string branchPointId = null;
            if (activeTree.Recordings.TryGetValue(segmentId, out Recording segmentRec)
                && segmentRec != null)
            {
                branchPointId = segmentRec.ParentBranchPointId;
            }

            // Same session id (the segment carries it as its SwitchSegmentSessionId stamp),
            // now owned by the stock click.
            scenario.ArmSwitchSegmentSession(new SwitchSegmentSession
            {
                SessionId = session.SessionId,
                TreeId = session.TreeId,
                ParentRecordingId = session.ParentRecordingId,
                ActiveSegmentRecordingId = session.ActiveSegmentRecordingId,
                SourceVesselPersistentId = sourcePid,
                FocusedVesselPersistentId = session.FocusedVesselPersistentId,
                SwitchUT = session.SwitchUT,
                EntryReason = MapIntentActionToEntryReason(marker.Action),
                IntentId = marker.IntentId,
                CommittedTreeId = session.CommittedTreeId,
                PreSessionBranchPointIds = session.PreSessionBranchPointIds != null
                    ? new List<string>(session.PreSessionBranchPointIds)
                    : new List<string>(),
            });
            scenario.ClearStockActionIntent("consumed-into-segment");
            ParsekLog.Info("SwitchSegment",
                $"route=committed-spawned-clone adopted-resumed-spawn-segment " +
                $"intentId={marker.IntentId:D} sessionId={session.SessionId:D} " +
                $"treeId={activeTree.Id ?? "<null>"} parentRecId={session.ParentRecordingId ?? "<standalone>"} " +
                $"segmentRecId={segmentId} sourcePid={sourcePid} focusedPid={newVessel.persistentId} " +
                $"switchUT={switchUT.ToString("R", CultureInfo.InvariantCulture)}");

            result = new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedCommittedSpawnedClone,
                NewRecordingId = segmentId,
                NewBranchPointId = branchPointId,
                SessionId = session.SessionId,
                DiagnosticReason = "consumed-into-segment",
            };
            return true;
        }

        private static bool IsTreeIdInCommittedTrees(string treeId)
        {
            if (string.IsNullOrEmpty(treeId)) return false;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] != null
                    && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bug 5 (post-#876 playtest 2026-05-17): probes both the direct
        /// <see cref="Recording.VesselPersistentId"/> (live committed vessel)
        /// and <see cref="Recording.SpawnedVesselPersistentId"/> (Parsek-
        /// spawned vessel whose live PID was minted at spawn time and stored
        /// on the committed recording by the spawn-at-end pipeline). The
        /// downstream <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/>
        /// pipeline already keys off <c>SpawnedVesselPersistentId</c> via
        /// <see cref="TryFindCommittedTreeForSpawnedVessel"/>, so a positive
        /// answer here lands in the same restore + clone path regardless of
        /// which field matched.
        ///
        /// <para>Before this fix, the lookup checked only
        /// <c>VesselPersistentId</c>, so a Switch-To on a Parsek-spawned
        /// vessel fell through to standalone routing rather than the
        /// committed-clone branch.</para>
        /// </summary>
        internal static bool TryFindCommittedTreeMatchingVessel(uint pid)
            => TryFindCommittedTreeMatchingVessel(pid, null, out _);

        // liveGuid is the focused live vessel's Vessel.id ("N" form, null = unknown). #976-class:
        // the VesselPersistentId branch matches the craft-baked pid, which a relaunch of the same
        // craft reuses, so it additionally requires the same launch (guid). The SpawnedVesselPersistentId
        // branch goes through VesselLaunchIdentity.LiveVesselIsRecordedSpawn: a genuine Parsek
        // spawn pid is KSP-unique and matches on the pid alone, but an adoption stamp
        // (spawned pid == recorded pid) is the craft-baked pid again and needs the same launch.
        // A null/unknown guid falls back to today's pid-only behavior.
        internal static bool TryFindCommittedTreeMatchingVessel(uint pid, string liveGuid)
            => TryFindCommittedTreeMatchingVessel(pid, liveGuid, out _);

        /// <summary>
        /// Single implementation backing both bool overloads. Returns the
        /// matched committed <see cref="RecordingTree"/> via
        /// <paramref name="matchedTree"/> (null when no match), and the bool
        /// result is exactly <c>matchedTree != null</c>. The matching semantics
        /// are identical to the bool overloads: a direct
        /// <see cref="Recording.VesselPersistentId"/> match (gated by
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedLaunch"/> so a
        /// relaunch of the same craft does not collide on the baked pid), plus
        /// the <see cref="Recording.VesselSpawned"/> +
        /// <see cref="Recording.SpawnedVesselPersistentId"/> match through
        /// <see cref="VesselLaunchIdentity.LiveVesselIsRecordedSpawn"/> (a
        /// genuine Parsek spawn pid is KSP-unique; an adoption stamp is the
        /// craft-baked pid and also needs the same launch guid). The first
        /// matching tree wins.
        ///
        /// <para>The out-param overload lets the Map Switch-To Prefix compare
        /// the matched committed tree id against the live active tree id, so a
        /// re-select of the vessel you are already flying (its own committed
        /// clone) does not trigger a pre-switch dialog.</para>
        /// </summary>
        internal static bool TryFindCommittedTreeMatchingVessel(
            uint pid, string liveGuid, out RecordingTree matchedTree)
        {
            matchedTree = null;
            if (pid == 0u) return false;
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            int refusedOtherLaunchStamps = 0;
            for (int i = 0; i < trees.Count; i++)
            {
                var tree = trees[i];
                if (tree == null || tree.Recordings == null) continue;
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec == null) continue;
                    if (rec.VesselPersistentId == pid
                        && VesselLaunchIdentity.LiveVesselIsRecordedLaunch(rec, pid, liveGuid))
                    {
                        matchedTree = tree;
                        LogRefusedOtherLaunchSpawnStamps(
                            "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
                        return true;
                    }
                    // Bug 5: also match recordings whose Parsek-spawned vessel
                    // is the focused one. SpawnedVesselPersistentId is set by
                    // the spawn-at-end pipeline at the new live PID minted on
                    // spawn; VesselPersistentId still carries the original
                    // recording PID.
                    if (IsCommittedSpawnStampMatch(rec, pid, liveGuid))
                    {
                        matchedTree = tree;
                        LogRefusedOtherLaunchSpawnStamps(
                            "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
                        return true;
                    }
                    if (IsOtherLaunchSpawnStamp(rec, pid, liveGuid))
                        refusedOtherLaunchStamps++;
                }
            }
            LogRefusedOtherLaunchSpawnStamps(
                "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
            return false;
        }

        /// <summary>
        /// Does the committed recording's spawn stamp name this live vessel? Requires
        /// <see cref="Recording.VesselSpawned"/> and
        /// <see cref="VesselLaunchIdentity.SpawnStampHeldBy"/>: a genuine Parsek spawn pid
        /// matches on the pid alone, an adoption stamp (the craft-baked pid) only when the
        /// launch guids do not conclusively differ or the live guid is linked to the
        /// recording's (a resumed spawned vessel), so a relaunch of the same craft never
        /// resumes the older launch's committed tree.
        /// </summary>
        internal static bool IsCommittedSpawnStampMatch(Recording rec, uint livePid, string liveGuid)
        {
            if (rec == null || !rec.VesselSpawned || livePid == 0u) return false;
            return VesselLaunchIdentity.SpawnStampHeldBy(rec, livePid, liveGuid);
        }

        /// <summary>
        /// Pure: the refusal half of <see cref="IsCommittedSpawnStampMatch"/>, counted for the
        /// summary line: the spawn pid equals the live pid but the stamp is an adoption stamp of
        /// a conclusively different launch.
        /// </summary>
        internal static bool IsOtherLaunchSpawnStamp(Recording rec, uint livePid, string liveGuid)
        {
            return rec != null
                && rec.VesselSpawned
                && livePid != 0u
                && rec.SpawnedVesselPersistentId == livePid
                && !VesselLaunchIdentity.SpawnStampHeldBy(rec, livePid, liveGuid);
        }

        private static void LogRefusedOtherLaunchSpawnStamps(
            string site, uint pid, string liveGuid, int refused)
        {
            if (refused <= 0) return;
            // Re-reached by the Update retry of the committed-tree restore: rate-limited,
            // with the live identity and the count in the key so a change prints at once.
            ParsekLog.InfoRateLimited("Flight",
                site + "|other-launch-stamp|" + pid.ToString(CultureInfo.InvariantCulture)
                    + "|" + (liveGuid ?? "(none)") + "|" + refused.ToString(CultureInfo.InvariantCulture),
                $"{site}: refused {refused.ToString(CultureInfo.InvariantCulture)} adoption-stamped " +
                $"recording(s) with spawn pid={pid.ToString(CultureInfo.InvariantCulture)} " +
                $"liveGuid={liveGuid ?? "(none)"}: another launch of the craft reuses the baked pid");
        }

        /// <summary>
        /// Goal-4 branch (plan §"Fly / Switch-To a vessel that is a tracked
        /// background member"). Flushes the BG parent via
        /// <see cref="BackgroundRecorder.OnVesselRemovedFromBackground"/>,
        /// resolves the terminal-leaf parent, attaches the continuation, and
        /// removes the focused vessel from
        /// <see cref="RecordingTree.BackgroundMap"/>. On any failure, falls
        /// through to <see cref="StartStandaloneContinuationSegment"/> with a
        /// <c>bg-lookup-failed-start-standalone</c> warn log.
        /// </summary>
        private StockActionIntentConsumeResult StartBgMemberContinuationSegment(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid)
        {
            uint newPid = newVessel.persistentId;
            var scenario = ParsekScenario.Instance;

            // 1. PID-to-recording-id lookup inside activeTree.
            string bgRecId;
            if (activeTree.BackgroundMap == null
                || !activeTree.BackgroundMap.TryGetValue(newPid, out bgRecId)
                || string.IsNullOrEmpty(bgRecId))
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=bg-entry-missing " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "bg-entry-missing");
            }

            Recording bgRec;
            if (activeTree.Recordings == null
                || !activeTree.Recordings.TryGetValue(bgRecId, out bgRec)
                || bgRec == null)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=recording-missing " +
                    $"focusedPid={newPid} bgRecId={bgRecId} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "recording-missing");
            }

            if (bgRec.VesselPersistentId != newPid)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-lookup-failed-start-standalone reason=pid-collision " +
                    $"focusedPid={newPid} bgRecId={bgRecId} " +
                    $"recPid={bgRec.VesselPersistentId} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "pid-collision");
            }

            // 2. Plan §"Segment Creation" step 2: close+flush BG parent at switchUT.
            // The public OnVesselRemovedFromBackground samples the current UT
            // internally; that is acceptable here because consume runs in the
            // same frame as the vessel switch.
            try
            {
                backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"bg-flush-failed-start-standalone focusedPid={newPid} " +
                    $"intentId={marker.IntentId:D} ex={ex.Message}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: true,
                    fallthroughReason: "bg-flush-threw");
            }

            // 2b. GS3-NUDGE-DROPS-UNFINISHED-FLIGHT: the BG close above flushes
            // and boundary-samples but never CLASSIFIES a terminal, unlike every
            // ordinary background close (EndDebrisRecording, deferred destruction,
            // scene-exit finalization). A recording must not end terminal-less
            // because the player switched onto it.
            StampTerminalOnSwitchClosedBgOrigin(bgRec, newVessel, newPid, switchUT);

            // 3. Resolve terminal leaf in the active tree (walks from any PID
            // match — the BG recording itself or any downstream continuation).
            var resolution = SwitchSegmentBuilder.ResolveSwitchContinuationParent(
                activeTree, newPid, focusedVesselRecordingIdHint: null);
            string parentRecId = resolution.Status
                == SwitchContinuationParentStatus.UniqueTerminalLeafFound
                    ? resolution.TerminalLeafRecordingId
                    : null;

            if (resolution.Status == SwitchContinuationParentStatus.AmbiguousStartStandalone)
            {
                // Ambiguous-match: resolver already logged Warn with candidates.
                // Per plan §"Parent Selection Risk" item 4, start standalone.
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: false,
                    fallthroughReason: "ambiguous-parent-bg-fallthrough");
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);
            string newBranchPointId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId before CreateSwitchContinuationSegment
            // mutates it (BG-member entry typically has activeTree.ActiveRecordingId
            // already null — the parent was backgrounded — but be defensive).
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"continuation-refused-start-standalone failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                return StartStandaloneContinuationSegment(newVessel, marker, switchUT,
                    sourcePid, fallthroughFromBgLookup: false,
                    fallthroughReason: "helper-refused");
            }

            // 4. Plan §"Segment Creation" step 9: remove from BG map AFTER the
            // flush so any concurrent observer of BackgroundMap still saw the
            // entry during the close.
            activeTree.BackgroundMap.Remove(newPid);

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: "bg-member-continuation", switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: SwitchSegmentEntryRoute.StartedBgMemberContinuation);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route=bg-member-continuation intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={activeTree.Id ?? "<null>"} " +
                $"parentRecId={parentRecId ?? "<standalone>"} segmentRecId={newRecordingId} " +
                $"bgRecId={bgRecId} sourcePid={sourcePid} focusedPid={newPid}");

            return new StockActionIntentConsumeResult
            {
                Route = SwitchSegmentEntryRoute.StartedBgMemberContinuation,
                NewRecordingId = newRecordingId,
                NewBranchPointId = newBranchPointId,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
        }

        /// <summary>
        /// GS3-NUDGE-DROPS-UNFINISHED-FLIGHT, half 1 (the DATA half). Live-side
        /// wrapper around the pure
        /// <see cref="SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp"/>
        /// decision: when a stock Fly / Switch-To click consumes a BG-recorded
        /// member, <see cref="BackgroundRecorder.OnVesselRemovedFromBackground(uint)"/>
        /// flushes and boundary-samples the member's recording but never
        /// classifies a terminal state — unlike <c>EndDebrisRecording</c>, the
        /// deferred-destruction path, and scene-exit finalization, which all end
        /// with "apply the finalization cache, else classify from the live
        /// vessel". This reuses that exact pair rather than inventing a parallel
        /// classification, so a recording never ends terminal-less merely because
        /// the player glanced at it.
        /// <para>Ordering contract: call AFTER the BG close (the flush seals the
        /// authored data and refreshes the finalization cache) and BEFORE the
        /// continuation segment is attached.</para>
        /// </summary>
        private void StampTerminalOnSwitchClosedBgOrigin(
            Recording bgRec,
            Vessel focusedVessel,
            uint focusedPid,
            double switchUT)
        {
            var decision = SwitchSegmentBuilder.ClassifySwitchCloseTerminalStamp(bgRec);
            if (decision != SwitchCloseTerminalStampDecision.Stamp)
            {
                ParsekLog.Verbose("SwitchSegment",
                    $"origin-terminal-stamp skipped decision={decision} " +
                    $"recId={bgRec?.RecordingId ?? "<none>"} focusedPid={focusedPid} " +
                    $"terminal={bgRec?.TerminalStateValue?.ToString() ?? "<none>"}");
                return;
            }

            string source = "live";
            if (backgroundRecorder != null)
            {
                try
                {
                    // PR #1427 review F1: peek the cache's verdict BEFORE applying
                    // it. This site is the only background close that knows the
                    // vessel is alive (the player just switched TO it), so a
                    // PREDICTED Destroyed from a ballistic-extrapolator cache must
                    // be vetoed here — the scoping clamp pulls it back to switchUT,
                    // so the applier's RejectedTerminalBeforeLastSample guard cannot.
                    TerminalState? cacheTerminal;
                    backgroundRecorder.TryPeekFinalizationCacheTerminalForBackgroundEnd(
                        bgRec, focusedPid, switchUT, out cacheTerminal);

                    var cacheDecision = SwitchSegmentBuilder.ClassifySwitchCloseCacheApply(
                        focusedVessel != null, cacheTerminal);

                    if (cacheDecision == SwitchCloseCacheApplyDecision.SkipPredictedDestroyedWhileAlive)
                    {
                        ParsekLog.Verbose("SwitchSegment",
                            $"origin-terminal-stamp cache-skipped " +
                            $"reason=predicted-destroyed-while-alive " +
                            $"rejectedCacheTerminal={cacheTerminal?.ToString() ?? "<none>"} " +
                            $"recId={bgRec.RecordingId ?? "<no-id>"} focusedPid={focusedPid} " +
                            $"ut={switchUT.ToString("R", CultureInfo.InvariantCulture)} " +
                            "— falling through to live classification");
                    }
                    else
                    {
                        RecordingFinalizationCacheApplyResult cacheResult;
                        if (backgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd(
                                bgRec,
                                focusedPid,
                                switchUT,
                                "SwitchSegment.BgMemberContinuation",
                                allowStale: true,
                                requireDestroyedTerminal: false,
                                confirmedDestroyed: false,
                                out cacheResult))
                        {
                            source = "cache";
                        }
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("SwitchSegment",
                        $"origin-terminal-stamp cache-apply-threw recId={bgRec.RecordingId ?? "<no-id>"} " +
                        $"focusedPid={focusedPid} ex={ex.Message}");
                }
            }

            if (!bgRec.TerminalStateValue.HasValue)
            {
                source = "live";
                if (focusedVessel != null)
                {
                    bgRec.StampTerminalState(
                        RecordingTree.DetermineTerminalState((int)focusedVessel.situation, focusedVessel),
                        "StampTerminalOnSwitchClosedBgOrigin.live");
                    CaptureTerminalOrbit(bgRec, focusedVessel);
                }
                else
                {
                    // No live vessel to classify against and no cache — leave the
                    // recording terminal-less rather than inventing a verdict.
                    ParsekLog.Warn("SwitchSegment",
                        $"origin-terminal-stamp unresolved recId={bgRec.RecordingId ?? "<no-id>"} " +
                        $"focusedPid={focusedPid} reason=no-vessel-no-cache");
                    return;
                }
            }

            bgRec.FilesDirty = true;
            ParsekLog.Verbose("SwitchSegment",
                $"origin-terminal-stamped recId={bgRec.RecordingId ?? "<no-id>"} " +
                $"terminal={bgRec.TerminalStateValue?.ToString() ?? "<none>"} source={source} " +
                $"focusedPid={focusedPid} ut={switchUT.ToString("R", CultureInfo.InvariantCulture)}");
        }

        /// <summary>
        /// Goal-5 branch (plan §"Fly / Switch-To a vessel unrelated to any
        /// Parsek tree"). Creates a fresh <see cref="RecordingTree"/> when
        /// none exists, then attaches a standalone continuation segment as
        /// the root recording. Also serves as the fall-through target for
        /// every other branch's refusal (committed-clone restore fail,
        /// BG-lookup miss, ambiguous parent, helper refusal).
        /// </summary>
        private StockActionIntentConsumeResult StartStandaloneContinuationSegment(
            Vessel newVessel,
            StockActionIntentMarker marker,
            double switchUT,
            uint sourcePid,
            bool fallthroughFromBgLookup,
            string fallthroughReason)
        {
            uint newPid = newVessel.persistentId;
            var scenario = ParsekScenario.Instance;

            // Spin up a fresh tree if one isn't live. The committed-clone
            // branch already installed activeTree when it took its path, so
            // by here a null activeTree means we're genuinely standalone.
            bool createdFreshTree = false;
            if (activeTree == null)
            {
                activeTree = new RecordingTree
                {
                    Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    TreeName = Recording.ResolveLocalizedName(newVessel.vesselName) ?? "Standalone",
                    BranchPoints = new List<BranchPoint>(),
                };
                EnsureBackgroundRecorderAttached("StartStandaloneContinuationSegment");
                createdFreshTree = true;
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId before CreateSwitchContinuationSegment
            // mutates it (a freshly-created tree has it null; the fall-through
            // path may have a non-null prior id from the upstream branch).
            string priorActiveRecordingId = activeTree.ActiveRecordingId;

            // Standalone path: parent is null, no branch-point id needed.
            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                activeTree,
                parentRecordingIdOrNull: null,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId: null,
                ut => BuildSwitchSegmentBoundaryPoint(newVessel, ut),
                sourcePid);

            if (!creation.Created)
            {
                // Both branches refused (committed clone tried first, then
                // standalone). Clear the intent so the player isn't surprised
                // by a delayed retry on the next focus change.
                ParsekLog.Warn("SwitchSegment",
                    $"standalone-creation-refused failureReason={creation.FailureReason ?? "<null>"} " +
                    $"focusedPid={newPid} intentId={marker.IntentId:D}");
                scenario.ClearStockActionIntent("consume-helper-refused");
                return new StockActionIntentConsumeResult
                {
                    Route = SwitchSegmentEntryRoute.Refused_TargetMismatch,
                    DiagnosticReason = creation.FailureReason ?? "standalone-helper-refused",
                };
            }

            if (createdFreshTree
                && string.IsNullOrEmpty(activeTree.RootRecordingId))
            {
                activeTree.RootRecordingId = newRecordingId;
            }

            SwitchSegmentEntryRoute route = fallthroughFromBgLookup
                ? SwitchSegmentEntryRoute.FellThroughBgLookupFailed
                : SwitchSegmentEntryRoute.StartedStandalone;

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: fallthroughFromBgLookup
                    ? "bg-lookup-failed-standalone"
                    : "standalone",
                switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, activeTree.Id, parentRecordingId: null,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: route);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route={(fallthroughFromBgLookup ? "bg-lookup-failed-standalone" : "standalone")} " +
                $"intentId={marker.IntentId:D} sessionId={sessionId:D} " +
                $"treeId={activeTree.Id ?? "<null>"} segmentRecId={newRecordingId} " +
                $"sourcePid={sourcePid} focusedPid={newPid} " +
                $"fallthroughReason={fallthroughReason ?? "<none>"} createdFreshTree={createdFreshTree}");

            return new StockActionIntentConsumeResult
            {
                Route = route,
                NewRecordingId = newRecordingId,
                NewBranchPointId = null,
                SessionId = sessionId,
                DiagnosticReason = "consumed-into-segment",
            };
        }

        /// <summary>
        /// Builds and arms the <see cref="SwitchSegmentSession"/> for a
        /// successfully-created segment. Centralized so every Started* route
        /// produces identically-shaped sessions.
        /// </summary>
        private void ArmSwitchSegmentSessionForRoute(
            StockActionIntentMarker marker,
            Guid sessionId,
            string treeId,
            string parentRecordingId,
            string newRecordingId,
            double switchUT,
            uint focusedPid,
            uint sourcePid,
            string committedTreeId,
            SwitchSegmentEntryRoute entryRoute)
        {
            ArmSwitchSegmentSession(
                MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId, treeId, parentRecordingId, newRecordingId,
                switchUT, focusedPid, sourcePid, committedTreeId);
        }

        /// <summary>
        /// Arms the session for a created segment. The marker-free form serves
        /// <see cref="TryContinueRestoredSpawnInNewSegment"/>, which has no stock intent.
        /// </summary>
        private void ArmSwitchSegmentSession(
            SwitchSegmentEntryReason entryReason,
            Guid intentId,
            Guid sessionId,
            string treeId,
            string parentRecordingId,
            string newRecordingId,
            double switchUT,
            uint focusedPid,
            uint sourcePid,
            string committedTreeId)
        {
            var preSessionBpIds = new List<string>();
            if (activeTree != null && activeTree.BranchPoints != null)
            {
                for (int i = 0; i < activeTree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = activeTree.BranchPoints[i];
                    if (bp == null || string.IsNullOrEmpty(bp.Id)) continue;
                    // Skip the just-attached continuation branch point so the
                    // pre-session list only contains historical BPs.
                    if (!string.IsNullOrEmpty(parentRecordingId)
                        && bp.ChildRecordingIds != null
                        && bp.ChildRecordingIds.Contains(newRecordingId))
                    {
                        continue;
                    }
                    preSessionBpIds.Add(bp.Id);
                }
            }

            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                TreeId = treeId,
                ParentRecordingId = parentRecordingId,
                ActiveSegmentRecordingId = newRecordingId,
                SourceVesselPersistentId = sourcePid,
                FocusedVesselPersistentId = focusedPid,
                SwitchUT = switchUT,
                EntryReason = entryReason,
                IntentId = intentId,
                CommittedTreeId = committedTreeId,
                PreSessionBranchPointIds = preSessionBpIds,
            };
            ParsekScenario scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"session not armed: no scenario sessionId={sessionId:D} " +
                    $"segmentRecId={newRecordingId ?? "<null>"} entryReason={entryReason}");
                return;
            }
            scenario.ArmSwitchSegmentSession(session);
        }

        /// <summary>
        /// Phase C.1: after <see cref="SwitchSegmentBuilder.CreateSwitchContinuationSegment"/>
        /// mutates the tree (sets <see cref="RecordingTree.ActiveRecordingId"/>
        /// to the new continuation), this helper binds a live
        /// <see cref="FlightRecorder"/> to the new recording so trajectory
        /// samples flow into it immediately. Mirrors the canonical
        /// <see cref="PromoteRecordingFromBackground"/> / <see cref="ResumeCommittedActiveRecording"/>
        /// pattern (new <see cref="FlightRecorder"/>, <c>ActiveTree</c> set,
        /// <see cref="FlightRecorder.StartRecording"/> with
        /// <c>isPromotion: true</c>, <see cref="PrepareSessionStateForRecorderStart"/>).
        ///
        /// <para>If a prior live recorder is still bound to the now-stale
        /// previous <see cref="RecordingTree.ActiveRecordingId"/> (committed-clone
        /// path A or B in OnFlightReady where <c>TryRestoreCommittedTreeForSpawnedActiveVessel</c>
        /// already installed a recorder for the parent leaf), we flush it
        /// against the prior id first via a temporary
        /// <see cref="RecordingTree.ActiveRecordingId"/> swap so the prior
        /// recording keeps its accumulated samples and the new continuation
        /// starts clean.</para>
        /// </summary>
        private bool BindLiveRecorderToSwitchSegment(
            Vessel newVessel,
            string priorActiveRecordingId,
            string newRecordingId,
            string routeTag,
            double switchUT)
        {
            if (newVessel == null || activeTree == null
                || string.IsNullOrEmpty(newRecordingId))
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-skipped reason=null-input route={routeTag} " +
                    $"hasVessel={newVessel != null} hasTree={activeTree != null} " +
                    $"newRecordingId={newRecordingId ?? "<null>"}");
                return false;
            }

            // If a prior recorder is live and bound to a different recording
            // (e.g. OnFlightReady's TryRestoreCommittedTreeForSpawnedActiveVessel
            // bound it to the parent leaf), flush it into the prior recording
            // before the new continuation takes over. CreateSwitchContinuationSegment
            // already overwrote tree.ActiveRecordingId — temporarily swap it back
            // so FlushRecorderToTreeRecording targets the parent.
            //
            // LOW 13 (PR #876 review) INVARIANT: this swap-back relies on the
            // entire flush completing synchronously on the Unity main thread.
            // Do NOT introduce a `yield return` or `await` between the swap-to-
            // prior and the swap-back -- a coroutine yield here would leak a
            // stale ActiveRecordingId to any callsite that reads it next frame
            // (including the recorder's own sampling loop). If you need async
            // work in the flush, capture the prior id into a local, do the
            // async, then re-acquire the tree state and recompute the swap.
            if (recorder != null && recorder.IsRecording
                && !string.IsNullOrEmpty(priorActiveRecordingId)
                && !string.Equals(priorActiveRecordingId, newRecordingId,
                    StringComparison.Ordinal))
            {
                string savedActiveId = activeTree.ActiveRecordingId;
                try
                {
                    activeTree.ActiveRecordingId = priorActiveRecordingId;
                    FlushRecorderToTreeRecording(recorder, activeTree);
                }
                finally
                {
                    activeTree.ActiveRecordingId = savedActiveId;
                }
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-flushed route={routeTag} " +
                    $"priorRecordingId={priorActiveRecordingId} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId}");
                recorder.IsRecording = false;
                recorder = null;
            }
            else if (recorder != null)
            {
                // Recorder exists but not recording (e.g. captured-stop holding
                // CaptureAtStop). Drop it — the new segment owns the live state.
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-dropped route={routeTag} " +
                    $"vesselPid={newVessel.persistentId} " +
                    $"wasRecording={recorder.IsRecording}");
                recorder = null;
            }

            // Canonical bind: mirrors PromoteRecordingFromBackground / ResumeCommittedActiveRecording.
            // MED 4 (PR #876 review): wrap the bind in try/catch so a
            // FlightRecorder ctor or StartRecording exception does not leave
            // `recorder` in a partial-construction state while
            // tree.ActiveRecordingId has already been advanced to the new
            // segment by SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // We do NOT roll back tree.ActiveRecordingId: the segment was
            // created and the marker is armed; the recorder failure is a
            // sampling problem the next frame may recover from. Setting
            // `recorder = null` lets the next frame's recorder-binding pass
            // re-create cleanly.
            try
            {
                recorder = new FlightRecorder();
                recorder.ActiveTree = activeTree;
                recorder.StartRecording(isPromotion: true);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-threw route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    $"{ex.GetType().Name}: {ex.Message}; recorder reset to null - " +
                    "tree.ActiveRecordingId left at new segment, next frame may rebind");
                recorder = null;
                return false;
            }

            if (!recorder.IsRecording)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-failed route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    "StartRecording returned IsRecording=false; new segment will have no samples");
                recorder = null;
                return false;
            }

            PrepareSessionStateForRecorderStart("BindLiveRecorderToSwitchSegment:" + routeTag);

            ParsekLog.Info("SwitchSegment",
                string.Format(CultureInfo.InvariantCulture,
                    "recorder-bound route={0} new-recording-id={1} vessel-pid={2} vessel-name='{3}' ut={4}",
                    routeTag,
                    newRecordingId,
                    newVessel.persistentId,
                    newVessel.vesselName ?? "<unnamed>",
                    switchUT.ToString("R", CultureInfo.InvariantCulture)));
            return true;
        }

        /// <summary>
        /// Bug 1 (post-#876 playtest 2026-05-17): single dispatch point for the
        /// stock-action intent consume helper. Called at the top of
        /// <see cref="OnFlightReady"/> after the in-progress-restore-coroutine
        /// skip but before <see cref="ShouldIgnoreFlightReadyReset"/>, so the
        /// consume runs on EVERY OnFlightReady path that has a valid live active
        /// vessel — both the "live recorder/tree already own this flight"
        /// early-return path (TS Fly / KSC Fly after
        /// <see cref="TryRestoreCommittedTreeForSpawnedActiveVessel"/> pre-attached
        /// the recorder) and the full-reset path.
        ///
        /// <para>The consume helper's own gates (intent != null, freshness, target-
        /// PID match) keep this a no-op when no intent is armed.</para>
        ///
        /// <para>Skipped intentionally when <c>restoringActiveTree=true</c> because
        /// the restore coroutine is rebuilding <c>activeTree</c> and consume mutating
        /// tree state mid-restore would race that coroutine. The intent marker
        /// stays armed; the TTL handles the leak.</para>
        ///
        /// <para>M1 (PR #876 final review): also skipped when
        /// <see cref="ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady"/>
        /// is non-<c>None</c> — i.e. when <c>OnLoad</c> has scheduled a restore
        /// coroutine that has not started yet. If the consume runs here it can
        /// install a fresh standalone <c>activeTree</c> and bind the recorder,
        /// then <see cref="ResetFlightReadyState"/> a few lines down nulls
        /// both, leaving <see cref="SwitchSegmentSession.TreeId"/> pointing at
        /// a dead tree. The restore coroutines themselves invoke this helper
        /// once they successfully install the restored tree, so the consume
        /// still runs — just deferred until the tree is alive. If the restore
        /// fails or aborts, the marker's TTL handles the leak.</para>
        /// </summary>
        private void DispatchConsumeIntentIfArmed()
        {
            // M1: defer the consume when a restore is pending — the restore
            // coroutines re-invoke this helper on success. Without this gate
            // the consume could install a fresh standalone tree right before
            // ResetFlightReadyState nulls it, leaving SwitchSegmentSession
            // pointing at a dead TreeId.
            var pendingRestoreMode =
                ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady;
            if (pendingRestoreMode != ParsekScenario.ActiveTreeRestoreMode.None)
            {
                var pendingScenario = ParsekScenario.Instance;
                var pendingMarker = !object.ReferenceEquals(null, pendingScenario)
                    ? pendingScenario.CurrentStockActionIntent
                    : null;
                string pendingIntentId = pendingMarker != null
                    ? pendingMarker.IntentId.ToString("D",
                        System.Globalization.CultureInfo.InvariantCulture)
                    : "<none>";
                ParsekLog.Verbose("SwitchIntent",
                    $"consume-deferred-pending-restore restoreMode={pendingRestoreMode} " +
                    $"intentId={pendingIntentId}");
                return;
            }

            var activeVesselForConsume = FlightGlobals.ActiveVessel;
            if (activeVesselForConsume == null
                || GhostMapPresence.IsGhostMapVessel(activeVesselForConsume.persistentId))
            {
                return;
            }
            var consumeResult = TryConsumeStockActionIntent(activeVesselForConsume);
            if (consumeResult.StartedSegment)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("consumed-by-stock-action-segment");
            }
        }

        /// <summary>
        /// Safety net for vessel switches that change <see cref="FlightGlobals.ActiveVessel"/>
        /// without reaching either the physics-frame recorder path or onVesselChange.
        /// Replays the normal <see cref="OnVesselSwitchComplete"/> transition once the
        /// frame loop can prove Parsek's active recorder/tree state is out of sync.
        /// </summary>
        private void HandleMissedVesselSwitchRecovery()
        {
            if (committedSpawnedRestoreSuppressedPid != 0)
            {
                Vessel suppressionActiveVessel = FlightGlobals.ActiveVessel;
                TickCommittedSpawnedRestoreSuppression(
                    ref committedSpawnedRestoreSuppressedPid,
                    suppressionActiveVessel != null ? suppressionActiveVessel.persistentId : 0u,
                    hasActiveTree: activeTree != null,
                    hasRecorder: recorder != null);
            }

            if (ShouldAttemptCommittedSpawnedRestoreInUpdate(
                    activeTree != null,
                    recorder != null,
                    restoringActiveTree,
                    PendingTreeSlot.HasPendingTree,
                    ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady,
                    Time.unscaledTime,
                    nextCommittedSpawnedRestoreRetryAt))
            {
                nextCommittedSpawnedRestoreRetryAt =
                    Time.unscaledTime + CommittedSpawnedRestoreRetryIntervalSeconds;
                if (TryRestoreCommittedTreeForSpawnedActiveVessel())
                {
                    nextCommittedSpawnedRestoreRetryAt = 0f;
                    return;
                }

                // A matched committed-tree restore may already have installed the tree,
                // detached it from committed storage, and put the returned vessel back in
                // BackgroundMap before the recorder attach failed. Falling through here is
                // intentional: the existing missed-switch recovery path can retry the final
                // promotion/resume from that live tree shape without rolling the tree back.
            }
            else if (activeTree != null || recorder != null)
            {
                // Only reset once a live tree/recorder takes over ownership. The other
                // transient blockers (restore coroutine, pending tree, restore mode) are
                // allowed to age out naturally against the 1s retry window.
                nextCommittedSpawnedRestoreRetryAt = 0f;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint activeVesselPid = activeVessel != null ? activeVessel.persistentId : 0;
            bool hasRecorder = recorder != null;
            bool recorderIsRecording = hasRecorder && recorder.IsRecording;
            bool recorderChainToVesselPending = hasRecorder && recorder.ChainToVesselPending;
            uint recorderPid = hasRecorder ? recorder.RecordingVesselId : 0;
            bool activeVesselTrackedInBackground = activeTree != null
                && activeVesselPid != 0
                && activeTree.BackgroundMap.ContainsKey(activeVesselPid);
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord =
                IsPostSwitchAutoRecordArmedForPid(activeVesselPid);

            if (!ShouldRecoverMissedVesselSwitch(
                    restoringActiveTree,
                    activeTree != null,
                    pendingTreeDockMerge,
                    pendingSplitRecorder != null,
                    pendingSplitInProgress,
                    hasRecorder,
                    recorderIsRecording,
                    recorderChainToVesselPending,
                    recorderPid,
                    activeVesselPid,
                    activeVesselTrackedInBackground,
                    activeVesselAlreadyArmedForPostSwitchAutoRecord))
            {
                return;
            }

            if (GhostMapPresence.IsGhostMapVessel(activeVesselPid)) return;

            var recoveryDiagnosticContext = new MissedVesselSwitchRecoveryDiagnosticContext
            {
                IsRecovery = true,
                ActiveVesselPid = activeVesselPid,
                RecorderVesselPid = recorderPid,
                HasRecorder = hasRecorder,
                RecorderIsRecording = recorderIsRecording,
                RecorderIsBackgrounded = hasRecorder && recorder.IsBackgrounded,
                RecorderChainToVesselPending = recorderChainToVesselPending,
                ActiveVesselTrackedInBackground = activeVesselTrackedInBackground,
                ActiveVesselAlreadyArmedForPostSwitchAutoRecord =
                    activeVesselAlreadyArmedForPostSwitchAutoRecord,
                ActiveTreeRecordingId = activeTree != null ? activeTree.ActiveRecordingId : null,
                ActiveTreeBackgroundMapCount = activeTree != null && activeTree.BackgroundMap != null
                    ? activeTree.BackgroundMap.Count
                    : 0
            };

            // Update() runs every frame; if the recovery handler does not clear
            // the predicate immediately (e.g. background recorder still pending),
            // this branch fires repeatedly for the same vessel. Rate-limit by
            // activePid so each vessel logs at most once per window — visibility
            // is preserved (still a WARN), spam is bounded. The recovery context
            // applies the same cadence to the nested RecState entry/post snapshots
            // without affecting real onVesselChange boundaries.
            ParsekLog.WarnRateLimited("Flight",
                $"missed-vessel-switch-{activeVesselPid}",
                $"Update: recovering missed vessel switch for active vessel '{activeVessel.vesselName}' " +
                $"(activePid={activeVesselPid}, recorderPid={recorderPid}, " +
                $"trackedInBackground={activeVesselTrackedInBackground})");
            ReplayVesselSwitchCompleteForMissedSwitchRecovery(
                activeVessel,
                recoveryDiagnosticContext);
        }

        /// <summary>
        /// Pure guard for the Update() safety net that replays missed vessel switches.
        /// Runs only when tree state is active and stable enough that the normal
        /// <see cref="OnVesselSwitchComplete"/> path is safe to reuse.
        /// </summary>
        internal static bool ShouldRecoverMissedVesselSwitch(
            bool isRestoringActiveTree,
            bool hasActiveTree,
            bool pendingTreeDockMerge,
            bool hasPendingSplitRecorder,
            bool pendingSplitInProgress,
            bool hasRecorder,
            bool recorderIsRecording,
            bool recorderChainToVesselPending,
            uint recorderVesselPid,
            uint activeVesselPid,
            bool activeVesselTrackedInBackground,
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord)
        {
            if (isRestoringActiveTree) return false;
            if (!hasActiveTree) return false;
            if (pendingTreeDockMerge) return false;
            if (hasPendingSplitRecorder || pendingSplitInProgress) return false;
            if (activeVesselPid == 0) return false;

            if (hasRecorder)
            {
                if (!recorderIsRecording) return false;
                if (recorderChainToVesselPending) return false;
                if (recorderVesselPid == 0) return false;
                return recorderVesselPid != activeVesselPid;
            }

            return activeVesselTrackedInBackground
                && !activeVesselAlreadyArmedForPostSwitchAutoRecord;
        }
    }
}
