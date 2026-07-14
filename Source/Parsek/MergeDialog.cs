using System.Collections.Generic;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Static methods for the post-revert merge dialog.
    /// </summary>
    public static partial class MergeDialog
    {
        // internal (M-C1): the AnswerMergeDialog seam verb locates the live PopupDialog by
        // this name and invokes the chosen DialogGUIButton's own callback. Widening from
        // private -> internal exposes no behavior (the const value is unchanged).
        internal const string DialogName = "ParsekMerge";
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

    }
}
