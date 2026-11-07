using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using KSP.UI.Screens;
using ToolbarControl_NS;
using UnityEngine;

namespace Parsek
{
    // The flight-scene switch continuation: consuming an armed stock-action intent (Map Switch-To,
    // Tracking Station Fly, KSC marker Fly) into the committed-tree clone, background-member or
    // standalone continuation segment, arming and binding the switch-segment session, the no-op
    // segment evaluators and auto-discards, and the missed-vessel-switch recovery. ParsekFlight keeps
    // OnVesselSwitchComplete (the onVesselChange subscription) and DispatchConsumeIntentIfArmed (its
    // place in OnFlightReady's order), the in-place continuation predicates and the switch statics
    // other files name, and forwarders for the entry points the map-focus patch, the scene-exit
    // interceptor, the scenario and the test commands call; it reaches this service through
    // SwitchContinuation.
    internal sealed class ParsekFlightSwitchContinuation
    {
        private readonly ParsekFlight host;

        internal ParsekFlightSwitchContinuation(ParsekFlight host)
        {
            this.host = host;
        }

        private const float CommittedSpawnedRestoreRetryIntervalSeconds = 1.0f;

        private float nextCommittedSpawnedRestoreRetryAt;

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

            if (ParsekFlight.restoringActiveTree)
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
                host.FlushRecorderIntoActiveTreeForSerialization();
                // A committed-restore clone is discarded whole on a no-op, so the
                // classifier also tail-checks every other member. Close the on-rails
                // background members' open segments first so that check sees them;
                // a LOADED background member accrues per-frame data the checkpoint
                // does not flush, so keep (the no-session resume's guard).
                bool committedClone = host.activeTree != null
                    && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(host.activeTree.Id);
                if (committedClone && host.backgroundRecorder != null)
                {
                    if (host.backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateActiveSwitchSegmentNoOp: treeId={host.activeTree.Id} " +
                            "keep reason=loaded-background-member (committed-restore clone)");
                        return false;
                    }
                    host.backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
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
            host.MergeFlow.AutoDiscardActiveTreeCore(
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
                host.activeTree != null
                && CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(host.activeTree.Id)
                    ? host.activeTree.Id
                    : null;

            var disposition = SwitchSegmentAttempts.TryDiscardActiveSwitchSegmentAttempt(
                out discardReason);

            // In-flight (pre-stash) the committed clone is the live activeTree, not
            // the pending slot, so the scoped discard leaves it intact
            // (droppedPendingClone=False). Tear it down so it is never serialized
            // isActive; the committed original survives in committedTrees.
            if (cloneTreeId != null
                && host.activeTree != null
                && string.Equals(host.activeTree.Id, cloneTreeId, StringComparison.Ordinal))
            {
                ParsekLog.Info("SwitchSegment",
                    $"DiscardActiveSwitchSegmentAttemptRevertingLiveClone: tearing down live " +
                    $"committed-restore clone treeId={cloneTreeId} to revert to the committed " +
                    $"original (reason={reason})");
                host.AutoDiscardActiveTreeWithMessage(reason, screenMessage, ledgerRecalcReason);
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

            if (ParsekFlight.restoringActiveTree)
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

            if (host.activeTree == null)
            {
                reason = "no-active-tree";
                return false;
            }
            if (!CommittedTreeRestoreAttempt.IsCommittedTreeRestoreAttemptTree(host.activeTree.Id))
            {
                reason = "not-committed-restore-clone";
                return false;
            }
            if (double.IsNaN(host.liveResumeSessionStartUT))
            {
                reason = "no-resume-anchor";
                return false;
            }
            if (host.activeTree.Recordings == null)
            {
                reason = "no-recordings";
                return false;
            }
            // The resume must be the live active recording (the in-place resume),
            // else we cannot reason about what the resume did.
            if (string.IsNullOrEmpty(host.activeTree.ActiveRecordingId)
                || !host.activeTree.Recordings.ContainsKey(host.activeTree.ActiveRecordingId))
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
                host.FlushRecorderIntoActiveTreeForSerialization();
                if (host.backgroundRecorder != null)
                {
                    // LOADED (physics-bubble) background members accrue per-frame
                    // data that CheckpointAllVessels does NOT flush, so they can't
                    // be cleanly tail-checked — defer (the BgMemberOrMixed analog,
                    // data-loss guard). On-rails members ARE flushed and evaluated.
                    if (host.backgroundRecorder.HasLoadedBackgroundMembers)
                    {
                        reason = "loaded-background-member";
                        return false;
                    }
                    host.backgroundRecorder.CheckpointAllVessels(Planetarium.GetUniversalTime());
                }

                // Tree-wide tail check: the resume is a no-op only if NO recording
                // in the clone (the foreground resumed recording AND every
                // on-rails background member) changed anything since the resume
                // anchor. Committed history (all before the anchor) passes
                // trivially. If any member's tail is meaningful, defer so a revert
                // cannot drop it.
                foreach (var kvp in host.activeTree.Recordings)
                {
                    Recording rec = kvp.Value;
                    if (rec == null) continue;
                    if (!SwitchSegmentNoOpClassifier.IsNoOpResumeTail(
                            rec, host.liveResumeSessionStartUT, out string keepReason))
                    {
                        reason = "member:" + (kvp.Key ?? "<null>") + ":" + (keepReason ?? "<none>");
                        ParsekLog.Verbose("SwitchSegment",
                            $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={host.activeTree.Id} " +
                            $"keep reason={reason}");
                        return false;
                    }
                }

                reason = "no-op";
                ParsekLog.Verbose("SwitchSegment",
                    $"TryEvaluateNoSessionCommittedResumeNoOp: treeId={host.activeTree.Id} " +
                    $"sinceUT={host.liveResumeSessionStartUT.ToString("R", CultureInfo.InvariantCulture)} " +
                    $"recordings={host.activeTree.Recordings.Count} noOp=True");
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
            host.AutoDiscardActiveTreeWithMessage(
                reason: reason,
                screenMessage: "Recording discarded - vessel unchanged after switch",
                ledgerRecalcReason: "noop-no-session-committed-resume-discard");
        }

        private void ReplayVesselSwitchCompleteForMissedSwitchRecovery(
            Vessel activeVessel,
            ParsekFlight.MissedVesselSwitchRecoveryDiagnosticContext recoveryDiagnosticContext)
        {
            var previousContext = host.currentVesselSwitchRecoveryDiagnosticContext;
            host.currentVesselSwitchRecoveryDiagnosticContext = recoveryDiagnosticContext;
            try
            {
                host.OnVesselSwitchComplete(activeVessel);
            }
            finally
            {
                host.currentVesselSwitchRecoveryDiagnosticContext = previousContext;
            }
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
                host.currentVesselSwitchRecoveryDiagnosticContext.IsRecovery;

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
            if (host.activeTree != null
                && host.activeTree.BackgroundMap != null
                && host.activeTree.BackgroundMap.ContainsKey(newPid))
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
            if (host.recorder != null && host.recorder.RecordingVesselId != 0
                && host.recorder.RecordingVesselId != newPid)
            {
                return host.recorder.RecordingVesselId;
            }
            if (host.activeTree != null && !string.IsNullOrEmpty(host.activeTree.ActiveRecordingId))
            {
                Recording active;
                if (host.activeTree.Recordings.TryGetValue(host.activeTree.ActiveRecordingId, out active)
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
            bool activeIsCommittedClone = host.activeTree != null
                && !string.IsNullOrEmpty(host.activeTree.Id)
                && RecordingStore.CommittedTrees != null
                && IsTreeIdInCommittedTrees(host.activeTree.Id);

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
            if (!activeIsCommittedClone && host.activeTree == null)
            {
                bool committedMatchExists = ParsekFlight.TryFindCommittedTreeMatchingVessel(
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
                    bool restored = host.TryRestoreCommittedTreeForSpawnedActiveVessel(
                        callerBuildsOwnSegment: true);
                    if (!restored || host.activeTree == null)
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
                host.activeTree, newPid);
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
            string priorActiveRecordingId = host.activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                host.activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                ParsekFlight.MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => ParsekFlight.BuildSwitchSegmentBoundaryPoint(newVessel, ut),
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
                marker, sessionId, host.activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: host.activeTree.Id,
                entryRoute: SwitchSegmentEntryRoute.StartedCommittedSpawnedClone);

            ParsekScenario.Instance.ClearStockActionIntent("consumed-into-segment");
            ParsekLog.Info("SwitchSegment",
                $"route=committed-spawned-clone intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={host.activeTree.Id ?? "<null>"} " +
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
            if (newVessel == null || host.activeTree == null
                || !ParsekFlight.IsAdoptableResumedSpawnSession(
                    session, host.activeTree.Id, host.activeTree.ActiveRecordingId, newVessel.persistentId))
            {
                return false;
            }

            string segmentId = host.activeTree.ActiveRecordingId;
            string branchPointId = null;
            if (host.activeTree.Recordings.TryGetValue(segmentId, out Recording segmentRec)
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
                EntryReason = ParsekFlight.MapIntentActionToEntryReason(marker.Action),
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
                $"treeId={host.activeTree.Id ?? "<null>"} parentRecId={session.ParentRecordingId ?? "<standalone>"} " +
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

        internal static void LogRefusedOtherLaunchSpawnStamps(
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
            if (host.activeTree.BackgroundMap == null
                || !host.activeTree.BackgroundMap.TryGetValue(newPid, out bgRecId)
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
            if (host.activeTree.Recordings == null
                || !host.activeTree.Recordings.TryGetValue(bgRecId, out bgRec)
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
                host.backgroundRecorder?.OnVesselRemovedFromBackground(newPid);
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
                host.activeTree, newPid, focusedVesselRecordingIdHint: null);
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
            string priorActiveRecordingId = host.activeTree.ActiveRecordingId;

            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                host.activeTree,
                parentRecId,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                ParsekFlight.MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId,
                ut => ParsekFlight.BuildSwitchSegmentBoundaryPoint(newVessel, ut),
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
            host.activeTree.BackgroundMap.Remove(newPid);

            // Phase C.1: bind the live recorder to the new continuation
            // recording id so trajectory samples flow into it immediately.
            BindLiveRecorderToSwitchSegment(
                newVessel, priorActiveRecordingId, newRecordingId,
                routeTag: "bg-member-continuation", switchUT: switchUT);

            ArmSwitchSegmentSessionForRoute(
                marker, sessionId, host.activeTree.Id, parentRecId,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: SwitchSegmentEntryRoute.StartedBgMemberContinuation);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route=bg-member-continuation intentId={marker.IntentId:D} " +
                $"sessionId={sessionId:D} treeId={host.activeTree.Id ?? "<null>"} " +
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
            if (host.backgroundRecorder != null)
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
                    host.backgroundRecorder.TryPeekFinalizationCacheTerminalForBackgroundEnd(
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
                        if (host.backgroundRecorder.TryApplyFinalizationCacheForBackgroundEnd(
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
                    ParsekFlight.CaptureTerminalOrbit(bgRec, focusedVessel);
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
            if (host.activeTree == null)
            {
                host.activeTree = new RecordingTree
                {
                    Id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
                    TreeName = Recording.ResolveLocalizedName(newVessel.vesselName) ?? "Standalone",
                    BranchPoints = new List<BranchPoint>(),
                };
                host.EnsureBackgroundRecorderAttached("StartStandaloneContinuationSegment");
                createdFreshTree = true;
            }

            Guid sessionId = Guid.NewGuid();
            string newRecordingId = Guid.NewGuid().ToString("D",
                CultureInfo.InvariantCulture);

            // Capture prior ActiveRecordingId before CreateSwitchContinuationSegment
            // mutates it (a freshly-created tree has it null; the fall-through
            // path may have a non-null prior id from the upstream branch).
            string priorActiveRecordingId = host.activeTree.ActiveRecordingId;

            // Standalone path: parent is null, no branch-point id needed.
            // LOW 10 (PR #876 review): focusedRootPartPid parameter removed
            // — see SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            // VesselName resolution (#autoLOC token -> "Jumping Flea") is
            // centralized inside SwitchSegmentBuilder.CreateSwitchContinuationSegment.
            var creation = SwitchSegmentBuilder.CreateSwitchContinuationSegment(
                host.activeTree,
                parentRecordingIdOrNull: null,
                newPid,
                newVessel.vesselName ?? "Unknown",
                switchUT,
                ParsekFlight.MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId,
                newRecordingId,
                newBranchPointId: null,
                ut => ParsekFlight.BuildSwitchSegmentBoundaryPoint(newVessel, ut),
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
                && string.IsNullOrEmpty(host.activeTree.RootRecordingId))
            {
                host.activeTree.RootRecordingId = newRecordingId;
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
                marker, sessionId, host.activeTree.Id, parentRecordingId: null,
                newRecordingId, switchUT, newPid, sourcePid,
                committedTreeId: null,
                entryRoute: route);

            scenario.ClearStockActionIntent("consumed-into-segment");

            ParsekLog.Info("SwitchSegment",
                $"route={(fallthroughFromBgLookup ? "bg-lookup-failed-standalone" : "standalone")} " +
                $"intentId={marker.IntentId:D} sessionId={sessionId:D} " +
                $"treeId={host.activeTree.Id ?? "<null>"} segmentRecId={newRecordingId} " +
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
                ParsekFlight.MapIntentActionToEntryReason(marker.Action),
                marker.IntentId,
                sessionId, treeId, parentRecordingId, newRecordingId,
                switchUT, focusedPid, sourcePid, committedTreeId);
        }

        /// <summary>
        /// Arms the session for a created segment. The marker-free form serves
        /// <see cref="TryContinueRestoredSpawnInNewSegment"/>, which has no stock intent.
        /// </summary>
        internal void ArmSwitchSegmentSession(
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
            if (host.activeTree != null && host.activeTree.BranchPoints != null)
            {
                for (int i = 0; i < host.activeTree.BranchPoints.Count; i++)
                {
                    BranchPoint bp = host.activeTree.BranchPoints[i];
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
        internal bool BindLiveRecorderToSwitchSegment(
            Vessel newVessel,
            string priorActiveRecordingId,
            string newRecordingId,
            string routeTag,
            double switchUT)
        {
            if (newVessel == null || host.activeTree == null
                || string.IsNullOrEmpty(newRecordingId))
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-skipped reason=null-input route={routeTag} " +
                    $"hasVessel={newVessel != null} hasTree={host.activeTree != null} " +
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
            if (host.recorder != null && host.recorder.IsRecording
                && !string.IsNullOrEmpty(priorActiveRecordingId)
                && !string.Equals(priorActiveRecordingId, newRecordingId,
                    StringComparison.Ordinal))
            {
                string savedActiveId = host.activeTree.ActiveRecordingId;
                try
                {
                    host.activeTree.ActiveRecordingId = priorActiveRecordingId;
                    host.FlushRecorderToTreeRecording(host.recorder, host.activeTree);
                }
                finally
                {
                    host.activeTree.ActiveRecordingId = savedActiveId;
                }
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-flushed route={routeTag} " +
                    $"priorRecordingId={priorActiveRecordingId} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId}");
                host.recorder.IsRecording = false;
                host.recorder = null;
            }
            else if (host.recorder != null)
            {
                // Recorder exists but not recording (e.g. captured-stop holding
                // CaptureAtStop). Drop it — the new segment owns the live state.
                ParsekLog.Info("SwitchSegment",
                    $"recorder-prior-dropped route={routeTag} " +
                    $"vesselPid={newVessel.persistentId} " +
                    $"wasRecording={host.recorder.IsRecording}");
                host.recorder = null;
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
                host.recorder = new FlightRecorder();
                host.recorder.ActiveTree = host.activeTree;
                host.recorder.StartRecording(isPromotion: true);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-threw route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    $"{ex.GetType().Name}: {ex.Message}; recorder reset to null - " +
                    "tree.ActiveRecordingId left at new segment, next frame may rebind");
                host.recorder = null;
                return false;
            }

            if (!host.recorder.IsRecording)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"recorder-bind-failed route={routeTag} " +
                    $"newRecordingId={newRecordingId} vesselPid={newVessel.persistentId} " +
                    "StartRecording returned IsRecording=false; new segment will have no samples");
                host.recorder = null;
                return false;
            }

            host.PrepareSessionStateForRecorderStart("BindLiveRecorderToSwitchSegment:" + routeTag);

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
        /// Safety net for vessel switches that change <see cref="FlightGlobals.ActiveVessel"/>
        /// without reaching either the physics-frame recorder path or onVesselChange.
        /// Replays the normal <see cref="OnVesselSwitchComplete"/> transition once the
        /// frame loop can prove Parsek's active recorder/tree state is out of sync.
        /// </summary>
        internal void HandleMissedVesselSwitchRecovery()
        {
            if (host.committedSpawnedRestoreSuppressedPid != 0)
            {
                Vessel suppressionActiveVessel = FlightGlobals.ActiveVessel;
                ParsekFlight.TickCommittedSpawnedRestoreSuppression(
                    ref host.committedSpawnedRestoreSuppressedPid,
                    suppressionActiveVessel != null ? suppressionActiveVessel.persistentId : 0u,
                    hasActiveTree: host.activeTree != null,
                    hasRecorder: host.recorder != null);
            }

            if (ParsekFlight.ShouldAttemptCommittedSpawnedRestoreInUpdate(
                    host.activeTree != null,
                    host.recorder != null,
                    ParsekFlight.restoringActiveTree,
                    PendingTreeSlot.HasPendingTree,
                    ParsekScenario.ScheduleActiveTreeRestoreOnFlightReady,
                    Time.unscaledTime,
                    nextCommittedSpawnedRestoreRetryAt))
            {
                nextCommittedSpawnedRestoreRetryAt =
                    Time.unscaledTime + CommittedSpawnedRestoreRetryIntervalSeconds;
                if (host.TryRestoreCommittedTreeForSpawnedActiveVessel())
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
            else if (host.activeTree != null || host.recorder != null)
            {
                // Only reset once a live tree/recorder takes over ownership. The other
                // transient blockers (restore coroutine, pending tree, restore mode) are
                // allowed to age out naturally against the 1s retry window.
                nextCommittedSpawnedRestoreRetryAt = 0f;
            }

            Vessel activeVessel = FlightGlobals.ActiveVessel;
            uint activeVesselPid = activeVessel != null ? activeVessel.persistentId : 0;
            bool hasRecorder = host.recorder != null;
            bool recorderIsRecording = hasRecorder && host.recorder.IsRecording;
            bool recorderChainToVesselPending = hasRecorder && host.recorder.ChainToVesselPending;
            uint recorderPid = hasRecorder ? host.recorder.RecordingVesselId : 0;
            bool activeVesselTrackedInBackground = host.activeTree != null
                && activeVesselPid != 0
                && host.activeTree.BackgroundMap.ContainsKey(activeVesselPid);
            bool activeVesselAlreadyArmedForPostSwitchAutoRecord =
                host.IsPostSwitchAutoRecordArmedForPid(activeVesselPid);

            if (!ParsekFlight.ShouldRecoverMissedVesselSwitch(
                    ParsekFlight.restoringActiveTree,
                    host.activeTree != null,
                    host.pendingTreeDockMerge,
                    host.pendingSplitRecorder != null,
                    host.pendingSplitInProgress,
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

            var recoveryDiagnosticContext = new ParsekFlight.MissedVesselSwitchRecoveryDiagnosticContext
            {
                IsRecovery = true,
                ActiveVesselPid = activeVesselPid,
                RecorderVesselPid = recorderPid,
                HasRecorder = hasRecorder,
                RecorderIsRecording = recorderIsRecording,
                RecorderIsBackgrounded = hasRecorder && host.recorder.IsBackgrounded,
                RecorderChainToVesselPending = recorderChainToVesselPending,
                ActiveVesselTrackedInBackground = activeVesselTrackedInBackground,
                ActiveVesselAlreadyArmedForPostSwitchAutoRecord =
                    activeVesselAlreadyArmedForPostSwitchAutoRecord,
                ActiveTreeRecordingId = host.activeTree != null ? host.activeTree.ActiveRecordingId : null,
                ActiveTreeBackgroundMapCount = host.activeTree != null && host.activeTree.BackgroundMap != null
                    ? host.activeTree.BackgroundMap.Count
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
    }
}
