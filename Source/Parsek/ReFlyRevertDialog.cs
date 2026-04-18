using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7): intercepts the stock
    /// Revert-to-Launch flow while a re-fly session is active and offers a
    /// 3-option popup:
    ///
    /// <list type="bullet">
    ///   <item><description><b>Retry from Rewind Point</b> — discard the current provisional, generate a fresh <c>SessionId</c>, and re-invoke the rewind via <see cref="RewindInvoker.StartInvoke"/> using the same <see cref="RewindPoint"/> + <see cref="ChildSlot"/>.</description></item>
    ///   <item><description><b>Full Revert (Discard Re-fly)</b> — purge the entire tree via <see cref="TreeDiscardPurge.PurgeTree"/> (clears RPs, supersedes, tombstones, marker, journal) and then run the stock revert path.</description></item>
    ///   <item><description><b>Continue Flying</b> — dismiss the popup and resume flight with no state changes.</description></item>
    /// </list>
    ///
    /// <para>
    /// The dialog mirrors <see cref="MergeDialog.ShowTreeDialog"/>'s
    /// <c>PopupDialog.SpawnPopupDialog</c> + <see cref="MultiOptionDialog"/>
    /// pattern and sets an input lock so the player cannot interact with the
    /// flight scene while deciding.
    /// </para>
    /// </summary>
    public static class ReFlyRevertDialog
    {
        internal const string DialogLockId = "ParsekReFlyRevertDialog";
        internal const string DialogName = "ParsekReFlyRevert";
        internal const string UiTag = "RewindUI";
        internal const string SessionTag = "ReFlySession";

        /// <summary>
        /// Test seam: set to a non-null action in unit tests to observe that
        /// <see cref="Show"/> was invoked without trying to spawn a real
        /// <see cref="PopupDialog"/> (which requires a live Unity canvas).
        /// The hook receives the marker's <c>SessionId</c>.
        /// </summary>
        internal static Action<string> ShowHookForTesting;

        /// <summary>
        /// Test seam: set to a non-null action in unit tests to capture the
        /// fully composed dialog body text (lets tests assert the context
        /// branch chose the right copy without spawning a real popup).
        /// Receives <c>(sessionId, body)</c>. Fires in addition to — not
        /// instead of — <see cref="ShowHookForTesting"/>.
        /// </summary>
        internal static Action<string, string> BodyHookForTesting;

        /// <summary>
        /// True while <see cref="Show"/> has spawned the popup and no callback
        /// has fired yet. Exposed for the in-game test harness so it can
        /// inspect whether the dialog appeared without having to parse the
        /// scene's UI tree.
        /// </summary>
        public static bool DialogVisible { get; private set; }

        /// <summary>
        /// Back-compat overload: default revert-target context is
        /// <see cref="RevertTarget.Launch"/>. Equivalent to
        /// <see cref="Show(ReFlySessionMarker, RevertTarget, Action, Action, Action)"/>
        /// with <c>target = Launch</c>.
        /// </summary>
        public static void Show(
            ReFlySessionMarker marker,
            Action onRetry,
            Action onFullRevert,
            Action onCancel)
        {
            Show(marker, RevertTarget.Launch, onRetry, onFullRevert, onCancel);
        }

        /// <summary>
        /// Spawns the 3-option re-fly revert popup and wires each button to
        /// the supplied callback. Callbacks must be non-null; a null callback
        /// is logged and treated as a no-op so a dropped wire does not strand
        /// the player behind the popup.
        ///
        /// <para>
        /// <paramref name="target"/> selects the body copy variant:
        /// <see cref="RevertTarget.Launch"/> keeps the launchpad-oriented
        /// wording, <see cref="RevertTarget.Prelaunch"/> swaps in
        /// VAB/SPH-oriented wording and clarifies that Retry still lands
        /// the player back in FLIGHT.
        /// </para>
        /// </summary>
        internal static void Show(
            ReFlySessionMarker marker,
            RevertTarget target,
            Action onRetry,
            Action onFullRevert,
            Action onCancel)
        {
            if (marker == null)
            {
                ParsekLog.Warn(UiTag, "ReFlyRevertDialog.Show: marker is null — refusing to spawn dialog");
                return;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            ParsekLog.Info(SessionTag, $"Revert dialog shown sess={sessionId} target={target}");

            string title = "Revert during re-fly";
            string body = BuildBody(target);

            var bodyHook = BodyHookForTesting;
            if (bodyHook != null)
            {
                try { bodyHook(sessionId, body); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog BodyHookForTesting threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            var hook = ShowHookForTesting;
            if (hook != null)
            {
                DialogVisible = true;
                try { hook(sessionId); }
                finally { DialogVisible = false; }
                return;
            }

            LockInput();
            DialogVisible = true;

            // Button handlers. Each releases the input lock + clears the
            // visible flag before dispatching, so a callback that spawns its
            // own popup does not fight the lock.
            DialogGUIButton retryButton = new DialogGUIButton("Retry from Rewind Point", () =>
            {
                DialogVisible = false;
                ClearLock();
                if (onRetry == null)
                {
                    ParsekLog.Warn(UiTag, $"ReFlyRevertDialog: Retry button had null callback sess={sessionId}");
                    return;
                }
                try { onRetry(); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog Retry callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            });

            DialogGUIButton fullRevertButton = new DialogGUIButton("Full Revert (Discard Re-fly)", () =>
            {
                DialogVisible = false;
                ClearLock();
                if (onFullRevert == null)
                {
                    ParsekLog.Warn(UiTag, $"ReFlyRevertDialog: Full Revert button had null callback sess={sessionId}");
                    return;
                }
                try { onFullRevert(); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog Full Revert callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            });

            DialogGUIButton cancelButton = new DialogGUIButton("Continue Flying", () =>
            {
                DialogVisible = false;
                ClearLock();
                if (onCancel == null)
                {
                    ParsekLog.Warn(UiTag, $"ReFlyRevertDialog: Cancel button had null callback sess={sessionId}");
                    return;
                }
                try { onCancel(); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog Cancel callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            });

            PopupDialog.DismissPopup(DialogName);
            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new MultiOptionDialog(
                    DialogName,
                    body,
                    title,
                    HighLogic.UISkin,
                    retryButton,
                    fullRevertButton,
                    cancelButton),
                false,
                HighLogic.UISkin);
        }

        /// <summary>
        /// Builds the dialog body copy for the given revert-target context.
        /// Launch variant keeps the launchpad-oriented wording; Prelaunch
        /// variant swaps in VAB/SPH wording and clarifies that Retry still
        /// returns the player to FLIGHT (not the editor) regardless of
        /// which button they clicked.
        /// </summary>
        internal static string BuildBody(RevertTarget target)
        {
            if (target == RevertTarget.Prelaunch)
            {
                return
                    "You are re-flying an unfinished mission. What would you like to do?\n\n" +
                    "- Retry from Rewind Point: discard this attempt and re-load the " +
                    "rewind-point quicksave. This returns you to FLIGHT at the split " +
                    "moment regardless of which Revert button you clicked. The " +
                    "Unfinished Flight entry stays available so you can try again.\n" +
                    "- Full Revert (Discard Re-fly): throw away the current re-fly attempt " +
                    "and clear the rewind point + supersede / tombstone state for this split, " +
                    "then let the stock Revert continue and return you to the VAB or SPH. " +
                    "The committed recordings (original launch, any prior siblings) stay in " +
                    "the timeline. Career state stays where it is now.\n" +
                    "- Continue Flying: keep the current attempt; do nothing.";
            }

            return
                "You are re-flying an unfinished mission. What would you like to do?\n\n" +
                "- Retry from Rewind Point: discard this attempt and re-load the " +
                "rewind-point quicksave. The Unfinished Flight entry stays available " +
                "so you can try again.\n" +
                "- Full Revert (Discard Re-fly): throw away the current re-fly attempt " +
                "and clear the rewind point + supersede / tombstone state for this split, " +
                "then let the stock Revert continue and return you to the launchpad. " +
                "The committed recordings (original launch, any prior siblings) stay in " +
                "the timeline. Career state stays where it is now.\n" +
                "- Continue Flying: keep the current attempt; do nothing.";
        }

        internal static void LockInput()
        {
            InputLockManager.SetControlLock(ControlTypes.All, DialogLockId);
            ParsekLog.Verbose(UiTag, $"ReFlyRevertDialog input lock set ({DialogLockId})");
        }

        internal static void ClearLock()
        {
            InputLockManager.RemoveControlLock(DialogLockId);
            ParsekLog.Verbose(UiTag, $"ReFlyRevertDialog input lock cleared ({DialogLockId})");
        }

        /// <summary>Test-only: clears the DialogVisible flag without going through the popup path.</summary>
        internal static void ResetForTesting()
        {
            DialogVisible = false;
            ShowHookForTesting = null;
            BodyHookForTesting = null;
        }
    }
}
