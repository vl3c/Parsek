using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for the post-revert merge dialog.
    /// </summary>
    public static class MergeDialog
    {
        private const string DialogName = "ParsekMerge";
        private const string MergeLockId = "ParsekMergeDialog";
        // SplitAtSection writes back-to-back UT bounds; 50ms covers float
        // rounding and one-frame skew without bridging a real inter-recording gap.

        internal enum ReFlyMergeCommitResult
        {
            NotApplicable,
            Completed,
            Interrupted,
        }

        /// <summary>
        /// Selects the button label / dialog title copy for the
        /// pre-transition <see cref="ShowTreeDialog"/> overload.
        /// </summary>
        internal enum MergeDialogButtonLabels
        {
            /// <summary>"Merge to Timeline" / "Discard"</summary>
            Default,
            /// <summary>Dynamic timeline action label / "Discard"</summary>
            ReFlyAttempt,
        }

        /// <summary>
        /// Fired after a tree is committed via the merge dialog.
        /// ParsekFlight subscribes to re-evaluate ghost chains.
        /// </summary>
        internal static System.Action OnTreeCommitted;

        /// <summary>
        /// Clears the deferred merge dialog flag and removes the input lock.
        /// Called from button callbacks and popup teardown paths.
        /// </summary>
        internal static void ClearPendingFlag(string reason = null)
        {
            bool wasPending = ParsekScenario.MergeDialogPending;
            ParsekScenario.MergeDialogPending = false;
            InputLockManager.RemoveControlLock(MergeLockId);
            string message =
                $"Cleared pending flag and input lock " +
                $"(reason='{FormatClearReason(reason)}', wasPending={wasPending})";
            if (wasPending)
                ParsekLog.Info("MergeDialog", message);
            else
                ParsekLog.Verbose("MergeDialog", message);
        }

        /// <summary>
        /// Dismisses the visible merge popup, if any, and always clears the
        /// deferred flag/input lock that were armed when the dialog opened.
        /// Use this for non-button teardown paths such as scene/test cleanup.
        /// </summary>
        internal static void DismissAndClearPendingFlag(string reason)
        {
            try
            {
                PopupDialog.DismissPopup(DialogName);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("MergeDialog",
                    $"DismissAndClearPendingFlag: DismissPopup('{DialogName}') threw " +
                    $"{ex.GetType().Name}: {ex.Message}; continuing cleanup " +
                    $"(reason='{FormatClearReason(reason)}')");
            }

            ClearPendingFlag(reason);
        }

        /// <summary>
        /// Blocks all player interaction while the merge dialog is shown.
        /// Prevents entering KSC buildings or other actions during the dialog.
        /// </summary>
        internal static void LockInput()
        {
            InputLockManager.SetControlLock(ControlTypes.All, MergeLockId);
            ParsekLog.Verbose("MergeDialog", "Input lock set");
        }

        // ================================================================
        // Tree merge dialog
        // ================================================================

        internal static void ShowTreeDialog(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "Cannot show tree dialog: tree is null");
                return;
            }

            // Bug fix (refly-suppressed-non-leaf): when a Re-Fly session is
            // active, gather the session-suppressed subtree closure so
            // BuildDefaultVesselDecisions can force ghost-only on every
            // non-leaf recording inside it (parents + chain siblings of the
            // origin). Without this, GetAllLeaves() returns only chain tips,
            // and parent recordings keep their VesselSnapshot — later
            // GhostPlaybackLogic.ShouldSpawnAtRecordingEnd happily spawns
            // them as real vessels alongside the playback ghost.
            var scenarioForSuppression = ParsekScenario.Instance;
            ReFlySessionMarker activeMarker =
                !object.ReferenceEquals(null, scenarioForSuppression)
                    ? scenarioForSuppression.ActiveReFlySessionMarker
                    : null;
            HashSet<string> suppressedRecordingIds = null;
            string activeReFlyTargetId = null;
            if (activeMarker != null)
            {
                activeReFlyTargetId = activeMarker.ActiveReFlyRecordingId;
                try
                {
                    var closure = EffectiveState.ComputeSessionSuppressedSubtree(activeMarker);
                    if (closure != null && closure.Count > 0)
                        suppressedRecordingIds = new HashSet<string>(closure, System.StringComparer.Ordinal);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("MergeDialog",
                        $"ShowTreeDialog: ComputeSessionSuppressedSubtree threw {ex.GetType().Name}: " +
                        $"{ex.Message} — falling back to leaf-only ghost-only decisions");
                }
            }

            var decisions = BuildDefaultVesselDecisions(
                tree, suppressedRecordingIds, activeReFlyTargetId);
            int spawnCount = 0;
            foreach (var val in decisions.Values)
                if (val) spawnCount++;

            ParsekLog.Info("MergeDialog",
                $"Tree merge dialog: tree='{tree.TreeName}', recordings={tree.Recordings.Count}, " +
                $"spawnable={spawnCount}");

            // Re-fly merge: a marker is active and the dialog is asking the
            // player to lock in the re-flight as the canonical entry. Show
            // the re-flight recording's own vessel name + duration (not the
            // whole-tree summary used for ordinary tree merges) and route
            // the body through the same auto-seal preview the pre-transition
            // path uses - this deferred fallback can still finalize via
            // TryCommitReFlySupersede and auto-seal the slot Immutable, so
            // the auto-seal copy and reason list must appear here
            // too when the preview classifies the attempt as sealable.
            var reFlyScenario = ParsekScenario.Instance;
            string message;
            bool timelineActionPermanent = true;
            string mergeLabel = BuildTimelineActionButtonLabel(timelineActionPermanent);
            string title = BuildTimelineActionDialogTitle(timelineActionPermanent);
            if (!object.ReferenceEquals(null, reFlyScenario)
                && reFlyScenario.ActiveReFlySessionMarker != null)
            {
                var marker = reFlyScenario.ActiveReFlySessionMarker;
                Recording reFlyRec = FindReFlyRecording(marker, tree);
                string vesselLabel = reFlyRec != null
                    ? (reFlyRec.VesselName ?? tree.TreeName ?? "<unnamed>")
                    : (tree.TreeName ?? "<unnamed>");
                double reFlyDuration = reFlyRec != null
                    ? System.Math.Max(0.0, reFlyRec.EndUT - reFlyRec.StartUT)
                    : ComputeTreeDurationRange(tree);

                // Preview.NoSeal is the safe fallback when reFlyRec is null
                // (rec missing from the pending tree AND the committed list -
                // FindReFlyRecording returned null). Preview's null-guard
                // would also collapse to NoSeal but skipping the call avoids
                // a CollectRecordingIdsForSafetyGate walk on a null. Live
                // vessel may legitimately be null in Space Center / Tracking
                // Station fallback - Preview tolerates it (skips the live-
                // terminal reasons; science + structural reasons still fire).
                ReFlyAutoSealPreviewResult preview = reFlyRec != null
                    ? ReFlyAutoSealPreviewer.Preview(
                        reFlyRec, marker, FlightGlobals.ActiveVessel)
                    : ReFlyAutoSealPreviewResult.NoSeal();
                string labelSource;
                timelineActionPermanent =
                    DetermineReFlyTimelineActionIsPermanent(
                        reFlyRec, marker, preview, out labelSource);
                mergeLabel = BuildTimelineActionButtonLabel(timelineActionPermanent);
                title = BuildTimelineActionDialogTitle(timelineActionPermanent);
                message = BuildReFlyDialogBody(
                    vesselLabel, reFlyDuration, preview);

                ParsekLog.Info("MergeDialog",
                    $"Re-Fly auto-seal preview (post-transition): " +
                    $"willSeal={preview.WillAutoSeal} " +
                    $"actionPermanent={timelineActionPermanent} " +
                    $"button='{mergeLabel}' " +
                    $"labelSource={labelSource ?? "<none>"} " +
                    $"reasons=[{string.Join(",", preview.Reasons)}] " +
                    $"sess={marker.SessionId ?? "<no-id>"}");
            }
            else
            {
                // Bug 3 (post-#876 playtest 2026-05-17): unified whole-tree
                // body for both regular tree-merge and switch-segment scoped
                // merges. The duration line distinguishes a 16s switch
                // segment from a 30-minute launch — the bespoke entry-reason
                // copy ("Keep your switch into ..." / "Keep your new flight
                // on ...") that used to live here was confusing because it
                // didn't surface the duration the player needed to tell
                // them apart.
                if (!object.ReferenceEquals(null, reFlyScenario)
                    && reFlyScenario.ActiveSwitchSegmentSession != null)
                {
                    ParsekLog.Info("MergeDialog",
                        $"Switch-segment merge dialog (post-transition): " +
                        $"sessionId={reFlyScenario.ActiveSwitchSegmentSession.SessionId:D} " +
                        $"entryReason={reFlyScenario.ActiveSwitchSegmentSession.EntryReason}");
                }
                message = BuildWholeTreeMergeDialogBody(tree);
            }

            var capturedDecisions = decisions;
            int capturedSpawnCount = spawnCount;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton(mergeLabel, () =>
                {
                    MergeCommit(tree, capturedDecisions, capturedSpawnCount);
                }),
                new DialogGUIButton("Discard", () =>
                {
                    MergeDiscard(tree);
                })
            };

            // Order matters: DismissPopup may invoke the previous popup's
            // OnDismiss handler, which clears the lock armed by LockInput.
            PopupDialog.DismissPopup(DialogName);
            LockInput();
            ParsekScenario.MergeDialogPending = true;
            PopupDialog popup = PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    message,
                    title,
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
            if (popup != null)
            {
                popup.OnDismiss += () =>
                {
                    ClearPendingFlag("popup teardown");
                };
            }
            else
            {
                ClearPendingFlag("popup spawn returned null");
                ParsekLog.Warn("MergeDialog",
                    $"ShowTreeDialog: SpawnPopupDialog returned null for tree='{tree.TreeName}'");
            }
        }

        /// <summary>
        /// Pre-transition merge dialog overload (Issue: scene-exit confirmation
        /// in flight). Shows the dialog while the player is still in flight,
        /// before <c>HighLogic.LoadScene</c> completes. The button handler runs
        /// <paramref name="preCommitFinalize"/> -> <see cref="MergeCommit"/> /
        /// <see cref="MergeDiscard"/> -> <paramref name="postChoice"/> in
        /// order. Decision-building happens AFTER
        /// <paramref name="preCommitFinalize"/> has stashed the pending tree
        /// so <see cref="CanPersistVessel"/> reads finalized
        /// <c>TerminalStateValue</c> rather than the live activeTree's null
        /// values.
        ///
        /// <para>If <see cref="MergeDiscard"/> refuses because of an active
        /// merge journal (<see cref="ParsekScenario.ActiveMergeJournal"/>),
        /// <paramref name="postChoice"/> is NOT invoked - the player remains
        /// in flight and the prefix's blocked LoadScene stays blocked.</para>
        /// </summary>
        internal static void ShowTreeDialog(
            RecordingTree liveTree,
            MergeDialogButtonLabels labels,
            System.Action preCommitFinalize,
            System.Action postChoice)
        {
            if (liveTree == null)
            {
                ParsekLog.Warn("MergeDialog", "ShowTreeDialog (pre-transition): liveTree is null");
                return;
            }

            // Display-only data computed from the live tree. Vessel name,
            // duration, and Re-Fly recording lookup are safe to read pre-finalize.
            var reFlyScenario = ParsekScenario.Instance;
            ReFlySessionMarker marker =
                !object.ReferenceEquals(null, reFlyScenario)
                    ? reFlyScenario.ActiveReFlySessionMarker
                    : null;
            string title;
            string message;
            string mergeLabel;
            string discardLabel;
            if (labels == MergeDialogButtonLabels.ReFlyAttempt)
            {
                title = "Re-Fly attempt - leaving flight";
                discardLabel = "Discard";
                Recording reFlyRec = marker != null
                    ? FindReFlyRecording(marker, liveTree)
                    : null;
                string vesselLabel = reFlyRec != null
                    ? (reFlyRec.VesselName ?? liveTree.TreeName ?? "<unnamed>")
                    : (liveTree.TreeName ?? "<unnamed>");
                double reFlyDuration = reFlyRec != null
                    ? System.Math.Max(0.0, reFlyRec.EndUT - reFlyRec.StartUT)
                    : ComputeTreeDurationRange(liveTree);

                // Auto-seal preview (situation sampled at dialog spawn
                // only - while the dialog sits open under LockInput, KSP
                // physics still runs and the active vessel situation
                // could in theory flip; the production classifier
                // re-classifies at finalize, so a transient flicker only
                // affects the dialog wording, not the seal verdict).
                var preview = ReFlyAutoSealPreviewer.Preview(
                    reFlyRec, marker, FlightGlobals.ActiveVessel);
                string labelSource;
                bool timelineActionPermanent =
                    DetermineReFlyTimelineActionIsPermanent(
                        reFlyRec, marker, preview, out labelSource);
                mergeLabel = BuildTimelineActionButtonLabel(timelineActionPermanent);
                message = BuildReFlyDialogBody(vesselLabel, reFlyDuration, preview);

                ParsekLog.Info("MergeDialog",
                    $"Re-Fly auto-seal preview: willSeal={preview.WillAutoSeal} " +
                    $"actionPermanent={timelineActionPermanent} " +
                    $"button='{mergeLabel}' " +
                    $"labelSource={labelSource ?? "<none>"} " +
                    $"reasons=[{string.Join(",", preview.Reasons)}] " +
                    $"sess={marker?.SessionId ?? "<no-id>"}");
            }
            else
            {
                title = "Confirm Merge to Timeline";
                mergeLabel = "Merge to Timeline";
                discardLabel = "Discard";
                // Bug 3 (post-#876 playtest 2026-05-17): unified body for both
                // regular tree-merge and switch-segment scoped merges. See
                // BuildWholeTreeMergeDialogBody for rationale.
                var switchScenario = ParsekScenario.Instance;
                if (!object.ReferenceEquals(null, switchScenario)
                    && switchScenario.ActiveSwitchSegmentSession != null)
                {
                    ParsekLog.Info("MergeDialog",
                        $"Switch-segment merge dialog (pre-transition): " +
                        $"sessionId={switchScenario.ActiveSwitchSegmentSession.SessionId:D} " +
                        $"entryReason={switchScenario.ActiveSwitchSegmentSession.EntryReason}");
                }
                message = BuildWholeTreeMergeDialogBody(liveTree);
            }

            ParsekLog.Info("MergeDialog",
                $"Pre-transition tree merge dialog: tree='{liveTree.TreeName}', " +
                $"recordings={liveTree.Recordings.Count}, labels={labels}");

            // Hide Discard at button-build time when a merge journal is
            // active (mirrors ReFlyRevertDialog's button gate). Handler
            // refusal is the load-bearing safety check; this is just UX.
            bool journalActive =
                !object.ReferenceEquals(null, reFlyScenario)
                && reFlyScenario.ActiveMergeJournal != null;

            DialogGUIButton[] buttons = journalActive
                ? new[]
                  {
                      new DialogGUIButton(mergeLabel, () => RunPreTransitionAction(
                          isMerge: true,
                          preCommitFinalize: preCommitFinalize,
                          postChoice: postChoice)),
                  }
                : new[]
                  {
                      new DialogGUIButton(mergeLabel, () => RunPreTransitionAction(
                          isMerge: true,
                          preCommitFinalize: preCommitFinalize,
                          postChoice: postChoice)),
                      new DialogGUIButton(discardLabel, () => RunPreTransitionAction(
                          isMerge: false,
                          preCommitFinalize: preCommitFinalize,
                          postChoice: postChoice)),
                  };

            PopupDialog.DismissPopup(DialogName);
            LockInput();
            ParsekScenario.MergeDialogPending = true;
            PopupDialog popup = PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    message,
                    title,
                    HighLogic.UISkin,
                    buttons
                ),
                false,
                HighLogic.UISkin
            );
            if (popup != null)
            {
                popup.OnDismiss += () =>
                {
                    ClearPendingFlag("popup teardown");
                };
            }
            else
            {
                ClearPendingFlag("popup spawn returned null");
                ParsekLog.Warn("MergeDialog",
                    $"ShowTreeDialog (pre-transition): SpawnPopupDialog returned null for tree='{liveTree.TreeName}'");
            }
        }

        /// <summary>
        /// Pre-switch decision dialog for the rapid Switch-To case (Map view
        /// "Switch To" clicked while a <see cref="SwitchSegmentSession"/> is
        /// already armed). Opens BEFORE the new stock <c>SetActiveVessel</c>
        /// runs and presents two buttons:
        ///
        /// <list type="bullet">
        /// <item><b>Merge</b>: finalize + stash + commit the prior session's
        ///     active tree using the pre-transition merge path (same as
        ///     scene-exit), then arm the new intent and call
        ///     <c>FlightGlobals.SetActiveVessel(target)</c>.</item>
        /// <item><b>Discard</b>: scoped-discard the prior session via
        ///     <see cref="RecordingStore.TryDiscardActiveSwitchSegmentAttempt"/>,
        ///     then arm the new intent and call
        ///     <c>FlightGlobals.SetActiveVessel(target)</c>.</item>
        /// </list>
        ///
        /// <para>There is no Cancel button — Switch-To is unambiguous player
        /// intent. The dialog only decides what to do with the prior segment
        /// before the switch proceeds.</para>
        ///
        /// <para>Returns true when the dialog was spawned (caller's Prefix
        /// returns false to skip the stock <c>OnSelect</c>). Returns false
        /// when <see cref="PopupDialog.SpawnPopupDialog"/> returned null —
        /// the caller's Prefix should fall back to running the original
        /// <c>OnSelect</c> so the supersede defensive path can take over.</para>
        /// </summary>
        internal static bool ShowPreSwitchDecisionDialog(
            Vessel target,
            System.Action mergeAction,
            System.Action discardAction)
        {
            if (target == null)
            {
                ParsekLog.Warn("MergeDialog",
                    "ShowPreSwitchDecisionDialog: target vessel is null - cannot spawn dialog");
                return false;
            }
            if (mergeAction == null || discardAction == null)
            {
                ParsekLog.Warn("MergeDialog",
                    "ShowPreSwitchDecisionDialog: merge/discard action is null - cannot spawn dialog");
                return false;
            }

            // Read prior session metadata for the dialog body (TreeName and
            // segment duration), routed through the same shared helper as the
            // scene-exit dialog so a 12s switch segment is visually distinct
            // from a 30-minute launch. If the active scenario / session is
            // unavailable for any reason, fall back to a stub body so the
            // dialog still spawns rather than silently leaving the player
            // stranded with no decision UI.
            var scenario = ParsekScenario.Instance;
            var session = scenario != null
                ? scenario.ActiveSwitchSegmentSession
                : null;
            string priorSessionIdStr = session != null
                ? $"{session.SessionId:D}"
                : "<none>";

            // Locate the session's tree so we can render the duration body.
            // The session.TreeId may resolve to the live activeTree (rapid
            // Switch-To inside FLIGHT) or a pending stash. M3 (PR #876
            // round-5 review): now uses the shared
            // RecordingStore.TryResolveTreeById helper, the same one
            // SceneExitInterceptor.TryResolveSessionTreeForDialog uses, so
            // the two callers cannot diverge in the slot walk.
            RecordingTree priorTree = null;
            RecordingStore.TreeSlotSource priorTreeSlot =
                RecordingStore.TreeSlotSource.None;
            try
            {
                if (session != null)
                {
                    RecordingStore.TryResolveTreeById(
                        session.TreeId, out priorTree, out priorTreeSlot);
                }
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("MergeDialog",
                    $"ShowPreSwitchDecisionDialog: tree lookup threw " +
                    $"{ex.GetType().Name}: {ex.Message} — body will use a stub");
                priorTree = null;
                priorTreeSlot = RecordingStore.TreeSlotSource.None;
            }

            // M1 (PR #876 round-5 review): if the session's tree resolved
            // to the CommittedTrees slot specifically, refuse to spawn the
            // dialog. The Merge button would call MergeCommit -> the M1
            // guard there would refuse (the tree isn't in pendingTree),
            // and the player would see a dialog that does nothing. The
            // session marker probably needs cleanup separately, but that
            // is out of scope for this PR — defensive log + refuse here
            // so we don't trigger the misbehaving Merge path.
            if (priorTreeSlot == RecordingStore.TreeSlotSource.Committed)
            {
                ParsekLog.Warn("SwitchIntentPatch",
                    $"bug-c-dialog-refused-session-tree-in-committed-slot " +
                    $"priorSessionId={priorSessionIdStr} " +
                    $"treeId={(session != null ? session.TreeId ?? "<null>" : "<no-session>")} " +
                    $"newTargetPid={target.persistentId} — " +
                    "Merge would no-op via the merge-commit-tree-mismatch guard");
                return false;
            }

            string message;
            if (priorTree != null)
            {
                try
                {
                    message = BuildWholeTreeMergeDialogBody(priorTree);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("MergeDialog",
                        $"ShowPreSwitchDecisionDialog: BuildWholeTreeMergeDialogBody threw " +
                        $"{ex.GetType().Name}: {ex.Message} — using stub body");
                    message = priorTree.TreeName ?? "<unnamed>";
                }
            }
            else
            {
                message = session != null && !string.IsNullOrEmpty(session.TreeId)
                    ? $"Prior switch-segment session (tree id={session.TreeId})"
                    : "Prior switch-segment session";
            }

            const string title = "Pending switch-segment recording";

            // M2 (PR #876 round-5 review): Esc must NOT dismiss this dialog —
            // the player must commit to Merge or Discard. KSP's stock
            // PopupDialog.Update() hard-codes `Input.GetKeyUp(KeyCode.Escape)`
            // -> Dismiss() and has no `dismissOnEscape` flag, so we enforce
            // the contract from OnDismiss: when teardown fires without a
            // button click, re-spawn the same dialog so the player can't
            // sneak past it via Esc. The button handlers set the shared
            // `buttonClicked` flag before invoking the action, so OnDismiss
            // knows whether to re-spawn or just clean up.
            bool buttonClicked = false;

            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge", () =>
                {
                    buttonClicked = true;
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-merge-chosen priorSessionId={priorSessionIdStr} " +
                        $"newTargetPid={target.persistentId}");
                    ClearPendingFlag("pre-switch-dialog merge button");
                    try
                    {
                        mergeAction.Invoke();
                    }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Error("SwitchIntentPatch",
                            $"pre-switch-dialog merge action threw " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }),
                new DialogGUIButton("Discard", () =>
                {
                    buttonClicked = true;
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-discard-chosen priorSessionId={priorSessionIdStr} " +
                        $"newTargetPid={target.persistentId}");
                    ClearPendingFlag("pre-switch-dialog discard button");
                    try
                    {
                        discardAction.Invoke();
                    }
                    catch (System.Exception ex)
                    {
                        ParsekLog.Error("SwitchIntentPatch",
                            $"pre-switch-dialog discard action threw " +
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                }),
            };

            // Mirror the scene-exit dialog's lock/flag sequencing so input is
            // blocked the same way and the OnDismiss teardown clears the lock
            // even if the popup is dismissed via a non-button path.
            PopupDialog.DismissPopup(DialogName);
            LockInput();
            ParsekScenario.MergeDialogPending = true;
            // KSP's stock PopupDialog hard-codes Esc -> Dismiss() in its
            // Update() loop with no `dismissOnEscape` flag; the boolean
            // parameter on SpawnPopupDialog is `persistAcrossScenes`, not
            // dismissal control. We enforce the "no Esc dismissal" contract
            // from the OnDismiss handler below — when teardown fires without
            // a button click, we re-spawn the same dialog.
            PopupDialog popup = PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    message,
                    title,
                    HighLogic.UISkin,
                    buttons
                ),
                persistAcrossScenes: false,
                HighLogic.UISkin
            );
            if (popup != null)
            {
                popup.OnDismiss += () =>
                {
                    if (buttonClicked)
                    {
                        ClearPendingFlag("pre-switch-dialog popup teardown");
                        return;
                    }
                    // Esc / non-button teardown: re-spawn the dialog so the
                    // player still has to make a choice. M2 (PR #876 round-5
                    // review): the stealth-Cancel UX bug stayed silent because
                    // OnDismiss cleared the input lock but left the prior
                    // session armed, so the next Switch-To opened a wrong-tree
                    // dialog. Forcing Merge/Discard is the contract.
                    ParsekLog.Info("SwitchIntentPatch",
                        $"pre-switch-dialog-esc-refused-respawning " +
                        $"priorSessionId={priorSessionIdStr} newTargetPid={target.persistentId}");
                    // Recursive re-spawn through the same helper. If THAT spawn
                    // fails, the lock is released and we fall back to the
                    // defensive supersede path documented at the spawn-failed
                    // log line below. The recursion is bounded by the player's
                    // own input — every Esc press just re-opens the dialog.
                    ClearPendingFlag("pre-switch-dialog popup teardown (re-spawn)");
                    ShowPreSwitchDecisionDialog(target, mergeAction, discardAction);
                };
                ParsekLog.Info("SwitchIntentPatch",
                    $"pre-switch-dialog-opened priorSessionId={priorSessionIdStr} " +
                    $"priorFocusedPid={(session != null ? session.FocusedVesselPersistentId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<none>")} " +
                    $"newTargetPid={target.persistentId}");
                return true;
            }

            ClearPendingFlag("pre-switch-dialog spawn returned null");
            ParsekLog.Warn("SwitchIntentPatch",
                $"pre-switch-dialog-spawn-failed falling-back-to-stock-supersede " +
                $"priorSessionId={priorSessionIdStr} newTargetPid={target.persistentId}");
            return false;
        }

        /// <summary>
        /// Pre-transition button-handler core: run preCommitFinalize, then
        /// build decisions on the just-stashed pending tree, then run
        /// MergeCommit / MergeDiscard. Skip postChoice if the action
        /// refused (journal-active guard).
        /// </summary>
        private static void RunPreTransitionAction(
            bool isMerge,
            System.Action preCommitFinalize,
            System.Action postChoice)
        {
            try
            {
                preCommitFinalize?.Invoke();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("MergeDialog",
                    $"RunPreTransitionAction: preCommitFinalize threw " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return;
            }

            var pending = RecordingStore.PendingTree;
            if (pending == null)
            {
                ParsekLog.Warn("MergeDialog",
                    "RunPreTransitionAction: preCommitFinalize produced no pending tree, " +
                    "skipping commit/discard but proceeding with postChoice");
                postChoice?.Invoke();
                return;
            }

            // Re-derive Re-Fly suppressed-subtree closure on the pending tree,
            // mirroring the live-path setup in the legacy ShowTreeDialog above.
            HashSet<string> suppressedIds = null;
            string activeReFlyTargetId = null;
            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker marker =
                !object.ReferenceEquals(null, scenario)
                    ? scenario.ActiveReFlySessionMarker
                    : null;
            if (marker != null)
            {
                activeReFlyTargetId = marker.ActiveReFlyRecordingId;
                try
                {
                    var closure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
                    if (closure != null && closure.Count > 0)
                        suppressedIds = new HashSet<string>(closure, System.StringComparer.Ordinal);
                }
                catch (System.Exception ex)
                {
                    ParsekLog.Warn("MergeDialog",
                        $"RunPreTransitionAction: ComputeSessionSuppressedSubtree threw " +
                        $"{ex.GetType().Name}: {ex.Message} - falling back to leaf-only " +
                        "ghost-only decisions");
                }
            }

            var decisions = BuildDefaultVesselDecisions(
                pending, suppressedIds, activeReFlyTargetId);
            int spawnCount = 0;
            foreach (var v in decisions.Values) if (v) spawnCount++;

            bool actionCompleted;
            if (isMerge)
            {
                MergeCommit(pending, decisions, spawnCount);
                // CommitPendingTree nulls RecordingStore.PendingTree on success.
                actionCompleted = (RecordingStore.PendingTree == null);
            }
            else
            {
                actionCompleted = MergeDiscardRanToCompletion(pending);
            }

            if (!actionCompleted)
            {
                ParsekLog.Warn("MergeDialog",
                    "RunPreTransitionAction: action refused (likely merge-journal-active " +
                    "guard). Skipping postChoice; player remains in flight.");
                return;
            }

            try
            {
                postChoice?.Invoke();
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("MergeDialog",
                    $"RunPreTransitionAction: postChoice threw " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        internal static string FormatDuration(double seconds)
            => ParsekTimeFormat.FormatDuration(seconds);

        internal static string BuildTimelineActionButtonLabel(bool isPermanent)
        {
            return isPermanent ? "Merge to Timeline" : "Commit to Timeline";
        }

        internal static string BuildTimelineActionDialogTitle(bool isPermanent)
        {
            return isPermanent ? "Confirm Merge to Timeline" : "Confirm Commit to Timeline";
        }

        internal static bool DetermineReFlyTimelineActionIsPermanent(
            Recording reFlyRec,
            ReFlySessionMarker marker,
            ReFlyAutoSealPreviewResult preview,
            out string source)
        {
            bool classifierPermanent;
            string classifierReason;
            if (SupersedeCommit.TryPredictReFlyMergeIsPermanent(
                    marker,
                    reFlyRec,
                    ParsekScenario.Instance,
                    out classifierPermanent,
                    out classifierReason))
            {
                source = "classifier:" + (classifierReason ?? "<none>");
                return classifierPermanent;
            }

            source = "preview:" + (classifierReason ?? "<unavailable>");
            return preview.WillAutoSeal;
        }

        /// <summary>
        /// Composes the Re-Fly merge dialog body. Shared by the pre-
        /// transition <see cref="ShowTreeDialog"/> 4-arg overload and the
        /// deferred post-transition 1-arg overload - both paths can land
        /// in <c>TryCommitReFlySupersede</c> and auto-seal, so both must
        /// render the auto-seal warning when the preview classifies the
        /// attempt as sealable. Pure helper for unit-testability: the
        /// dialog spawn requires Unity runtime, but the body string is
        /// data-driven. Callers pass a precomputed
        /// <see cref="ReFlyAutoSealPreviewResult"/>; this method only
        /// formats it.
        /// </summary>
        internal static string BuildReFlyDialogBody(
            string vesselLabel,
            double reFlyDuration,
            ReFlyAutoSealPreviewResult preview)
        {
            string headline = $"<align=\"center\">{vesselLabel} - " +
                              $"{FormatDuration(reFlyDuration)}</align>\n\n";
            if (!preview.WillAutoSeal)
            {
                return headline +
                    "<align=\"left\">Do you want to commit this Re-Fly attempt " +
                    "to the timeline?</align>";
            }

            string reasons = BuildReFlyAutoSealReasonLines(preview);
            // Auto-seal flips MergeState to Immutable and closes the rewind
            // slot, so this branch *is* the irreversible one. Keep the
            // short translation of what "auto-seal" means in player terms
            // (the slot becomes permanent, the line of flight can no longer
            // be Re-Flown) - dropping that sentence in the original trim
            // left "auto-sealed" undefined for players unfamiliar with the
            // term. Voice stays declarative ("If not discarded, ... will be
            // ...") instead of matching the no-seal branch's question form;
            // the "If not discarded" anchor signals that the Discard button
            // is still the escape hatch and that asymmetry is intentional.
            return headline +
                "<align=\"left\"><b>If not discarded, this Re-Fly attempt " +
                $"will be merged AND auto-sealed</b> for the following " +
                $"reason(s):\n{reasons}\n\n" +
                "Auto-seal makes the slot permanent and you cannot Re-Fly this line again.</align>";
        }

        internal static string BuildReFlyAutoSealReasonLines(
            ReFlyAutoSealPreviewResult preview)
        {
            if (preview.Reasons == null || preview.Reasons.Count == 0)
            {
                ParsekLog.Warn("MergeDialog",
                    "BuildReFlyAutoSealReasonLines: auto-seal preview returned no reasons; using fallback line");
                return "- auto-seal condition met";
            }

            var lines = new List<string>(preview.Reasons.Count);
            for (int i = 0; i < preview.Reasons.Count; i++)
                lines.Add("- " + ReFlyAutoSealPreviewResult.PhraseFor(preview.Reasons[i]));
            return string.Join("\n", lines);
        }

        private static string FormatClearReason(string reason)
            => string.IsNullOrEmpty(reason) ? "<unspecified>" : reason;

        /// <summary>
        /// Bug 3 (post-#876 playtest 2026-05-17): unified body for the
        /// whole-tree pre/post-transition merge dialog. Both switch-segment
        /// (short standalone segment) and long-lived launch trees render the
        /// same wording: "{TreeName} - {Duration}". The player tells short
        /// segments apart from whole recordings by reading the duration.
        /// Previously a separate <c>BuildSwitchSegmentDialogBody</c> emitted
        /// entry-reason-aware copy ("Keep your switch into ..." / "Keep your
        /// new flight on ..."), but the playtest report identified that
        /// bespoke copy as confusing — the duration line is the load-bearing
        /// distinguisher.
        ///
        /// <para>Bug 6 follow-up (post-#876 playtest 2026-05-17, route=
        /// committed-spawned-clone case): when an
        /// <see cref="SwitchSegmentSession"/> is armed AND the dialog's tree
        /// owns the session's <c>ActiveSegmentRecordingId</c>, the duration
        /// shown is the SEGMENT recording's elapsed time
        /// (<c>EndUT - StartUT</c>), NOT the whole tree's span. Without this
        /// the dialog after a committed-clone switch/Fly auto-record shows
        /// the launch-to-present mission time (e.g. "Kerbal X - 28m") even
        /// though the pending work is the ~10-second post-switch segment,
        /// because the segment recording lives inside the committed-clone
        /// tree alongside ~15 pre-existing recordings. Player mental model:
        /// "how long did I fly after the switch" — child-debris-recording
        /// windows after explosions are intentionally NOT included.</para>
        ///
        /// <para>Defensive fallbacks:</para>
        /// <list type="bullet">
        /// <item>session armed but <c>ActiveSegmentRecordingId</c> is null
        ///     or not in this tree → tree-wide duration (a different tree
        ///     is being merged than the one carrying the segment; no warn).</item>
        /// <item>segment has <c>EndUT &lt; StartUT</c> (malformed bounds) →
        ///     tree-wide duration + <c>[SwitchSegment]</c> Warn log.</item>
        /// </list>
        /// </summary>
        internal static string BuildWholeTreeMergeDialogBody(RecordingTree tree)
        {
            string treeName = tree?.TreeName ?? "<unnamed>";
            // L2 (PR #876 final review): ParsekScenario.Instance is a Unity-static
            // touch point — its static initializer can race during scene load
            // and throw a TypeInitializationException (or a downstream null
            // chase). Wrap the dispatch so a broken scenario state cannot
            // crash the dialog. Falling back to the tree-wide duration is the
            // safe whole-launch reading; the [SwitchSegment] Warn surfaces the
            // race in KSP.log so a real bug can still be diagnosed.
            double duration;
            try
            {
                duration = ResolveDialogBodyDuration(tree);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("SwitchSegment",
                    $"BuildWholeTreeMergeDialogBody: dialog-body-static-exception " +
                    $"exType={ex.GetType().Name} " +
                    $"message={ex.Message ?? "<null>"} — falling back to tree-wide duration");
                duration = ComputeTreeDurationRange(tree);
            }
            return $"{treeName} - {FormatDuration(duration)}";
        }

        /// <summary>
        /// Test seam: override the current Planetarium UT used by the
        /// segment-aware duration path's live-recording fallback. Production
        /// code leaves this null and the helper falls back to
        /// <c>Planetarium.GetUniversalTime()</c>; unit tests inject a fixed
        /// UT so the live-recording branch is exercisable without a Unity
        /// runtime.
        /// </summary>
        internal static System.Func<double> NowUtProviderForTesting;

        /// <summary>Clears all test seams in MergeDialog.</summary>
        internal static void ResetTestOverrides()
        {
            NowUtProviderForTesting = null;
        }

        private static double CurrentUtForSegmentDuration()
        {
            var hook = NowUtProviderForTesting;
            if (hook != null)
            {
                try { return hook(); }
                catch { /* fall through to Planetarium */ }
            }
            try { return Planetarium.GetUniversalTime(); }
            catch
            {
                // No Planetarium singleton (unit test without injected hook).
                // Returning NaN forces the segment-aware branch to fall back
                // to the persisted EndUT - StartUT computation instead of
                // synthesizing a nonsense live duration.
                return double.NaN;
            }
        }

        /// <summary>
        /// Picks the duration to render in the merge dialog body. When an
        /// active switch-segment session targets a recording inside
        /// <paramref name="tree"/>, prefers the segment recording's elapsed
        /// time so the dialog distinguishes a ~10s post-switch segment from
        /// the surrounding committed-clone tree's full mission duration.
        /// Falls back to <see cref="ComputeTreeDurationRange"/> on:
        /// no session armed, session missing the active segment id, segment
        /// not present in this tree, or malformed segment bounds.
        ///
        /// <para>Live-recording fallback (Bug A, post-#876 playtest
        /// 2026-05-17): a still-live segment that has only sampled its
        /// initial point has <c>EndUT == StartUT</c> (or a stale
        /// <c>ExplicitEndUT</c> behind <c>StartUT</c>), so
        /// <c>EndUT - StartUT</c> rounds to zero even after ~40 seconds of
        /// real flight. The dialog renders "0s" in that window. When the
        /// segment looks live (<c>EndUT &lt;= StartUT</c>) AND the current
        /// Planetarium UT is past <c>StartUT</c>, fall back to
        /// <c>currentUT - StartUT</c>. Clamps non-finite or negative results
        /// to <c>0</c> so a UT regression (rewind-to-staging crossing the
        /// segment, etc.) cannot leak a nonsense duration into the UI.</para>
        /// </summary>
        internal static double ResolveDialogBodyDuration(RecordingTree tree)
        {
            var scenario = ParsekScenario.Instance;
            var session = !object.ReferenceEquals(null, scenario)
                ? scenario.ActiveSwitchSegmentSession
                : null;
            if (session == null
                || string.IsNullOrEmpty(session.ActiveSegmentRecordingId)
                || tree?.Recordings == null)
            {
                return ComputeTreeDurationRange(tree);
            }

            Recording segment;
            if (!tree.Recordings.TryGetValue(
                    session.ActiveSegmentRecordingId, out segment)
                || segment == null)
            {
                // Different tree than the one carrying the segment: legitimate
                // case (dialog is for tree B but session targets tree A), no
                // warning needed. Fall back to tree-wide duration.
                return ComputeTreeDurationRange(tree);
            }

            double startUT = segment.StartUT;
            double endUT = segment.EndUT;

            // Bug A live-recording fallback (post-#876 playtest 2026-05-17):
            // a recording that has only sampled its initial point — or that
            // is bound to the live recorder but has not yet been finalized —
            // has EndUT <= StartUT. Prefer currentUT - startUT in that case
            // so the dialog reflects the wall-clock elapsed segment time
            // instead of rounding to 0s. This intentionally takes precedence
            // over the malformed-segment-bounds warn below: a live-but-still
            // sampling segment is the expected steady-state shape, not a
            // bug, and emitting a Warn every dialog open would be noise.
            if (endUT <= startUT)
            {
                double currentUT = CurrentUtForSegmentDuration();
                if (!double.IsNaN(currentUT)
                    && !double.IsInfinity(currentUT)
                    && currentUT > startUT)
                {
                    double liveDuration = currentUT - startUT;
                    if (liveDuration < 0.0
                        || double.IsNaN(liveDuration)
                        || double.IsInfinity(liveDuration))
                    {
                        liveDuration = 0.0;
                    }
                    ParsekLog.Verbose("SwitchSegment",
                        $"BuildWholeTreeMergeDialogBody: using live segment duration " +
                        $"recId={segment.RecordingId ?? "<null>"} " +
                        $"durationSec={liveDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"startUT={startUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"currentUT={currentUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"sessionId={session.SessionId:D} treeId={tree.Id ?? "<null>"}");
                    return liveDuration;
                }
                if (endUT < startUT)
                {
                    // currentUT unavailable (no Planetarium / regression
                    // edge) and the bounds are malformed: fall back to
                    // tree-wide duration and Warn so a real bug surfaces.
                    ParsekLog.Warn("SwitchSegment",
                        $"BuildWholeTreeMergeDialogBody: malformed-segment-bounds " +
                        $"recId={segment.RecordingId ?? "<null>"} " +
                        $"startUT={startUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"endUT={endUT.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                        $"sessionId={session.SessionId:D} — falling back to tree-wide duration");
                    return ComputeTreeDurationRange(tree);
                }
                // endUT == startUT and no live clock — return 0 explicitly
                // through the normal branch below; the Verbose log keeps the
                // log shape consistent for the segment-aware path.
            }

            double segmentDuration = endUT - startUT;
            if (segmentDuration < 0.0
                || double.IsNaN(segmentDuration)
                || double.IsInfinity(segmentDuration))
            {
                segmentDuration = 0.0;
            }
            ParsekLog.Verbose("SwitchSegment",
                $"BuildWholeTreeMergeDialogBody: using segment duration " +
                $"recId={segment.RecordingId ?? "<null>"} " +
                $"durationSec={segmentDuration.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"sessionId={session.SessionId:D} treeId={tree.Id ?? "<null>"}");
            return segmentDuration;
        }

        /// <summary>
        /// Locate the recording the active re-fly session targets. Tries the
        /// pending tree first (so the lookup works whether the dialog fires
        /// before or after `RecordingStore.CommitPendingTree`), then falls
        /// back to the committed recordings list. Returns null if neither
        /// source has the recording — the caller falls back to whole-tree
        /// metadata.
        /// </summary>
        internal static Recording FindReFlyRecording(
            ReFlySessionMarker marker, RecordingTree pendingTree)
        {
            if (marker == null) return null;
            string targetId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(targetId)) return null;

            if (pendingTree != null && pendingTree.Recordings != null
                && pendingTree.Recordings.TryGetValue(targetId, out Recording fromTree)
                && fromTree != null)
                return fromTree;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, targetId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        // ================================================================
        // Tree commit / discard — extracted from the dialog button lambdas
        // so that unit tests can exercise the post-button work directly
        // (the lambda itself isn't reachable from outside Unity).
        //
        // Phase C of the ledger / lump-sum reconciliation fix
        // (`docs/dev/done/plans/fix-ledger-lump-sum-reconciliation.md`) wires
        // <see cref="RecordingStore.MarkTreeAsApplied"/> into the merge path
        // so committed tree recordings are immediately marked fully applied
        // (the in-flight commit path <see cref="ParsekFlight.CommitTreeFlight"/>
        // already does the equivalent inline; this brings the merge dialog
        // into parity).
        //
        // Discard intentionally does NOT mark applied: the tree is being
        // wiped from storage, so there is no surviving caller that needs
        // its recording indexes advanced.
        // ================================================================

        /// <summary>
        /// Implements the "Merge to Timeline" branch of the dialog.
        /// </summary>
        internal static void MergeCommit(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            int spawnCount)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeCommit: tree is null — nothing to commit");
                return;
            }

            // M1 (PR #876 round-5 review): RecordingStore.CommitPendingTree
            // operates on the global pendingTree slot. If a caller routes a
            // session-resolved tree from CommittedTrees (Bug-C dialog path),
            // CommitPendingTree would no-op on null/mismatch and the dialog
            // would disappear with nothing committed. Refuse the commit when
            // the passed tree isn't the one CommitPendingTree will act on, so
            // we never silently misbehave.
            var pendingForGuard = RecordingStore.PendingTree;
            if (pendingForGuard == null
                || !object.ReferenceEquals(pendingForGuard, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"merge-commit-tree-mismatch " +
                    $"passedTreeId={tree.Id ?? "<null>"} " +
                    $"pendingTreeId={pendingForGuard?.Id ?? "<null>"} — " +
                    "refusing commit (CommitPendingTree would no-op or commit the wrong tree)");
                ClearPendingFlag("merge-commit-tree-mismatch refused");
                return;
            }

            var scenarioForAdoption = ParsekScenario.Instance;
            string activeReFlyTargetId =
                !object.ReferenceEquals(null, scenarioForAdoption)
                    ? scenarioForAdoption.ActiveReFlySessionMarker?.ActiveReFlyRecordingId
                    : null;
            var activeReFlyTargetHint =
                CaptureOptimizationSurvivorHint(tree, activeReFlyTargetId);

            ApplyVesselDecisions(tree, decisions);
            // Collect after ApplyVesselDecisions so candidates reflect the actual
            // post-decision snapshot state, but before optimization renames/splits/merges tips.
            var retainedParentChainTipCandidates =
                CollectRetainedParentChainTipAdoptionCandidates(
                    tree, decisions, activeReFlyTargetId);
            RecordingStore.CommitPendingTree();
            // Phase C/F: mark recordings fully applied after the tree moves
            // from pending to committed state.
            RecordingStore.MarkTreeAsApplied(tree);
            RecordingStore.RunOptimizationPass();
            AdoptExistingSourceVesselsForRetainedParentChainTips(
                tree,
                decisions,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            CrewReservationManager.SwapReservedCrewInFlight();

            // Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a
            // re-fly session is active, write the supersede relations for the
            // origin subtree and flip the provisional's MergeState AFTER the
            // tree commits (so the provisional has moved from pending-tree
            // storage into the committed list) and BEFORE firing
            // OnTreeCommitted (so downstream chain evaluators see the
            // superseded subtree hidden from ERS).
            ReFlyMergeCommitResult reFlyResult =
                TryCommitReFlySupersede(activeReFlyTargetHint);

            // #292 + rewind-staging follow-up: refresh quicksave only after the
            // re-fly staged commit has either completed or been bypassed.
            // Interrupted re-fly commits intentionally skip the refresh so F9
            // cannot resurrect a half-committed session from a stale snapshot.
            if (reFlyResult != ReFlyMergeCommitResult.Interrupted)
            {
                RecordingStore.RefreshQuicksaveAfterMerge(
                    "merge dialog Tree Merge", tree.Recordings.Count);
            }

            // Switch/Fly segment merge hook: when an active session is armed
            // and the commit succeeded, clear the marker (plan §"Merge and
            // Discard Scope": "On Merge, commit the pending tree normally
            // and clear the switch marker only after the commit succeeds.").
            // If a committed-tree restore attempt is also armed, clear it
            // too — the commit promoted the clone to the new committed tree.
            var switchSegmentScenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, switchSegmentScenario)
                && switchSegmentScenario.ActiveSwitchSegmentSession != null)
            {
                switchSegmentScenario.ClearSwitchSegmentSession("scoped-merge-success");
                if (RecordingStore.HasCommittedTreeRestoreAttempt)
                {
                    RecordingStore.ClearCommittedTreeRestoreAttempt(
                        "scoped-merge-success switch-segment");
                }
            }

            ClearPendingFlag("merge dialog commit button");
            OnTreeCommitted?.Invoke();
            if (spawnCount > 0)
                ParsekLog.ScreenMessage(
                    $"Merged - {spawnCount} vessel(s) will appear after ghost playback", 3f);
            else
                ParsekLog.ScreenMessage(
                    "Merged to timeline (no surviving vessels)", 3f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Merge (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count}, spawnable={spawnCount})");
        }

        /// <summary>
        /// Implements the "Discard" branch of the dialog. Does NOT call
        /// <see cref="RecordingStore.MarkTreeAsApplied"/>: the tree is
        /// removed from storage by <see cref="RecordingStore.DiscardPendingTree"/>
        /// so there is no surviving caller that needs recording indexes advanced.
        ///
        /// <para>Refuses with a Warn log + screen message if a merge journal
        /// is active (<see cref="ParsekScenario.ActiveMergeJournal"/>) -
        /// mirrors <see cref="RevertInterceptor.DiscardReFlyHandler"/>'s
        /// guard so a discard mid-merge does not race the journal
        /// finisher's rollback.</para>
        /// </summary>
        internal static void MergeDiscard(RecordingTree tree)
        {
            MergeDiscardRanToCompletion(tree);
        }

        /// <summary>
        /// <see cref="MergeDiscard"/> variant that returns a bool: true when
        /// the discard ran end-to-end and the caller may proceed with any
        /// scene-transition continuation, false when the merge-journal-active
        /// guard refused. Bug 2 (post-#876 playtest 2026-05-17) collapsed the
        /// tri-state outcome to bool — the secondary-dialog deferral case is
        /// gone because the topology-based scoped Discard now sweeps the full
        /// segment subtree and never needs a follow-up whole-tree prompt.
        /// </summary>
        /// <param name="tree">The pending tree to discard.</param>
        internal static bool MergeDiscardRanToCompletion(RecordingTree tree)
        {
            if (tree == null)
            {
                ParsekLog.Warn("MergeDialog", "MergeDiscard: tree is null - nothing to discard");
                return false;
            }

            var scenario = ParsekScenario.Instance;
            if (!object.ReferenceEquals(null, scenario) && scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"MergeDiscard: refusing - merge journal active " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage("Discard: merge in progress - retry in a moment", 3f);
                return false;
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec.VesselSnapshot != null)
                    CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
            }

            if (TryDiscardActiveReFlyAttempt(tree))
                return true;

            // Switch/Fly segment scoped Discard (plan §"Merge and Discard Scope").
            // Mirrors the ReFly placement: try scoped first, then fall back to
            // whole-pending-tree discard ONLY when no session was armed.
            //
            // Bug 2 (post-#876 playtest 2026-05-17): scoped Discard now sweeps
            // the topological subtree rooted at the segment recording
            // (descendants regardless of SwitchSegmentSessionId stamp). The
            // old "secondary whole-pending-tree dialog when pre-existing
            // changes remain" flow has been deleted — the broader sweep makes
            // it unnecessary in practice, and the second dialog was
            // confusing in playtest (e.g. an orphan debris recording from a
            // Breakup-during-segment falsely triggered the prompt).
            string switchSegmentReason;
            var switchSegmentDisposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(
                    out switchSegmentReason);
            if (switchSegmentDisposition
                != RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession)
            {
                ParsekLog.Info("MergeDialog",
                    $"MergeDiscard: scoped switch-segment discard succeeded " +
                    $"disposition={switchSegmentDisposition} reason={switchSegmentReason}");
                ClearPendingFlag("merge dialog switch-segment scoped discard");
                ParsekLog.ScreenMessage("Switch segment discarded", 2f);
                return true;
            }

            // M1 (PR #876 round-5 review): the whole-pending-tree branch
            // below reads RecordingStore.PendingTree as its canonical input
            // (DiscardPendingTreeAndRecalculate, RefreshSaveAndQuicksaveAfterDiscard
            // and friends). A session-resolved tree from the CommittedTrees
            // slot would diverge here and we'd discard the wrong tree (or
            // no-op, leaving the session live). The earlier Re-Fly and
            // switch-segment scoped branches already had their chance — at
            // this point a tree != pending is genuinely the misrouted Bug-C
            // case the round-5 review flagged. Refuse with Warn.
            var pendingForGuard = RecordingStore.PendingTree;
            if (pendingForGuard == null
                || !object.ReferenceEquals(pendingForGuard, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"merge-discard-tree-mismatch " +
                    $"passedTreeId={tree.Id ?? "<null>"} " +
                    $"pendingTreeId={pendingForGuard?.Id ?? "<null>"} — " +
                    "refusing whole-pending-tree discard (Re-Fly / switch-segment scoped branches already declined)");
                ClearPendingFlag("merge-discard-tree-mismatch refused");
                return false;
            }

            bool refreshSerializedPendingMarker =
                RecordingStore.HasPendingTree
                && RecordingStore.PendingTreeSerializedForSave;
            int discardedRecordingCount = tree.Recordings?.Count ?? 0;

            // #466: while the merge/discard choice is pending, mid-flight effects stay live
            // in KSP and patching is deferred. Discard must now rebuild from the committed
            // ledger immediately after the pending tree is removed.
            ParsekScenario.DiscardPendingTreeAndRecalculate("merge dialog discard");
            if (refreshSerializedPendingMarker)
            {
                RecordingStore.RefreshSaveAndQuicksaveAfterDiscard(
                    "merge dialog Tree Discard", discardedRecordingCount);
            }
            else
            {
                ParsekLog.Verbose("MergeDialog",
                    "MergeDiscard: save refresh skipped because pending tree " +
                    "was not marked as serialized");
            }
            ClearPendingFlag("merge dialog discard button");
            ParsekLog.ScreenMessage("Recording discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Tree Discard (tree='{tree.TreeName}', " +
                $"recordings={tree.Recordings.Count})");
            return true;
        }

        /// <summary>
        /// Removes the active Re-Fly attempt's recordings, branch-point
        /// topology (session-authored branch points + dangling parent/child
        /// id refs), sidecar files, transient marker fields, and any stale
        /// <c>tree.ActiveRecordingId</c> from in-memory and on-disk storage.
        /// Shared between Discard call sites - the post-transition merge
        /// dialog (<see cref="TryDiscardActiveReFlyAttempt"/>) and the
        /// Esc/Revert dialog (<see cref="RevertInterceptor.DiscardReFlyHandler"/>)
        /// - so both paths converge on the same attempt-id collection,
        /// pruning, and topology cleanup. Pre-PR-#734 the Revert path only
        /// removed the fork from <see cref="RecordingStore.CommittedRecordings"/>,
        /// leaving the committed-tree fork dictionary entry, attempt-authored
        /// debris children created by <c>CreateBreakupChildRecording</c>,
        /// session-authored branch points, dangling parent/child id refs,
        /// and stale <c>ActiveRecordingId</c> behind for OnSave to serialise
        /// as committed mission history.
        /// Caller is responsible for clearing the marker / scenario journal
        /// and any flow-specific cleanup; this helper handles only the
        /// recording/topology side.
        /// </summary>
        internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology(
            ReFlySessionMarker marker, string callSite)
        {
            var summary = new AttemptDiscardSummary();
            if (marker == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"PruneActiveReFlyAttemptOwnedTopology: null marker " +
                    $"callSite={callSite ?? "<none>"} - nothing to prune");
                return summary;
            }

            var tree = RewindInvoker.FindTreeForReFlyFork(marker.TreeId);
            if (tree == null)
            {
                // Degenerate case: marker points at a tree id that no
                // longer lives in PendingTree, CommittedTrees, or the
                // live activeTree. Fall back to the legacy single-id fork
                // removal so the flat committed list is at least cleaned.
                // Topology pruning is impossible without a tree handle -
                // future Re-Fly on this id would have to discover the
                // missing context another way.
                if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId))
                {
                    var single = new HashSet<string>(System.StringComparer.Ordinal)
                    {
                        marker.ActiveReFlyRecordingId,
                    };
                    summary.AttemptIds = single;
                    summary.RemovedCommitted = RemoveCommittedAttemptRecordings(single);
                }
                ParsekLog.Warn("MergeDialog",
                    $"PruneActiveReFlyAttemptOwnedTopology: no in-memory tree found " +
                    $"for treeId={marker.TreeId ?? "<none>"} callSite={callSite ?? "<none>"} " +
                    $"sess={marker.SessionId ?? "<none>"} - falling back to single-id removal " +
                    $"(lost descendant + topology cleanup; removedCommitted={summary.RemovedCommitted})");
                return summary;
            }

            summary.AttemptIds = CollectReFlyAttemptOwnedRecordingIds(tree, marker);
            summary.RemovedCommitted = RemoveCommittedAttemptRecordings(summary.AttemptIds);
            summary.PurgedEvents = GameStateStore.PurgeEventsForRecordings(
                summary.AttemptIds,
                $"{callSite ?? "PruneActiveReFlyAttemptOwnedTopology"} sess={marker.SessionId ?? "<no-id>"}");
            summary.DeletedFiles = DeleteAttemptRecordingFiles(tree, summary.AttemptIds);
            summary.PrunedCommittedTreeEntries = PruneAttemptRecordingsFromCommittedTrees(
                summary.AttemptIds, marker);
            summary.TransientCleared = ClearReFlyAttemptTransientFields(
                tree, marker, summary.AttemptIds);

            ParsekLog.Info("MergeDialog",
                $"PruneActiveReFlyAttemptOwnedTopology callSite={callSite ?? "<none>"} " +
                $"sess={marker.SessionId ?? "<none>"} " +
                $"tree={tree.Id ?? "<none>"} " +
                $"attemptIds={summary.AttemptIds.Count} " +
                $"removedCommitted={summary.RemovedCommitted} " +
                $"purgedEvents={summary.PurgedEvents} " +
                $"deletedFiles={summary.DeletedFiles} " +
                $"prunedCommittedTreeEntries={summary.PrunedCommittedTreeEntries} " +
                $"transientCleared={summary.TransientCleared}");

            return summary;
        }

        /// <summary>
        /// Per-call counters from <see cref="PruneActiveReFlyAttemptOwnedTopology"/>.
        /// Callers use the counts in their own end-of-flow summary log lines
        /// and tests assert on them directly.
        /// </summary>
        internal struct AttemptDiscardSummary
        {
            internal HashSet<string> AttemptIds;
            internal int RemovedCommitted;
            internal int PurgedEvents;
            internal int DeletedFiles;
            internal int PrunedCommittedTreeEntries;
            internal int TransientCleared;
        }

        /// <summary>
        /// Re-Fly-specific Discard branch for the scene-exit merge dialog.
        /// A Re-Fly pending tree often reuses the original committed tree id;
        /// the ordinary tree-discard path would route through
        /// <see cref="TreeDiscardPurge.PurgeTree"/> and purge the mission's
        /// persistent rewind/supersede/tombstone state. This path abandons
        /// only the active session attempt and leaves the committed mission
        /// timeline intact.
        /// </summary>
        internal static bool TryDiscardActiveReFlyAttempt(RecordingTree tree)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                return false;

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
                return false;

            // Defensive belt-and-braces guard for any caller that bypasses
            // MergeDiscard's gate (test seam, future call site). Mirrors
            // RevertInterceptor.DiscardReFlyHandler's guard at
            // RevertInterceptor.cs:345-352.
            if (scenario.ActiveMergeJournal != null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: refusing - merge journal active " +
                    $"sess={marker.SessionId ?? "<no-id>"} " +
                    $"journal={scenario.ActiveMergeJournal.JournalId ?? "<no-id>"}");
                ParsekLog.ScreenMessage("Discard Re-Fly: merge in progress - retry in a moment", 3f);
                return false;
            }

            if (!IsReFlyMarkerScopedToTree(marker, tree))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: active marker sess={marker.SessionId ?? "<no-id>"} " +
                    $"tree={marker.TreeId ?? "<none>"} does not match dialog tree={tree?.Id ?? "<none>"} - " +
                    "falling back to regular tree discard");
                return false;
            }

            string sessionId = marker.SessionId;
            var attemptIds = CollectReFlyAttemptOwnedRecordingIds(tree, marker);

            int removedCommitted = RemoveCommittedAttemptRecordings(attemptIds);
            int purgedEvents = GameStateStore.PurgeEventsForRecordings(
                attemptIds,
                $"MergeDialog Re-Fly discard sess={sessionId ?? "<no-id>"}");
            int deletedFiles = DeleteAttemptRecordingFiles(tree, attemptIds);
            int prunedCommittedTreeEntries = PruneAttemptRecordingsFromCommittedTrees(
                attemptIds, marker);
            int transientCleared = ClearReFlyAttemptTransientFields(tree, marker, attemptIds);
            bool committedTreeDetached = !CommittedTreeExists(tree.Id);
            bool rpPromoted = PromoteOriginRewindPointForDiscard(scenario, marker);
            int discardedSessionRps = PurgeDiscardedSessionRewindPoints(scenario, marker);
            bool restoredCommittedTree = committedTreeDetached
                && RestoreSanitizedPendingTreeIfDetached(tree, marker, attemptIds);

            RecordingStore.PopPendingTree();
            GameStateRecorder.PendingScienceSubjects.Clear();
            RecordingStore.ClearRewindReplayTargetScope();

            scenario.ActiveReFlySessionMarker = null;
            Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
            scenario.ActiveMergeJournal = null;
            scenario.BumpSupersedeStateVersion();
            ReFlyRevertButtonGate.Apply("MergeDialog:discard-refly-attempt");
            SupersedeCommit.ClearPreReFlyAnchorSnapshotsForSession(sessionId);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(
                ParsekScenario.GetCurrentTimelineUTForLedgerRecalc(),
                "merge-dialog-discard-refly");
            ClearPendingFlag();
            bool durableSaved = SaveDiscardedReFlyStateDurably(sessionId);

            ParsekLog.ScreenMessage("Re-Fly attempt discarded", 2f);
            ParsekLog.Info("MergeDialog",
                $"User chose: Re-Fly Attempt Discard (tree='{tree.TreeName}', " +
                $"treeId={tree.Id ?? "<none>"}, sess={sessionId ?? "<no-id>"}, " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}, " +
                $"active={marker.ActiveReFlyRecordingId ?? "<none>"}, " +
                $"attemptIds={attemptIds.Count}, removedCommitted={removedCommitted}, " +
                $"purgedEvents={purgedEvents}, deletedFiles={deletedFiles}, " +
                $"prunedCommittedTreeEntries={prunedCommittedTreeEntries}, " +
                $"transientCleared={transientCleared}, " +
                $"rpPromoted={rpPromoted}, discardedSessionRps={discardedSessionRps}, " +
                $"restoredCommittedTree={restoredCommittedTree}, durableSaved={durableSaved})");
            ParsekLog.Info("ReFlySession",
                $"End reason=discardReFlyAttemptFromMergeDialog sess={sessionId ?? "<no-id>"} " +
                $"tree={tree.Id ?? "<none>"} active={marker.ActiveReFlyRecordingId ?? "<none>"} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");
            return true;
        }

        private static bool IsReFlyMarkerScopedToTree(
            ReFlySessionMarker marker, RecordingTree tree)
        {
            if (marker == null || tree == null)
                return false;
            if (string.IsNullOrEmpty(marker.TreeId))
                return true;
            return string.Equals(marker.TreeId, tree.Id, System.StringComparison.Ordinal);
        }

        private static HashSet<string> CollectReFlyAttemptOwnedRecordingIds(
            RecordingTree tree, ReFlySessionMarker marker)
        {
            var ids = new HashSet<string>(System.StringComparer.Ordinal);
            if (marker == null)
                return ids;

            AddAttemptIdIfSafe(ids, marker.ActiveReFlyRecordingId, marker, tree);

            if (tree == null || tree.Recordings == null)
                return ids;

            foreach (var rec in tree.Recordings.Values)
            {
                if (!IsReFlyAttemptOwnedRecording(rec, marker))
                    continue;
                AddAttemptIdIfSafe(ids, rec.RecordingId, marker, tree);
            }

            // Catch attempt-authored child recordings linked to session-authored
            // branch points. Production helpers like ParsekFlight.CreateBreakupChildRecording
            // do NOT propagate the marker's SessionId / RewindPointId /
            // SupersedeTargetId onto the new child Recording - they only
            // append the new id to bp.ChildRecordingIds. As a result the
            // marker-tag scan above misses them, so without this descendant
            // walk Discard would leave abandoned debris/children in
            // tree.Recordings (and on next save in CommittedRecordings) for
            // OnSave to serialise as committed mission history. The walk is
            // a single linear pass because chained breakups (child A spawns
            // its own breakup BP_B with grand-child B) produce ANOTHER
            // session-authored branch point, which the same loop visits.
            AddSessionBranchPointDescendantAttemptIds(ids, tree, marker);

            return ids;
        }

        private static void AddSessionBranchPointDescendantAttemptIds(
            HashSet<string> ids, RecordingTree tree, ReFlySessionMarker marker)
        {
            if (ids == null || tree == null || tree.BranchPoints == null
                || marker == null)
            {
                return;
            }

            // A null PreSessionBranchPointIds baseline means the marker was
            // written by code that does not snapshot branch-point ids - we
            // cannot tell session-authored from pre-session BPs, so skip
            // (mirrors PruneSessionCreatedBranchPoints' null-baseline skip).
            if (marker.PreSessionBranchPointIds == null)
                return;

            var preSessionBpIds = new HashSet<string>(
                marker.PreSessionBranchPointIds,
                System.StringComparer.Ordinal);

            int adopted = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id))
                    continue;
                if (preSessionBpIds.Contains(bp.Id))
                    continue;
                if (bp.ChildRecordingIds == null)
                    continue;

                for (int j = 0; j < bp.ChildRecordingIds.Count; j++)
                {
                    string childId = bp.ChildRecordingIds[j];
                    if (string.IsNullOrEmpty(childId))
                        continue;
                    if (ids.Contains(childId))
                        continue;
                    // Defence in depth: the protected origin/supersede-target
                    // ids should never appear as a session-authored BP child
                    // in production (origin pre-existed the attempt; supersede
                    // targets are pre-existing too), but skip them anyway.
                    if (IsProtectedReFlyRecordingId(childId, marker))
                        continue;

                    ids.Add(childId);
                    adopted++;
                }
            }

            if (adopted > 0)
            {
                ParsekLog.Info("MergeDialog",
                    $"AddSessionBranchPointDescendantAttemptIds: " +
                    $"adopted {adopted} attempt-authored child recording(s) " +
                    $"linked to session-authored branch points " +
                    $"(tree={tree.Id ?? "<none>"}, sess={marker.SessionId ?? "<none>"})");
            }
        }

        private static bool IsReFlyAttemptOwnedRecording(
            Recording rec, ReFlySessionMarker marker)
        {
            if (rec == null || marker == null || string.IsNullOrEmpty(rec.RecordingId))
                return false;

            if (!string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && string.Equals(rec.RecordingId,
                    marker.ActiveReFlyRecordingId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.SessionId)
                && string.Equals(rec.CreatingSessionId,
                    marker.SessionId, System.StringComparison.Ordinal))
                return true;

            if (!string.IsNullOrEmpty(marker.RewindPointId)
                && string.Equals(rec.ProvisionalForRpId,
                    marker.RewindPointId, System.StringComparison.Ordinal))
                return true;

            if (rec.MergeState == MergeState.NotCommitted
                && !string.IsNullOrEmpty(rec.SupersedeTargetId))
            {
                if (string.Equals(rec.SupersedeTargetId,
                        marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                    return true;
                if (string.Equals(rec.SupersedeTargetId,
                        marker.SupersedeTargetId, System.StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static void AddAttemptIdIfSafe(
            HashSet<string> ids,
            string recordingId,
            ReFlySessionMarker marker,
            RecordingTree dialogTree)
        {
            if (ids == null || string.IsNullOrEmpty(recordingId))
                return;

            if (IsProtectedReFlyRecordingId(recordingId, marker))
            {
                ParsekLog.Verbose("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: protected original rec={recordingId} " +
                    "excluded from attempt discard");
                return;
            }

            if (CommittedTreeContainsRecording(dialogTree?.Id, recordingId))
            {
                // AtomicMarkerWrite attaches the in-place fork to whichever
                // tree owns origin's tree id (pending OR committed). When no
                // pending tree exists at marker-write time, the fork is
                // attached to the committed tree as a NotCommitted recording.
                // That fork is the active Re-Fly attempt itself, not committed
                // mission history, so the guard must let it through; the
                // tree-pruning step in TryDiscardActiveReFlyAttempt removes
                // the entry from the committed tree's Recordings dictionary
                // so OnSave does not serialise the fork as history.
                if (IsMarkerOwnedNotCommittedFork(recordingId, marker))
                {
                    ParsekLog.Info("MergeDialog",
                        $"TryDiscardActiveReFlyAttempt: rec={recordingId} is the marker-owned " +
                        $"NotCommitted Re-Fly fork attached to committed tree " +
                        $"{dialogTree?.Id ?? "<none>"}; including in attempt discard");
                    ids.Add(recordingId);
                    return;
                }

                ParsekLog.Warn("MergeDialog",
                    $"TryDiscardActiveReFlyAttempt: rec={recordingId} exists in committed tree " +
                    $"{dialogTree?.Id ?? "<none>"} - excluding from attempt discard to protect mission history");
                return;
            }

            ids.Add(recordingId);
        }

        private static bool IsMarkerOwnedNotCommittedFork(
            string recordingId, ReFlySessionMarker marker)
        {
            if (string.IsNullOrEmpty(recordingId) || marker == null)
                return false;
            var rec = LookupCommittedRecordingById(recordingId);
            if (rec == null || rec.MergeState != MergeState.NotCommitted)
                return false;
            return IsReFlyAttemptOwnedRecording(rec, marker);
        }

        private static Recording LookupCommittedRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec != null
                    && string.Equals(rec.RecordingId, recordingId,
                        System.StringComparison.Ordinal))
                {
                    return rec;
                }
            }
            return null;
        }

        private static bool IsProtectedReFlyRecordingId(
            string recordingId, ReFlySessionMarker marker)
        {
            if (string.IsNullOrEmpty(recordingId) || marker == null)
                return true;
            if (string.Equals(recordingId,
                    marker.OriginChildRecordingId, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(recordingId,
                    marker.SupersedeTargetId, System.StringComparison.Ordinal))
                return true;
            return false;
        }

        private static bool CommittedTreeContainsRecording(
            string treeId, string recordingId)
        {
            if (string.IsNullOrEmpty(treeId) || string.IsNullOrEmpty(recordingId))
                return false;

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null || committedTree.Recordings == null)
                    continue;
                if (!string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    continue;
                if (committedTree.Recordings.ContainsKey(recordingId))
                    return true;
            }

            return false;
        }

        private static int RemoveCommittedAttemptRecordings(HashSet<string> attemptIds)
        {
            if (attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return 0;

            for (int i = committed.Count - 1; i >= 0; i--)
            {
                var rec = committed[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;

                if (RecordingStore.RemoveCommittedInternal(rec))
                {
                    removed++;
                    ParsekLog.Info("RecordingStore",
                        $"Removed Re-Fly attempt rec={rec.RecordingId} during merge-dialog discard");
                }
            }

            return removed;
        }

        private static int DeleteAttemptRecordingFiles(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int deleted = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                    continue;
                if (!attemptIds.Contains(rec.RecordingId))
                    continue;
                RecordingStore.DeleteRecordingFiles(rec);
                deleted++;
            }
            return deleted;
        }

        /// <summary>
        /// Removes attempt-owned recording entries AND attempt-authored
        /// topology (branch points, branch-point parent/child id references,
        /// recording parent/child branch-point ids) from any committed tree
        /// the in-place attempt mutated. Required for the committed-tree-attach
        /// shape where <see cref="RewindInvoker.AtomicMarkerWrite"/> attached the
        /// in-place fork to a committed tree because no pending tree existed.
        /// Without this, the fork dictionary entry, any session-authored branch
        /// points (e.g. an in-flight stage separation that ran before the
        /// merge dialog), and dangling parent/child id refs would survive
        /// discard and OnSave would serialise them as committed mission history.
        /// The pending-tree case is naturally handled by
        /// <see cref="RecordingStore.PopPendingTree"/> later in the discard flow.
        /// </summary>
        private static int PruneAttemptRecordingsFromCommittedTrees(
            HashSet<string> attemptIds, ReFlySessionMarker marker)
        {
            if (attemptIds == null || attemptIds.Count == 0)
                return 0;

            int prunedRecordings = 0;
            int totalScrubbedBranchPointRefs = 0;
            int totalRemovedSessionBranchPoints = 0;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return 0;

            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null || committedTree.Recordings == null)
                    continue;

                int prunedHere = 0;
                foreach (var attemptId in attemptIds)
                {
                    if (string.IsNullOrEmpty(attemptId))
                        continue;
                    if (committedTree.Recordings.Remove(attemptId))
                        prunedHere++;
                }

                // Repair stale ActiveRecordingId. RestoreActiveTreeFromPending's
                // markerSwap branch sets `tree.ActiveRecordingId = marker.ActiveReFlyRecordingId`
                // (the fork id) when the marker takes over an active tree.
                // The fork was just removed from `committedTree.Recordings`,
                // so leaving ActiveRecordingId pointing at the deleted id
                // would round-trip through OnSave and any consumer reading
                // `tree.ActiveRecordingId ?? tree.RootRecordingId` raw
                // (e.g. ParsekFlight's recorder-rebind seed) would target a
                // missing recording on the next session. Mirrors the equivalent
                // repair `RestoreSanitizedPendingTreeIfDetached` already does
                // for the detached-pending path.
                if (prunedHere > 0
                    && !string.IsNullOrEmpty(committedTree.ActiveRecordingId)
                    && !committedTree.Recordings.ContainsKey(committedTree.ActiveRecordingId))
                {
                    string oldActive = committedTree.ActiveRecordingId;
                    committedTree.ActiveRecordingId =
                        marker != null
                        && !string.IsNullOrEmpty(marker.OriginChildRecordingId)
                        && committedTree.Recordings.ContainsKey(marker.OriginChildRecordingId)
                            ? marker.OriginChildRecordingId
                            : null;
                    ParsekLog.Info("MergeDialog",
                        $"PruneAttemptRecordingsFromCommittedTrees: reset stale " +
                        $"tree.ActiveRecordingId from '{oldActive}' to " +
                        $"'{committedTree.ActiveRecordingId ?? "<null>"}' on tree={committedTree.Id ?? "<none>"} " +
                        $"(prior id was an attempt recording removed by this prune)");
                }

                // Session-authored branch points (e.g. an in-flight stage
                // separation booked during the abandoned attempt) must be
                // dropped too. Done BEFORE the topology scrub so the scrub
                // only walks BPs that will survive Discard - any work on a
                // about-to-be-removed BP is wasted. PreSessionBranchPointIds
                // is captured against marker.TreeId only, so applying it to
                // a different committed tree would erroneously delete that
                // tree's unrelated branch points - gate on tree id match.
                int removedSessionBps = 0;
                if (marker != null
                    && !string.IsNullOrEmpty(marker.TreeId)
                    && string.Equals(committedTree.Id, marker.TreeId,
                        System.StringComparison.Ordinal))
                {
                    removedSessionBps = PruneSessionCreatedBranchPoints(
                        committedTree, marker);
                }

                // Branch-point ParentRecordingIds / ChildRecordingIds on
                // surviving BPs may reference now-removed attempt recordings;
                // scrub them so serialised topology never points at deleted
                // ids. Mirrors RemoveAttemptRecordingsFromTree for the
                // detached-pending path.
                int scrubbedRefs = ScrubAttemptIdsFromBranchPointTopology(
                    committedTree, attemptIds);

                if (prunedHere > 0 || scrubbedRefs > 0 || removedSessionBps > 0)
                {
                    // The fork's pid lived in the recorder's active slot AND
                    // in the tree's background map. After pruning, the map
                    // must be rebuilt so the abandoned pid does not survive
                    // as a stale background entry.
                    committedTree.RebuildBackgroundMap();
                    ParsekLog.Info("MergeDialog",
                        $"PruneAttemptRecordingsFromCommittedTrees: " +
                        $"tree={committedTree.Id ?? "<none>"} " +
                        $"prunedRecordings={prunedHere} " +
                        $"scrubbedBranchPointRefs={scrubbedRefs} " +
                        $"removedSessionBranchPoints={removedSessionBps}");
                }

                prunedRecordings += prunedHere;
                totalScrubbedBranchPointRefs += scrubbedRefs;
                totalRemovedSessionBranchPoints += removedSessionBps;
            }

            if (totalScrubbedBranchPointRefs > 0
                || totalRemovedSessionBranchPoints > 0)
            {
                ParsekLog.Verbose("MergeDialog",
                    $"PruneAttemptRecordingsFromCommittedTrees totals: " +
                    $"prunedRecordings={prunedRecordings} " +
                    $"scrubbedBranchPointRefs={totalScrubbedBranchPointRefs} " +
                    $"removedSessionBranchPoints={totalRemovedSessionBranchPoints}");
            }

            return prunedRecordings;
        }

        private static int ScrubAttemptIdsFromBranchPointTopology(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.BranchPoints == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int scrubbed = 0;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                scrubbed += CountAndRemoveAttemptIds(bp.ParentRecordingIds, attemptIds);
                scrubbed += CountAndRemoveAttemptIds(bp.ChildRecordingIds, attemptIds);
            }
            return scrubbed;
        }

        private static int CountAndRemoveAttemptIds(
            List<string> recordingIds, HashSet<string> attemptIds)
        {
            if (recordingIds == null || attemptIds == null || attemptIds.Count == 0)
                return 0;
            int removed = 0;
            for (int i = recordingIds.Count - 1; i >= 0; i--)
            {
                if (attemptIds.Contains(recordingIds[i]))
                {
                    recordingIds.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        private static int ClearReFlyAttemptTransientFields(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            int cleared = 0;
            cleared += ClearTransientFieldsInRecordings(
                RecordingStore.CommittedRecordings, marker, attemptIds);
            if (tree != null && tree.Recordings != null)
                cleared += ClearTransientFieldsInRecordings(
                    tree.Recordings.Values, marker, attemptIds);
            return cleared;
        }

        private static int ClearTransientFieldsInRecordings(
            IEnumerable<Recording> recordings,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (recordings == null || marker == null)
                return 0;

            int cleared = 0;
            foreach (var rec in recordings)
            {
                if (rec == null)
                    continue;

                bool sessionOwned = !string.IsNullOrEmpty(marker.SessionId)
                    && string.Equals(rec.CreatingSessionId,
                        marker.SessionId, System.StringComparison.Ordinal);
                bool rpOwned = !string.IsNullOrEmpty(marker.RewindPointId)
                    && string.Equals(rec.ProvisionalForRpId,
                        marker.RewindPointId, System.StringComparison.Ordinal);
                bool attemptOwned = attemptIds != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && attemptIds.Contains(rec.RecordingId);

                if (!sessionOwned && !rpOwned && !attemptOwned)
                    continue;

                if (!string.IsNullOrEmpty(rec.CreatingSessionId)
                    || !string.IsNullOrEmpty(rec.ProvisionalForRpId)
                    || !string.IsNullOrEmpty(rec.SupersedeTargetId))
                {
                    cleared++;
                }

                rec.CreatingSessionId = null;
                rec.ProvisionalForRpId = null;
                rec.SupersedeTargetId = null;
            }
            return cleared;
        }

        private static bool RestoreSanitizedPendingTreeIfDetached(
            RecordingTree tree,
            ReFlySessionMarker marker,
            HashSet<string> attemptIds)
        {
            if (tree == null)
                return false;
            if (CommittedTreeExists(tree.Id))
                return false;

            int prunedRecordings = RemoveAttemptRecordingsFromTree(tree, attemptIds);
            int prunedSessionBranchPoints = PruneSessionCreatedBranchPoints(tree, marker);
            if (tree.Recordings == null || tree.Recordings.Count == 0)
            {
                // Expected unreachable in production: AddAttemptIdIfSafe protects
                // the origin id, so a valid Re-Fly tree keeps at least that recording.
                ParsekLog.Warn("MergeDialog",
                    $"RestoreSanitizedPendingTreeIfDetached: tree={tree.Id ?? "<none>"} " +
                    "has no recordings after Re-Fly attempt pruning - cannot restore committed tree");
                return false;
            }

            if (string.IsNullOrEmpty(tree.ActiveRecordingId)
                || !tree.Recordings.ContainsKey(tree.ActiveRecordingId))
            {
                tree.ActiveRecordingId = !string.IsNullOrEmpty(marker?.OriginChildRecordingId)
                    && tree.Recordings.ContainsKey(marker.OriginChildRecordingId)
                        ? marker.OriginChildRecordingId
                        : FirstRecordingId(tree);
            }
            if (string.IsNullOrEmpty(tree.RootRecordingId)
                || !tree.Recordings.ContainsKey(tree.RootRecordingId))
            {
                tree.RootRecordingId = tree.ActiveRecordingId ?? FirstRecordingId(tree);
            }

            foreach (var rec in tree.Recordings.Values)
            {
                if (rec != null && string.IsNullOrEmpty(rec.TreeId))
                    rec.TreeId = tree.Id;
            }

            int groupRepairs = RecordingGroupStore.RepairAutoGeneratedTreeGroups(tree);
            tree.RebuildBackgroundMap();
            int addedRecordings = 0;
            int skippedExisting = 0;
            int dirtyQueuedForScenarioSave = 0;
            foreach (var rec in tree.Recordings.Values)
            {
                if (rec == null)
                    continue;
                if (CommittedRecordingIdExists(rec.RecordingId))
                {
                    skippedExisting++;
                }
                else
                {
                    RecordingStore.AddCommittedInternal(rec);
                    addedRecordings++;
                }
                if (rec.FilesDirty)
                    dirtyQueuedForScenarioSave++;
            }
            RecordingStore.AddCommittedTreeInternal(tree);
            ParsekLog.Info("MergeDialog",
                $"RestoreSanitizedPendingTreeIfDetached: restored committed tree " +
                $"tree={tree.Id ?? "<none>"} recordings={tree.Recordings.Count} " +
                $"prunedAttemptRecordings={prunedRecordings} " +
                $"prunedSessionBranchPoints={prunedSessionBranchPoints} " +
                $"groupRepairs={groupRepairs} " +
                $"addedRecordings={addedRecordings} skippedExisting={skippedExisting} " +
                $"dirtyQueuedForScenarioSave={dirtyQueuedForScenarioSave}");
            return true;
        }

        private static int RemoveAttemptRecordingsFromTree(
            RecordingTree tree, HashSet<string> attemptIds)
        {
            if (tree == null || tree.Recordings == null
                || attemptIds == null || attemptIds.Count == 0)
                return 0;

            int removed = 0;
            foreach (string id in attemptIds)
            {
                if (string.IsNullOrEmpty(id))
                    continue;
                if (tree.Recordings.Remove(id))
                    removed++;
            }

            if (tree.BranchPoints != null)
            {
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    var bp = tree.BranchPoints[i];
                    if (bp == null)
                        continue;
                    RemoveAttemptIds(bp.ParentRecordingIds, attemptIds);
                    RemoveAttemptIds(bp.ChildRecordingIds, attemptIds);
                }
            }

            return removed;
        }

        private static int PruneSessionCreatedBranchPoints(
            RecordingTree tree,
            ReFlySessionMarker marker)
        {
            if (tree == null || tree.BranchPoints == null || tree.BranchPoints.Count == 0)
                return 0;
            if (marker == null || marker.PreSessionBranchPointIds == null)
                return 0;

            // A present-but-empty baseline is intentional: the marker was
            // written by code that snapshots branch point IDs, and the tree had
            // no branch points at invocation. In that case every current branch
            // point is session-authored and must be discarded. A null baseline
            // is the legacy/unknown case and is skipped above.
            var preSessionIds = new HashSet<string>(
                marker.PreSessionBranchPointIds,
                System.StringComparer.Ordinal);
            var removedIds = new HashSet<string>(System.StringComparer.Ordinal);
            for (int i = tree.BranchPoints.Count - 1; i >= 0; i--)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null || string.IsNullOrEmpty(bp.Id))
                    continue;
                if (preSessionIds.Contains(bp.Id))
                    continue;
                removedIds.Add(bp.Id);
                tree.BranchPoints.RemoveAt(i);
            }

            if (removedIds.Count == 0)
                return 0;

            if (tree.Recordings != null)
            {
                foreach (var rec in tree.Recordings.Values)
                {
                    if (rec == null)
                        continue;

                    bool changed = false;
                    if (!string.IsNullOrEmpty(rec.ParentBranchPointId)
                        && removedIds.Contains(rec.ParentBranchPointId))
                    {
                        rec.ParentBranchPointId = null;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(rec.ChildBranchPointId)
                        && removedIds.Contains(rec.ChildBranchPointId))
                    {
                        rec.ChildBranchPointId = null;
                        changed = true;
                    }

                    if (changed)
                        rec.MarkFilesDirty();
                }
            }

            ParsekLog.Verbose("MergeDialog",
                $"PruneSessionCreatedBranchPoints: tree={tree.Id ?? "<none>"} " +
                $"sess={marker.SessionId ?? "<none>"} removed={removedIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            return removedIds.Count;
        }

        private static void RemoveAttemptIds(
            List<string> recordingIds, HashSet<string> attemptIds)
        {
            if (recordingIds == null || attemptIds == null || attemptIds.Count == 0)
                return;
            for (int i = recordingIds.Count - 1; i >= 0; i--)
            {
                if (attemptIds.Contains(recordingIds[i]))
                    recordingIds.RemoveAt(i);
            }
        }

        private static string FirstRecordingId(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null)
                return null;
            foreach (var kvp in tree.Recordings)
                return kvp.Key;
            return null;
        }

        private static bool CommittedTreeExists(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return false;
            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees == null)
                return false;
            for (int i = 0; i < committedTrees.Count; i++)
            {
                var committedTree = committedTrees[i];
                if (committedTree == null)
                    continue;
                if (string.Equals(committedTree.Id, treeId, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool CommittedRecordingIdExists(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null)
                return false;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null)
                    continue;
                if (string.Equals(rec.RecordingId, recordingId, System.StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static RewindPoint FindRewindPointForMarker(
            ParsekScenario scenario, ReFlySessionMarker marker)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || marker == null
                || string.IsNullOrEmpty(marker.RewindPointId))
            {
                return null;
            }

            for (int i = 0; i < scenario.RewindPoints.Count; i++)
            {
                var rp = scenario.RewindPoints[i];
                if (rp == null)
                    continue;
                if (string.Equals(rp.RewindPointId,
                        marker.RewindPointId, System.StringComparison.Ordinal))
                    return rp;
            }
            return null;
        }

        private static bool PromoteOriginRewindPointForDiscard(
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            RewindPoint rp = FindRewindPointForMarker(scenario, marker);
            if (rp == null)
                return false;

            bool promoted = rp.SessionProvisional;
            bool hadCreatingSessionId = !string.IsNullOrEmpty(rp.CreatingSessionId);
            rp.SessionProvisional = false;
            rp.CreatingSessionId = null;
            if (promoted)
            {
                ParsekLog.Info("ReFlySession",
                    $"Origin RP promoted to persistent rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            else if (hadCreatingSessionId)
            {
                ParsekLog.Info("ReFlySession",
                    $"Origin RP session metadata cleared rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            else
            {
                ParsekLog.Verbose("ReFlySession",
                    $"Origin RP already persistent rp={marker.RewindPointId} " +
                    $"sess={marker.SessionId ?? "<no-id>"} reason=discardReFlyAttemptFromMergeDialog");
            }
            return promoted;
        }

        private static int PurgeDiscardedSessionRewindPoints(
            ParsekScenario scenario,
            ReFlySessionMarker marker)
        {
            if (object.ReferenceEquals(null, scenario)
                || scenario.RewindPoints == null
                || marker == null
                || string.IsNullOrEmpty(marker.SessionId))
            {
                return 0;
            }

            int removed = 0;
            for (int i = scenario.RewindPoints.Count - 1; i >= 0; i--)
            {
                RewindPoint rp = scenario.RewindPoints[i];
                if (rp == null)
                    continue;
                if (!rp.SessionProvisional)
                    continue;
                if (!string.Equals(rp.CreatingSessionId,
                        marker.SessionId, System.StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrEmpty(marker.RewindPointId)
                    && string.Equals(rp.RewindPointId,
                        marker.RewindPointId, System.StringComparison.Ordinal))
                    continue;

                RewindPointReaper.TryDeleteQuicksaveFile(rp);
                scenario.RewindPoints.RemoveAt(i);
                RecordingsTableUI.ClearRewindSlotCanInvokeLogState(rp.RewindPointId);
                RewindPointReaper.ClearBranchPointBackref(rp);
                ParsekLog.Info("ReFlySession",
                    $"Discard removed session provisional RP rp={rp.RewindPointId ?? "<no-id>"} " +
                    $"bp={rp.BranchPointId ?? "<no-bp>"} sess={marker.SessionId ?? "<no-id>"}");
                removed++;
            }

            if (removed > 0)
            {
                ParsekLog.Info("ReFlySession",
                    $"Discard removed {removed.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                    $"session provisional RP(s) sess={marker.SessionId ?? "<no-id>"}");
            }
            return removed;
        }

        private static bool SaveDiscardedReFlyStateDurably(string sessionId)
        {
            var saveFn = RecordingStore.SaveGameForTesting ?? GamePersistence.SaveGame;
            if (RecordingStore.SaveGameForTesting == null)
            {
                if (HighLogic.LoadedScene == GameScenes.LOADING)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"Discard Re-Fly durable save skipped during LOADING sess={sessionId ?? "<no-id>"}");
                    return false;
                }
                if (HighLogic.CurrentGame == null
                    || string.IsNullOrEmpty(HighLogic.SaveFolder))
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"Discard Re-Fly durable save skipped (no current game/save folder) " +
                        $"sess={sessionId ?? "<no-id>"}");
                    return false;
                }
            }
            // SaveGameForTesting deliberately bypasses the scene/current-game
            // guards above so xUnit can assert the durable-save call without
            // constructing a live KSP HighLogic state. Test hooks ignore the
            // SaveFolder argument.

            try
            {
                string result = saveFn("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
                if (string.IsNullOrEmpty(result))
                {
                    ParsekLog.Warn("MergeDialog",
                        $"Discard Re-Fly durable save returned null sess={sessionId ?? "<no-id>"}");
                    return false;
                }

                ParsekLog.Info("MergeDialog",
                    $"Discard Re-Fly state persisted via persistent.sfs sess={sessionId ?? "<no-id>"}");
                return true;
            }
            catch (System.Exception ex)
            {
                ParsekLog.Warn("MergeDialog",
                    $"Discard Re-Fly durable save threw sess={sessionId ?? "<no-id>"} " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a re-fly
        /// session is active at merge time, write the supersede relations for
        /// the origin subtree and flip the provisional's MergeState. Skipped
        /// silently when no session is active — the regular tree-merge flow
        /// is unchanged.
        /// </summary>
        internal static ReFlyMergeCommitResult TryCommitReFlySupersede()
            => TryCommitReFlySupersede(null);

        private static ReFlyMergeCommitResult TryCommitReFlySupersede(
            RecordingIdentityHint activeReFlyTargetHint)
        {
            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no scenario instance — skipping re-fly commit path");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            var marker = scenario.ActiveReFlySessionMarker;
            if (marker == null)
            {
                ParsekLog.Verbose("MergeDialog",
                    "TryCommitReFlySupersede: no active re-fly session marker — " +
                    "regular tree-merge flow, no supersede relations to write");
                return ReFlyMergeCommitResult.NotApplicable;
            }

            string provisionalId = marker.ActiveReFlyRecordingId;
            if (string.IsNullOrEmpty(provisionalId))
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: marker sess={marker.SessionId ?? "<no-id>"} " +
                    "has no ActiveReFlyRecordingId — cannot commit supersede; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }

            Recording provisional = FindCommittedRecording(provisionalId);
            if (provisional == null)
            {
                provisional = ResolveOptimizedRecordingSurvivor(
                    activeReFlyTargetHint,
                    marker);
                if (provisional != null)
                {
                    ParsekLog.Info("MergeDialog",
                        $"TryCommitReFlySupersede: resolved optimized-away active " +
                        $"Re-Fly recording id={provisionalId} to survivor " +
                        $"id={provisional.RecordingId ?? "<no-id>"} " +
                        $"tree={provisional.TreeId ?? "<none>"} " +
                        $"chainId={provisional.ChainId ?? "<none>"} " +
                        $"chainBranch={provisional.ChainBranch} " +
                        $"sourcePid={provisional.VesselPersistentId}");
                    // Keep the marker pointed at the optimized survivor for the
                    // supersede cleanup below; MergeCommit's captured active target
                    // remains the pre-optimization id by design.
                    marker.ActiveReFlyRecordingId = provisional.RecordingId;
                    provisionalId = provisional.RecordingId;
                }
            }
            if (provisional == null)
            {
                ParsekLog.Warn("MergeDialog",
                    $"TryCommitReFlySupersede: provisional rec={provisionalId} " +
                    "not found in committed list after tree commit; " +
                    "leaving marker in place for load-time sweep");
                return ReFlyMergeCommitResult.Interrupted;
            }


            ParsekLog.Info("MergeDialog",
                $"TryCommitReFlySupersede: invoking MergeJournalOrchestrator for " +
                $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId} " +
                $"origin={marker.OriginChildRecordingId ?? "<none>"}");

            bool ok;
            try
            {
                ok = MergeJournalOrchestrator.RunMerge(marker, provisional);
            }
            catch (System.Exception ex)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator threw {ex.GetType().Name}: {ex.Message} — " +
                    $"journal will drive recovery on next load");
                ParsekLog.ScreenMessage(
                    "Merge interrupted — will finish on next load", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            if (!ok)
            {
                ParsekLog.Error("MergeDialog",
                    $"TryCommitReFlySupersede: orchestrator returned false for " +
                    $"sess={marker.SessionId ?? "<no-id>"} provisional={provisionalId}");
                ParsekLog.ScreenMessage("Merge commit skipped (see log)", 3f);
                return ReFlyMergeCommitResult.Interrupted;
            }

            return ReFlyMergeCommitResult.Completed;
        }


        // Raw committed-list scan by id. Kept local to the merge path so we
        // don't need another allowlist entry; the recording we're hunting for
        // is the provisional that was added by RewindInvoker.AddProvisional
        // (NotCommitted, filtered out of ERS), so ERS routing is not the right
        // lookup.
        private static Recording FindCommittedRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (string.Equals(rec.RecordingId, recordingId, System.StringComparison.Ordinal))
                    return rec;
            }
            return null;
        }

        private sealed class RecordingIdentityHint
        {
            internal string RecordingId;
            internal string TreeId;
            internal string ChainId;
            internal int ChainBranch;
            internal uint VesselPersistentId;
        }

        private static RecordingIdentityHint CaptureOptimizationSurvivorHint(
            RecordingTree tree,
            string recordingId)
        {
            if (tree == null
                || tree.Recordings == null
                || string.IsNullOrEmpty(recordingId))
            {
                return null;
            }

            Recording rec;
            if (!tree.Recordings.TryGetValue(recordingId, out rec) || rec == null)
                return null;

            return CaptureRecordingIdentityHint(rec);
        }

        private static RecordingIdentityHint CaptureRecordingIdentityHint(Recording rec)
        {
            if (rec == null)
                return null;

            return new RecordingIdentityHint
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                ChainId = rec.ChainId,
                ChainBranch = rec.ChainBranch,
                VesselPersistentId = rec.VesselPersistentId,
            };
        }

        private static Recording ResolveOptimizedRecordingSurvivor(
            RecordingIdentityHint hint,
            ReFlySessionMarker marker)
        {
            if (hint == null)
                return null;

            Recording exact = FindCommittedRecording(hint.RecordingId);
            if (exact != null)
                return exact;

            Recording origin = marker != null
                ? FindCommittedRecording(marker.OriginChildRecordingId)
                : null;
            if (IsOptimizationSurvivorForHint(origin, hint))
                return origin;

            Recording supersedeTarget = marker != null
                ? FindCommittedRecording(marker.SupersedeTargetId)
                : null;
            if (IsOptimizationSurvivorForHint(supersedeTarget, hint))
                return supersedeTarget;

            var committed = RecordingStore.CommittedRecordings;
            if (committed == null
                || string.IsNullOrEmpty(hint.ChainId)
                || hint.VesselPersistentId == 0u)
            {
                return null;
            }

            Recording best = null;
            for (int i = 0; i < committed.Count; i++)
            {
                Recording candidate = committed[i];
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                if (!IsOptimizationSurvivorForHint(candidate, hint))
                    continue;

                if (best == null || candidate.ChainIndex < best.ChainIndex)
                    best = candidate;
            }

            return best;
        }

        private static bool IsOptimizationSurvivorForHint(
            Recording candidate,
            RecordingIdentityHint hint)
        {
            return IsRecordingIdentityMatch(candidate, hint, allowExactRecordingId: false);
        }

        private static bool IsRecordingIdentityMatch(
            Recording candidate,
            RecordingIdentityHint hint,
            bool allowExactRecordingId)
        {
            if (candidate == null || hint == null)
                return false;
            if (allowExactRecordingId
                && !string.IsNullOrEmpty(hint.RecordingId)
                && string.Equals(
                    hint.RecordingId,
                    candidate.RecordingId,
                    System.StringComparison.Ordinal))
            {
                return true;
            }
            if (string.IsNullOrEmpty(candidate.RecordingId))
                return false;
            if (string.IsNullOrEmpty(hint.ChainId)
                || string.IsNullOrEmpty(candidate.ChainId)
                || !string.Equals(
                    candidate.ChainId,
                    hint.ChainId,
                    System.StringComparison.Ordinal))
            {
                return false;
            }
            if (candidate.ChainBranch != hint.ChainBranch)
                return false;
            if (hint.VesselPersistentId == 0u
                || candidate.VesselPersistentId == 0u
                || candidate.VesselPersistentId != hint.VesselPersistentId)
            {
                return false;
            }
            if (!string.IsNullOrEmpty(hint.TreeId)
                && !string.Equals(
                    candidate.TreeId,
                    hint.TreeId,
                    System.StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        #region Extracted helpers

        /// <summary>
        /// Pure function: compute the total time span across all recordings in a tree.
        /// Returns 0 if the tree has no recordings.
        /// </summary>
        internal static double ComputeTreeDurationRange(RecordingTree tree)
        {
            if (tree == null || tree.Recordings == null || tree.Recordings.Count == 0)
                return 0;

            double minStartUT = double.MaxValue;
            double maxEndUT = double.MinValue;
            foreach (var rec in tree.Recordings.Values)
            {
                double start = rec.StartUT;
                double end = rec.EndUT;
                if (start < minStartUT) minStartUT = start;
                if (end > maxEndUT) maxEndUT = end;
            }

            return (minStartUT < double.MaxValue && maxEndUT > double.MinValue)
                ? maxEndUT - minStartUT
                : 0;
        }

        #endregion

        // ================================================================
        // Per-vessel persist/ghost-only decisions
        // ================================================================

        /// <summary>
        /// Determines whether a recording can actually spawn as a real vessel after merge.
        /// This reuses the same intrinsic spawn policy as timeline playback so the dialog's
        /// default "spawnable" count stays aligned with runtime behavior.
        /// </summary>
        internal static bool CanPersistVessel(Recording rec, RecordingTree treeContext = null)
        {
            if (rec == null)
                return false;

            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: treeContext);
            return needsSpawn;
        }

        /// <summary>
        /// Builds default persist/ghost-only decisions for all leaf recordings in a tree.
        /// Surviving vessels default to persist (true), destroyed/recovered default to ghost-only (false).
        /// Keys are RecordingId. Pure static for testability.
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(RecordingTree tree)
            => BuildDefaultVesselDecisions(tree, null, null);

        /// <summary>
        /// Builds default persist/ghost-only decisions for a tree, additionally
        /// forcing every non-leaf recording listed in
        /// <paramref name="suppressedRecordingIds"/> to ghost-only — except the
        /// recording identified by <paramref name="activeReFlyTargetId"/>, which
        /// is the live Re-Fly target the player is currently flying and must
        /// stay spawnable.
        ///
        /// <para>
        /// Bug fix (refly-suppressed-non-leaf): without this branch a parent
        /// recording in the suppressed subtree (e.g. an upper stage that has
        /// child decoupling/breakup branches) keeps <c>VesselSnapshot</c> set,
        /// and <c>GhostPlaybackLogic.ShouldSpawnAtRecordingEnd</c> later spawns
        /// it as a clickable real vessel alongside the playback ghost. The
        /// only branches the leaf walk reaches are chain tips, so non-leaf
        /// suppressed recordings would otherwise be silently retained.
        /// </para>
        /// </summary>
        internal static Dictionary<string, bool> BuildDefaultVesselDecisions(
            RecordingTree tree,
            HashSet<string> suppressedRecordingIds,
            string activeReFlyTargetId)
        {
            var decisions = new Dictionary<string, bool>();
            if (tree == null)
                return decisions;

            var leaves = tree.GetAllLeaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                var leaf = leaves[i];
                bool canPersist = CanPersistVessel(leaf, tree);
                decisions[leaf.RecordingId] = canPersist;
                ParsekLog.Verbose("MergeDialog",
                    $"BuildDefaultVesselDecisions: leaf='{leaf.RecordingId}' vessel='{leaf.VesselName}' " +
                    $"terminal={leaf.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={leaf.VesselSnapshot != null} canPersist={canPersist}");
            }

            // Bug #271: in always-tree mode with breakup-continuous design, the active
            // recording may be non-leaf (it has ChildBranchPointId from breakup branches)
            // but is still the main vessel's recording that should be spawnable. Include
            // it in decisions if it wasn't already covered as a leaf.
            if (!string.IsNullOrEmpty(tree.ActiveRecordingId)
                && !decisions.ContainsKey(tree.ActiveRecordingId))
            {
                Recording activeRec;
                if (tree.Recordings.TryGetValue(tree.ActiveRecordingId, out activeRec))
                {
                    bool canPersist = CanPersistVessel(activeRec, tree);
                    decisions[activeRec.RecordingId] = canPersist;
                    ParsekLog.Verbose("MergeDialog",
                        $"BuildDefaultVesselDecisions: active-nonleaf='{activeRec.RecordingId}' " +
                        $"vessel='{activeRec.VesselName}' " +
                        $"terminal={activeRec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"hasSnapshot={activeRec.VesselSnapshot != null} canPersist={canPersist}");
                }
            }

            // Re-Fly suppressed-subtree pass: for every recording whose id appears
            // in the suppression closure, force ghost-only — even if it is a
            // non-leaf the leaf walk above never visited. The sole exception is
            // the live Re-Fly target itself, which must stay spawnable. Note we
            // walk the closure ids (not tree.Recordings) because the closure is
            // computed against committed recordings and may name records that
            // are not in the pending tree (chain siblings, etc.); we only act
            // when the id ALSO exists in this tree.
            if (suppressedRecordingIds != null && suppressedRecordingIds.Count > 0)
            {
                int forced = 0;
                int skippedActiveTarget = 0;
                int alreadyGhostOnly = 0;
                int notInTree = 0;
                foreach (string suppressedId in suppressedRecordingIds)
                {
                    if (string.IsNullOrEmpty(suppressedId)) continue;
                    if (!string.IsNullOrEmpty(activeReFlyTargetId)
                        && string.Equals(suppressedId, activeReFlyTargetId, System.StringComparison.Ordinal))
                    {
                        skippedActiveTarget++;
                        ParsekLog.Verbose("MergeDialog",
                            $"BuildDefaultVesselDecisions: keeping active Re-Fly target spawnable " +
                            $"id='{suppressedId}' (in suppressed subtree but is the live target)");
                        continue;
                    }
                    Recording rec;
                    if (!tree.Recordings.TryGetValue(suppressedId, out rec) || rec == null)
                    {
                        notInTree++;
                        continue;
                    }
                    bool priorPersistDecision;
                    if (decisions.TryGetValue(suppressedId, out priorPersistDecision) && !priorPersistDecision)
                    {
                        alreadyGhostOnly++;
                        continue;
                    }
                    decisions[suppressedId] = false;
                    forced++;
                    ParsekLog.Info("MergeDialog",
                        $"BuildDefaultVesselDecisions: forcing ghost-only on suppressed " +
                        $"id='{suppressedId}' vessel='{rec.VesselName}' " +
                        $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"isLeaf={rec.ChildBranchPointId == null} " +
                        $"priorDecision={(decisions.ContainsKey(suppressedId) ? "set" : "unset")}");
                }
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: suppressed-subtree pass complete " +
                    $"closureSize={suppressedRecordingIds.Count} forcedGhostOnly={forced} " +
                    $"skippedActiveTarget={skippedActiveTarget} alreadyGhostOnly={alreadyGhostOnly} " +
                    $"notInTree={notInTree} activeTarget='{activeReFlyTargetId ?? "<none>"}'");
            }

            ApplyActiveReFlyParentChainDefaults(
                tree,
                decisions,
                activeReFlyTargetId,
                suppressedRecordingIds);

            return decisions;
        }


        private static void ApplyActiveReFlyParentChainDefaults(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId,
            HashSet<string> suppressedRecordingIds)
        {
            // This runs only while BuildDefaultVesselDecisions is constructing
            // the dialog's initial defaults. Keep this path decision-only:
            // MergeCommit stamps spawned state by adopting already-materialized
            // source vessels only after the player actually accepts the merge.
            // Parent-chain terminal tips can be stale old-future cleanup, but
            // they can also be legitimate future materialized vessels. Keep the
            // dialog aligned with the normal runtime spawn predicate unless the
            // tip is explicitly suppressed.
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return;

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return;

            int forced = 0;
            int retainedSpawnable = 0;
            int alreadyGhostOnly = 0;
            int missing = 0;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId)) continue;

                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec) || rec == null)
                {
                    missing++;
                    continue;
                }

                bool priorDecision;
                bool hadPriorDecision = decisions.TryGetValue(tipId, out priorDecision);
                if (hadPriorDecision && !priorDecision)
                {
                    alreadyGhostOnly++;
                    continue;
                }

                bool explicitlySuppressed = suppressedRecordingIds != null
                    && suppressedRecordingIds.Contains(tipId)
                    && !string.Equals(
                        tipId,
                        activeReFlyTargetId,
                        System.StringComparison.Ordinal);
                bool canPersist = hadPriorDecision
                    ? priorDecision
                    : CanPersistVessel(rec, tree);

                if (canPersist && !explicitlySuppressed)
                {
                    decisions[tipId] = true;
                    retainedSpawnable++;
                    ParsekLog.Info("MergeDialog",
                        $"BuildDefaultVesselDecisions: retaining active Re-Fly parent-chain " +
                        $"terminal tip spawnable id='{tipId}' vessel='{rec.VesselName}' " +
                        $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                        $"hasSnapshot={rec.VesselSnapshot != null} " +
                        $"priorDecision={(hadPriorDecision ? "set" : "unset")} " +
                        $"reason=normal-spawn-policy activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                decisions[tipId] = false;
                forced++;
                ParsekLog.Info("MergeDialog",
                    $"BuildDefaultVesselDecisions: defaulting active Re-Fly parent-chain " +
                    $"terminal tip to ghost-only id='{tipId}' vessel='{rec.VesselName}' " +
                    $"terminal={rec.TerminalStateValue?.ToString() ?? "null"} " +
                    $"hasSnapshot={rec.VesselSnapshot != null} " +
                    $"priorDecision={(hadPriorDecision ? "set" : "unset")} " +
                    $"canPersist={canPersist} explicitlySuppressed={explicitlySuppressed} " +
                    $"activeTarget='{activeReFlyTargetId}'");
            }

            ParsekLog.Info("MergeDialog",
                $"BuildDefaultVesselDecisions: active Re-Fly parent-chain pass complete " +
                $"candidates={parentTips.Count} forcedGhostOnly={forced} " +
                $"retainedSpawnable={retainedSpawnable} " +
                $"alreadyGhostOnly={alreadyGhostOnly} missing={missing} " +
                $"activeTarget='{activeReFlyTargetId}'");
        }

        internal static int AdoptExistingSourceVesselsForRetainedParentChainTips(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId)
        {
            var retainedParentChainTipCandidates =
                CollectRetainedParentChainTipAdoptionCandidates(
                    tree, decisions, activeReFlyTargetId);
            return AdoptExistingSourceVesselsForRetainedParentChainTips(
                tree,
                decisions,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
        }

        private static int AdoptExistingSourceVesselsForRetainedParentChainTips(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId,
            List<RecordingIdentityHint> retainedParentChainTipCandidates)
        {
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return 0;

            var currentTipRecords = CollectCurrentParentChainTipAdoptionRecords(
                tree,
                activeReFlyTargetId,
                retainedParentChainTipCandidates);
            if (currentTipRecords == null || currentTipRecords.Count == 0)
                return 0;

            int checkedSpawnable = 0;
            int adoptedExistingSource = 0;
            int skippedNotSpawnable = 0;
            int missing = 0;

            for (int i = 0; i < currentTipRecords.Count; i++)
            {
                Recording rec = currentTipRecords[i];
                if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                {
                    missing++;
                    continue;
                }
                string tipId = rec.RecordingId;

                bool retainedByPreOptimizationTip =
                    IsRetainedParentChainTipAdoptionCandidate(
                        rec,
                        retainedParentChainTipCandidates);
                if (retainedByPreOptimizationTip)
                {
                    if (!CanPersistVessel(rec, tree))
                    {
                        skippedNotSpawnable++;
                        continue;
                    }
                }
                else
                {
                    bool persist;
                    bool hadDecision = TryResolvePersistDecisionForOptimizedTip(
                        tree, decisions, rec, out persist);
                    if (hadDecision && !persist)
                    {
                        skippedNotSpawnable++;
                        continue;
                    }

                    if (!hadDecision && !CanPersistVessel(rec, tree))
                    {
                        skippedNotSpawnable++;
                        continue;
                    }
                }

                checkedSpawnable++;
                if (VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                    rec,
                    "MergeDialog",
                    $"MergeCommit parent-chain tip '{tipId}'"))
                {
                    adoptedExistingSource++;
                }
            }

            ParsekLog.Info("MergeDialog",
                $"MergeCommit: active Re-Fly parent-chain adoption pass complete " +
                $"candidates={currentTipRecords.Count} checkedSpawnable={checkedSpawnable} " +
                $"adoptedExistingSource={adoptedExistingSource} " +
                $"skippedNotSpawnable={skippedNotSpawnable} missing={missing} " +
                $"retainedPreOptimizationTips={retainedParentChainTipCandidates?.Count ?? 0} " +
                $"activeTarget='{activeReFlyTargetId}'");
            return adoptedExistingSource;
        }

        private static List<Recording> CollectCurrentParentChainTipAdoptionRecords(
            RecordingTree tree,
            string activeReFlyTargetId,
            List<RecordingIdentityHint> retainedParentChainTipCandidates)
        {
            var result = new List<Recording>();
            var seen = new HashSet<string>(System.StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null)
                return result;

            if (retainedParentChainTipCandidates != null
                && retainedParentChainTipCandidates.Count > 0)
            {
                foreach (Recording rec in tree.Recordings.Values)
                {
                    AddCurrentParentChainTipIfMatch(
                        result,
                        seen,
                        rec,
                        IsRetainedParentChainTipAdoptionCandidate(
                            rec,
                            retainedParentChainTipCandidates));
                }
                return result;
            }

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return result;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId))
                    continue;
                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec))
                    continue;
                AddCurrentParentChainTipIfMatch(result, seen, rec, rec != null);
            }

            return result;
        }

        private static void AddCurrentParentChainTipIfMatch(
            List<Recording> result,
            HashSet<string> seen,
            Recording rec,
            bool matched)
        {
            if (!matched
                || result == null
                || seen == null
                || rec == null
                || string.IsNullOrEmpty(rec.RecordingId))
            {
                return;
            }
            if (!seen.Add(rec.RecordingId))
                return;
            result.Add(rec);
        }

        private static List<RecordingIdentityHint> CollectRetainedParentChainTipAdoptionCandidates(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            string activeReFlyTargetId)
        {
            var result = new List<RecordingIdentityHint>();
            if (tree == null || decisions == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return result;

            var parentTips = CollectActiveReFlyParentChainTerminalTipIds(
                tree, activeReFlyTargetId);
            if (parentTips == null || parentTips.Count == 0)
                return result;

            foreach (string tipId in parentTips)
            {
                if (string.IsNullOrEmpty(tipId)) continue;

                Recording rec;
                if (!tree.Recordings.TryGetValue(tipId, out rec) || rec == null)
                    continue;

                bool persist;
                bool hadDecision = TryResolvePersistDecisionForOptimizedTip(
                    tree, decisions, rec, out persist);
                if (hadDecision && !persist)
                    continue;
                if (!CanPersistVessel(rec, tree))
                    continue;

                var hint = CaptureRecordingIdentityHint(rec);
                if (hint != null)
                    result.Add(hint);
            }

            return result;
        }

        private static bool IsRetainedParentChainTipAdoptionCandidate(
            Recording rec,
            List<RecordingIdentityHint> candidates)
        {
            if (rec == null || candidates == null || candidates.Count == 0)
                return false;

            for (int i = 0; i < candidates.Count; i++)
            {
                RecordingIdentityHint candidate = candidates[i];
                if (candidate == null)
                    continue;

                if (IsRecordingIdentityMatch(rec, candidate, allowExactRecordingId: true))
                    return true;
            }

            return false;
        }

        internal static bool TryResolvePersistDecisionForOptimizedTip(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            Recording rec,
            out bool persist)
        {
            persist = false;
            if (rec == null || decisions == null)
                return false;

            if (!string.IsNullOrEmpty(rec.RecordingId)
                && decisions.TryGetValue(rec.RecordingId, out persist))
            {
                return true;
            }

            if (tree == null
                || tree.Recordings == null
                || string.IsNullOrEmpty(rec.ChainId))
            {
                return false;
            }

            Recording best = null;
            bool bestDecision = false;
            foreach (var candidate in tree.Recordings.Values)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.RecordingId))
                    continue;
                bool candidateDecision;
                if (!decisions.TryGetValue(candidate.RecordingId, out candidateDecision))
                    continue;
                if (!string.Equals(candidate.ChainId, rec.ChainId, System.StringComparison.Ordinal))
                    continue;
                if (candidate.ChainBranch != rec.ChainBranch)
                    continue;
                if (!string.IsNullOrEmpty(rec.TreeId)
                    && !string.Equals(candidate.TreeId, rec.TreeId, System.StringComparison.Ordinal))
                    continue;
                if (candidate.ChainIndex > rec.ChainIndex)
                    continue;
                if (best == null || candidate.ChainIndex > best.ChainIndex)
                {
                    best = candidate;
                    bestDecision = candidateDecision;
                }
            }

            if (best == null)
                return false;

            persist = bestDecision;
            return true;
        }

        internal static HashSet<string> CollectActiveReFlyParentChainTerminalTipIds(
            RecordingTree tree,
            string activeReFlyTargetId)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(activeReFlyTargetId))
                return result;

            Recording activeRec;
            if (!tree.Recordings.TryGetValue(activeReFlyTargetId, out activeRec) || activeRec == null)
                return result;

            HashSet<string> activeChainIds = CollectSameChainRecordingIds(tree, activeRec);
            var parentBranchPointIds = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (string activeChainId in activeChainIds)
            {
                Recording chainMember;
                if (string.IsNullOrEmpty(activeChainId)
                    || !tree.Recordings.TryGetValue(activeChainId, out chainMember)
                    || chainMember == null)
                    continue;
                if (!string.IsNullOrEmpty(chainMember.ParentBranchPointId))
                    parentBranchPointIds.Add(chainMember.ParentBranchPointId);
            }

            foreach (string parentBpId in parentBranchPointIds)
            {
                BranchPoint bp = FindBranchPoint(tree, parentBpId);
                if (bp == null)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: parent bp '{parentBpId}' missing " +
                        $"for activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (bp.ParentRecordingIds == null || bp.ParentRecordingIds.Count != 1)
                {
                    int parentCount = bp.ParentRecordingIds != null ? bp.ParentRecordingIds.Count : 0;
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip bp={bp.Id} " +
                        $"parentCount={parentCount} activeTarget='{activeReFlyTargetId}' " +
                        "(not a single-parent old-future branch)");
                    continue;
                }

                string parentId = bp.ParentRecordingIds[0];
                if (string.IsNullOrEmpty(parentId) || activeChainIds.Contains(parentId))
                    continue;

                Recording parentRec;
                if (!tree.Recordings.TryGetValue(parentId, out parentRec) || parentRec == null)
                    continue;

                Recording terminalRec = EffectiveState.ResolveChainTerminalRecording(parentRec, tree);
                if (terminalRec == null || string.IsNullOrEmpty(terminalRec.RecordingId))
                    continue;

                if (activeChainIds.Contains(terminalRec.RecordingId)
                    || string.Equals(terminalRec.RecordingId, activeReFlyTargetId,
                        System.StringComparison.Ordinal))
                {
                    continue;
                }

                if (terminalRec.VesselPersistentId != 0u
                    && activeRec.VesselPersistentId != 0u
                    && terminalRec.VesselPersistentId == activeRec.VesselPersistentId)
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' because it shares vessel pid " +
                        $"{terminalRec.VesselPersistentId} with activeTarget='{activeReFlyTargetId}'");
                    continue;
                }

                if (!IsTerminalLinkedToParentBranch(bp, parentRec, terminalRec))
                {
                    ParsekLog.Verbose("MergeDialog",
                        $"CollectActiveReFlyParentChainTerminalTipIds: skip parent terminal " +
                        $"id='{terminalRec.RecordingId}' for bp={bp.Id}; terminal is not linked " +
                        "to the active target's parent branch");
                    continue;
                }

                result.Add(terminalRec.RecordingId);
            }

            return result;
        }

        private static HashSet<string> CollectSameChainRecordingIds(
            RecordingTree tree,
            Recording rec)
        {
            var result = new HashSet<string>(System.StringComparer.Ordinal);
            if (rec == null || string.IsNullOrEmpty(rec.RecordingId))
                return result;

            result.Add(rec.RecordingId);
            if (tree == null || tree.Recordings == null || string.IsNullOrEmpty(rec.ChainId))
                return result;

            foreach (var cand in tree.Recordings.Values)
            {
                if (cand == null || string.IsNullOrEmpty(cand.RecordingId)) continue;
                if (!string.Equals(cand.ChainId, rec.ChainId, System.StringComparison.Ordinal)) continue;
                if (cand.ChainBranch != rec.ChainBranch) continue;
                if (!string.IsNullOrEmpty(rec.TreeId)
                    && !string.Equals(cand.TreeId, rec.TreeId, System.StringComparison.Ordinal))
                    continue;
                result.Add(cand.RecordingId);
            }

            return result;
        }

        private static bool IsTerminalLinkedToParentBranch(
            BranchPoint bp,
            Recording parentRec,
            Recording terminalRec)
        {
            if (bp == null || parentRec == null || terminalRec == null)
                return false;
            if (object.ReferenceEquals(parentRec, terminalRec))
                return true;
            if (string.Equals(parentRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (string.Equals(terminalRec.ChildBranchPointId, bp.Id, System.StringComparison.Ordinal))
                return true;
            if (bp.ParentRecordingIds != null)
            {
                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    if (string.Equals(bp.ParentRecordingIds[i], terminalRec.RecordingId,
                        System.StringComparison.Ordinal))
                        return true;
                }
            }
            if (!string.IsNullOrEmpty(parentRec.ChainId)
                && string.Equals(parentRec.ChainId, terminalRec.ChainId, System.StringComparison.Ordinal)
                && parentRec.ChainBranch == terminalRec.ChainBranch
                && terminalRec.ChainIndex >= parentRec.ChainIndex
                && (string.IsNullOrEmpty(parentRec.TreeId)
                    || string.Equals(parentRec.TreeId, terminalRec.TreeId, System.StringComparison.Ordinal)))
            {
                return true;
            }
            return false;
        }

        private static BranchPoint FindBranchPoint(RecordingTree tree, string branchPointId)
        {
            if (tree == null || tree.BranchPoints == null || string.IsNullOrEmpty(branchPointId))
                return null;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp != null && string.Equals(bp.Id, branchPointId, System.StringComparison.Ordinal))
                    return bp;
            }
            return null;
        }

        /// <summary>
        /// Applies vessel decisions to the tree: nulls VesselSnapshot on recordings
        /// that are marked ghost-only (false in decisions dict).
        /// </summary>
        static void ApplyVesselDecisions(RecordingTree tree, Dictionary<string, bool> decisions)
        {
            if (tree == null || decisions == null)
                return;

            foreach (var kvp in decisions)
            {
                if (!kvp.Value) // ghost-only
                {
                    Recording rec;
                    if (tree.Recordings.TryGetValue(kvp.Key, out rec))
                    {
                        if (rec.VesselSnapshot != null)
                        {
                            // Preserve GhostVisualSnapshot for ghost rendering if not already set
                            if (rec.GhostVisualSnapshot == null)
                                rec.GhostVisualSnapshot = rec.VesselSnapshot.CreateCopy();
                            CrewReservationManager.UnreserveCrewInSnapshot(rec.VesselSnapshot);
                            rec.VesselSnapshot = null;
                            ParsekLog.Info("MergeDialog",
                                $"ApplyVesselDecisions: ghost-only for '{rec.VesselName}' (id={kvp.Key}), " +
                                $"spawn snapshot nulled, ghostVisual={rec.GhostVisualSnapshot != null}");
                        }
                    }
                }
            }
        }

    }
}
