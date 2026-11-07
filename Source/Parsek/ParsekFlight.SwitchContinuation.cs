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
        /// Primary detection mechanism for vessel switches in tree mode.
        /// Handles the old-vessel transition to background and, when Parsek is idle,
        /// arms the post-switch watcher for the new active vessel instead of starting
        /// immediately on focus change alone. Fires regardless of on-rails state,
        /// unlike OnPhysicsFrame.
        /// </summary>
        internal void OnVesselSwitchComplete(Vessel newVessel)
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
                SplitBrancher.EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(active)");
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
                SplitBrancher.EmitPendingSplitBeforeFocusLeaves("OnVesselSwitchComplete(bg)");
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
            var consumeResult = SwitchContinuation.TryConsumeStockActionIntent(newVessel);
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
                        ParsekFlightSwitchContinuation.LogRefusedOtherLaunchSpawnStamps(
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
                        ParsekFlightSwitchContinuation.LogRefusedOtherLaunchSpawnStamps(
                            "TryFindCommittedTreeMatchingVessel", pid, liveGuid, refusedOtherLaunchStamps);
                        return true;
                    }
                    if (IsOtherLaunchSpawnStamp(rec, pid, liveGuid))
                        refusedOtherLaunchStamps++;
                }
            }
            ParsekFlightSwitchContinuation.LogRefusedOtherLaunchSpawnStamps(
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
            var consumeResult = SwitchContinuation.TryConsumeStockActionIntent(activeVesselForConsume);
            if (consumeResult.StartedSegment)
            {
                PostSwitchAutoRecorder.DisarmPostSwitchAutoRecord("consumed-by-stock-action-segment");
            }
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
