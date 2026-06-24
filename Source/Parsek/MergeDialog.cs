using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for the post-revert merge dialog.
    /// </summary>
    public static partial class MergeDialog
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
        /// Fired after a tree is committed via the merge dialog. Subscribers
        /// receive the just-committed <see cref="RecordingTree"/> so they can
        /// inspect it (e.g. route-creation eligibility) without having to
        /// re-derive which tree was the subject of the commit.
        /// ParsekFlight subscribes to re-evaluate ghost chains; the route
        /// creation dialog subscribes to offer route creation when the
        /// committed tree carries a completed route proof.
        /// </summary>
        internal static System.Action<RecordingTree> OnTreeCommitted;

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
            bool isReFlyDialog = false;
            string mergeLabel = BuildTimelineActionButtonLabel(timelineActionPermanent);
            string title = BuildTimelineActionDialogTitle(timelineActionPermanent);
            if (!object.ReferenceEquals(null, reFlyScenario)
                && reFlyScenario.ActiveReFlySessionMarker != null)
            {
                isReFlyDialog = true;
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
                mergeLabel = BuildTimelineActionButtonLabel(
                    timelineActionPermanent, isReFlyAttempt: true);
                title = BuildTimelineActionDialogTitle(timelineActionPermanent);
                message = BuildReFlyDialogBody(
                    vesselLabel, reFlyDuration, preview, timelineActionPermanent);

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

            // A not-yet-sealable Re-Fly attempt gets a third button so the
            // player can close the slot here instead of making a separate
            // trip to the Recordings window: Commit (don't seal) keeps it
            // open, Merge & Seal commits and seals it now.
            DialogGUIButton[] buttons = (isReFlyDialog && !timelineActionPermanent)
                ? new[]
                {
                    new DialogGUIButton(mergeLabel, () =>
                    {
                        MergeCommit(tree, capturedDecisions, capturedSpawnCount);
                    }),
                    new DialogGUIButton(BuildReFlyMergeAndSealButtonLabel(), () =>
                    {
                        MergeCommit(tree, capturedDecisions, capturedSpawnCount,
                            playerRequestedSeal: true);
                    }),
                    new DialogGUIButton("Discard", () =>
                    {
                        MergeDiscard(tree);
                    })
                }
                : new[]
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
            bool isReFlyDialog = false;
            bool timelineActionPermanent = true;
            if (labels == MergeDialogButtonLabels.ReFlyAttempt)
            {
                isReFlyDialog = true;
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
                timelineActionPermanent =
                    DetermineReFlyTimelineActionIsPermanent(
                        reFlyRec, marker, preview, out labelSource);
                mergeLabel = BuildTimelineActionButtonLabel(
                    timelineActionPermanent, isReFlyAttempt: true);
                message = BuildReFlyDialogBody(
                    vesselLabel, reFlyDuration, preview, timelineActionPermanent);

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
                title = "Confirm: Merge to Timeline";
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

            // Three buttons for a not-yet-sealable Re-Fly attempt so the
            // player can close the slot at scene exit instead of a separate
            // Recordings-window trip. Journal-active keeps the single forced
            // merge button (Discard is gated for merge-journal safety).
            DialogGUIButton[] buttons;
            if (journalActive)
            {
                buttons = new[]
                {
                    new DialogGUIButton(mergeLabel, () => RunPreTransitionAction(
                        isMerge: true,
                        preCommitFinalize: preCommitFinalize,
                        postChoice: postChoice)),
                };
            }
            else if (isReFlyDialog && !timelineActionPermanent)
            {
                buttons = new[]
                {
                    new DialogGUIButton(mergeLabel, () => RunPreTransitionAction(
                        isMerge: true,
                        preCommitFinalize: preCommitFinalize,
                        postChoice: postChoice)),
                    new DialogGUIButton(BuildReFlyMergeAndSealButtonLabel(), () =>
                        RunPreTransitionAction(
                            isMerge: true,
                            preCommitFinalize: preCommitFinalize,
                            postChoice: postChoice,
                            playerRequestedSeal: true)),
                    new DialogGUIButton(discardLabel, () => RunPreTransitionAction(
                        isMerge: false,
                        preCommitFinalize: preCommitFinalize,
                        postChoice: postChoice)),
                };
            }
            else
            {
                buttons = new[]
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
            }

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
            System.Action discardAction,
            RecordingTree priorTreeOverride = null)
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
            //
            // No-session Case B (2026-05-17 follow-up): callers in the
            // no-session pre-switch path pass the live activeTree via
            // priorTreeOverride. We skip the session-id-based resolver
            // and render the dialog body directly from that tree (with
            // ResolveDialogBodyDuration falling back to tree-wide
            // duration in the absence of a session — already supported).
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
            if (priorTreeOverride != null)
            {
                priorTree = priorTreeOverride;
                // Case B: tree comes straight from the caller (live
                // ActiveTreeForDisplay). No slot resolution and no
                // committed-slot guard — Case B never crosses a
                // CommittedTrees boundary because we hand the tree in
                // directly.
                priorTreeSlot = RecordingStore.TreeSlotSource.Active;
            }
            else
            {
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

                // M1 (PR #876 round-5 review): if the session's tree
                // resolved to the CommittedTrees slot specifically,
                // refuse to spawn the dialog. The Merge button would
                // call MergeCommit -> the M1 guard there would refuse
                // (the tree isn't in pendingTree), and the player would
                // see a dialog that does nothing. The session marker
                // probably needs cleanup separately, but that is out
                // of scope for this PR — defensive log + refuse here
                // so we don't trigger the misbehaving Merge path.
                // Skipped on the no-session Case B branch above
                // because the caller passes the active tree directly.
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

            // MED2 (PR #876 round-6 review): gate the generic Merge / Discard
            // log lines on the session-armed path. For Case B
            // (priorTreeOverride != null), `priorSessionId` is always
            // "<none>" and the Case B handler emits its own
            // `*-chosen-no-session` log line with the priorTreeId — the
            // generic line would be redundant noise.
            bool isCaseB = priorTreeOverride != null;
            DialogGUIButton[] buttons = new[]
            {
                new DialogGUIButton("Merge", () =>
                {
                    buttonClicked = true;
                    if (!isCaseB)
                    {
                        ParsekLog.Info("SwitchIntentPatch",
                            $"pre-switch-dialog-merge-chosen priorSessionId={priorSessionIdStr} " +
                            $"newTargetPid={target.persistentId}");
                    }
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
                    if (!isCaseB)
                    {
                        ParsekLog.Info("SwitchIntentPatch",
                            $"pre-switch-dialog-discard-chosen priorSessionId={priorSessionIdStr} " +
                            $"newTargetPid={target.persistentId}");
                    }
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
                    //
                    // LOW 3 (PR #876 round-6 review): emit a case
                    // discriminator so the reader can tell whether the
                    // respawn was for the session-armed (Case A) or
                    // no-session (Case B) path. `priorSessionId` is
                    // "<none>" on Case B and the priorTreeId is the
                    // identifying field instead.
                    if (isCaseB)
                    {
                        ParsekLog.Info("SwitchIntentPatch",
                            $"pre-switch-dialog-esc-refused-respawning " +
                            $"case=case-B-no-session " +
                            $"priorTreeId={priorTreeOverride?.Id ?? "<null>"} " +
                            $"newTargetPid={target.persistentId}");
                    }
                    else
                    {
                        ParsekLog.Info("SwitchIntentPatch",
                            $"pre-switch-dialog-esc-refused-respawning " +
                            $"case=case-A-session " +
                            $"priorSessionId={priorSessionIdStr} newTargetPid={target.persistentId}");
                    }
                    // Recursive re-spawn through the same helper. If THAT spawn
                    // fails, the lock is released and we fall back to the
                    // defensive supersede path documented at the spawn-failed
                    // log line below. The recursion is bounded by the player's
                    // own input — every Esc press just re-opens the dialog.
                    ClearPendingFlag("pre-switch-dialog popup teardown (re-spawn)");
                    ShowPreSwitchDecisionDialog(
                        target, mergeAction, discardAction, priorTreeOverride);
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
            System.Action postChoice,
            bool playerRequestedSeal = false)
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
                MergeCommit(pending, decisions, spawnCount, playerRequestedSeal);
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
        /// <paramref name="playerRequestedSeal"/> is set ONLY by the
        /// "Merge &amp; Seal" button shown on a not-yet-sealable Re-Fly
        /// attempt; it closes the re-fly slot after the commit (same effect
        /// as the Recordings-window Seal button) by flipping the slot's
        /// effective tip to <see cref="MergeState.Immutable"/>. The
        /// "Commit (don't seal)" button leaves it <c>false</c>, so the tip
        /// stays <see cref="MergeState.CommittedProvisional"/> (open).
        /// </summary>
        internal static void MergeCommit(
            RecordingTree tree,
            Dictionary<string, bool> decisions,
            int spawnCount,
            bool playerRequestedSeal = false)
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
                TryCommitReFlySupersede(activeReFlyTargetHint, playerRequestedSeal);

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
            OnTreeCommitted?.Invoke(tree);
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
        /// Phase 8 of Rewind-to-Staging (design §6.6 steps 2-3): if a re-fly
        /// session is active at merge time, write the supersede relations for
        /// the origin subtree and flip the provisional's MergeState. Skipped
        /// silently when no session is active — the regular tree-merge flow
        /// is unchanged.
        /// </summary>
        internal static ReFlyMergeCommitResult TryCommitReFlySupersede()
            => TryCommitReFlySupersede(null, playerRequestedSeal: false);

        private static ReFlyMergeCommitResult TryCommitReFlySupersede(
            RecordingIdentityHint activeReFlyTargetHint,
            bool playerRequestedSeal)
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

            // "Merge & Seal": the player asked to close the slot now even
            // though the outcome did not auto-seal. Reuse the same path the
            // Recordings-window Seal button uses, which flips the slot's
            // effective tip CommittedProvisional -> Immutable (the single
            // open/closed source of truth after collapse-seal-into-mergestate).
            // "Commit (don't seal)" leaves playerRequestedSeal false, so the
            // tip stays CommittedProvisional (open / re-flyable) and the slot
            // remains an Unfinished Flight. Failure to resolve the slot is
            // non-fatal — the merge already committed, so warn and point the
            // player at the manual Seal affordance. TrySeal does its own
            // persist + RP reap, a second durable pass after the merge
            // journal's; that redundancy is intentional and cheap for a
            // one-off interactive action.
            if (playerRequestedSeal)
            {
                ApplyPlayerRequestedSeal(provisional);
            }
            else
            {
                // Diagnostic: make "Commit (don't seal)" leave a positive trace
                // so a log reader can tell it apart from "Merge & Seal" (which
                // logs via ApplyPlayerRequestedSeal). Without this, only the
                // seal path was visible. The player declined to seal, but the
                // merge's terminal classification may still have auto-sealed the
                // tip to Immutable (e.g. a landed / destroyed outcome via
                // SupersedeCommit), so report the provisional's actual resulting
                // MergeState rather than assuming it stayed open — the tip
                // MergeState is the single open/closed source of truth.
                MergeState resultState = provisional != null
                    ? provisional.MergeState
                    : MergeState.NotCommitted;
                string disposition = resultState == MergeState.Immutable
                    ? "tip auto-sealed to Immutable by terminal classification"
                    : $"slot left open at {resultState}";
                ParsekLog.Info("MergeDialog",
                    $"Re-Fly merge committed WITHOUT player seal (player chose Commit-don't-seal); " +
                    $"{disposition} rec={provisional?.RecordingId ?? "<no-id>"}");
            }

            return ReFlyMergeCommitResult.Completed;
        }

        // internal (not private) so the MergeAndSealReFlyClosesSlot in-game
        // test can drive the exact Merge & Seal post-merge step.
        internal static void ApplyPlayerRequestedSeal(Recording provisional)
        {
            string recId = provisional?.RecordingId ?? "<no-id>";
            if (provisional == null)
            {
                ParsekLog.Warn("MergeDialog",
                    "Merge & Seal: resolved provisional is null after merge — " +
                    "cannot seal; player can seal from the Recordings window");
                ParsekLog.ScreenMessage(
                    "Merged, but could not seal — seal it from the Recordings window", 4f);
                return;
            }

            string sealReason;
            bool sealed_ = UnfinishedFlightSealHandler.TrySeal(provisional, out sealReason);
            if (sealed_)
            {
                ParsekLog.Info("MergeDialog",
                    $"Merge & Seal: sealed re-fly slot after merge rec={recId}");
                ParsekLog.ScreenMessage("Re-Fly slot sealed", 3f);
            }
            else
            {
                ParsekLog.Warn("MergeDialog",
                    $"Merge & Seal: seal failed after merge rec={recId} " +
                    $"reason={sealReason ?? "<none>"} — player can seal from the Recordings window");
                ParsekLog.ScreenMessage(
                    "Merged, but could not seal — seal it from the Recordings window", 4f);
            }
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
