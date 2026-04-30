using System;
using UnityEngine;

namespace Parsek
{
    /// <summary>
    /// Phase 12 of Rewind-to-Staging (design §6.7): intercepts the stock
    /// Revert-to-Launch / Revert-to-VAB/SPH flow while a re-fly session is
    /// active and offers a 3-option popup:
    ///
    /// <list type="bullet">
    ///   <item><description><b>Retry from Rewind Point</b> — discard the current provisional, generate a fresh <c>SessionId</c>, and re-invoke the rewind via <see cref="RewindInvoker.StartInvoke"/> using the same <see cref="RewindPoint"/> + <see cref="ChildSlot"/>.</description></item>
    ///   <item><description><b>Discard Re-fly</b> — session-scoped cleanup: remove the provisional re-fly recording, promote the origin RP to persistent, clear marker + journal, reload the RP quicksave, and transition to the Space Center (Launch click) or VAB / SPH (Prelaunch click). The tree's other Rewind Points, supersede relations, and tombstones stay intact; Unfinished Flights still shows this split so the player can try again.</description></item>
    ///   <item><description><b>Continue Flying</b> — dismiss the popup and resume flight with no state changes.</description></item>
    /// </list>
    ///
    /// <para>
    /// The dialog mirrors <see cref="MergeDialog.ShowTreeDialog"/>'s
    /// <c>PopupDialog.SpawnPopupDialog</c> + <see cref="MultiOptionDialog"/>
    /// pattern and sets an input lock so the player cannot interact with the
    /// flight scene while deciding. When
    /// <see cref="ParsekScenario.ActiveMergeJournal"/> is non-null the Discard
    /// Re-fly button is hidden (the handler also refuses defensively) — a
    /// discard mid-merge would race the journal finisher's rollback.
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
        /// Test seam: captures which buttons the dialog composed. Lets unit
        /// tests assert the journal-active gate hid the Discard button
        /// without parsing the live Unity UI tree. Receives
        /// <c>(sessionId, includeDiscard)</c>.
        /// </summary>
        internal static Action<string, bool> ButtonsHookForTesting;

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
            Action onDiscardReFly,
            Action onCancel)
        {
            Show(marker, RevertTarget.Launch, onRetry, onDiscardReFly, onCancel);
        }

        /// <summary>
        /// Spawns the 3-option re-fly revert popup and wires each button to
        /// the supplied callback. Callbacks must be non-null; a null callback
        /// is logged and treated as a no-op so a dropped wire does not strand
        /// the player behind the popup.
        ///
        /// <para>
        /// <paramref name="target"/> selects the body copy variant:
        /// <see cref="RevertTarget.Launch"/> says "returns you to the Space
        /// Center"; <see cref="RevertTarget.Prelaunch"/> says "returns you
        /// to the VAB or SPH" and clarifies that Retry still lands the
        /// player back in FLIGHT.
        /// </para>
        ///
        /// <para>
        /// When <see cref="ParsekScenario.ActiveMergeJournal"/> is non-null
        /// the Discard Re-fly button is omitted entirely (primary UX signal
        /// to the player) and the dialog still shows Retry + Continue Flying.
        /// The handler also refuses defensively if called anyway.
        /// </para>
        /// </summary>
        internal static void Show(
            ReFlySessionMarker marker,
            RevertTarget target,
            Action onRetry,
            Action onDiscardReFly,
            Action onCancel)
        {
            if (marker == null)
            {
                ParsekLog.Warn(UiTag, "ReFlyRevertDialog.Show: marker is null — refusing to spawn dialog");
                return;
            }

            string sessionId = marker.SessionId ?? "<no-id>";
            ParsekLog.Info(SessionTag, $"Revert dialog shown sess={sessionId} target={target}");

            string title = "Revert during Re-Fly";
            bool journalActive = IsMergeJournalActive();
            string body = BuildBody(target, journalActive);

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

            var buttonsHook = ButtonsHookForTesting;
            if (buttonsHook != null)
            {
                try { buttonsHook(sessionId, !journalActive); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog ButtonsHookForTesting threw: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (journalActive)
            {
                ParsekLog.Info(SessionTag,
                    $"Revert dialog: merge journal active sess={sessionId} — Discard Re-fly button hidden");
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

            DialogGUIButton discardButton = new DialogGUIButton("Discard Re-Fly", () =>
            {
                DialogVisible = false;
                ClearLock();
                if (onDiscardReFly == null)
                {
                    ParsekLog.Warn(UiTag, $"ReFlyRevertDialog: Discard Re-fly button had null callback sess={sessionId}");
                    return;
                }
                try { onDiscardReFly(); }
                catch (Exception ex)
                {
                    ParsekLog.Error(UiTag,
                        $"ReFlyRevertDialog Discard Re-fly callback threw: {ex.GetType().Name}: {ex.Message}");
                }
            });

            DialogGUIButton cancelButton = new DialogGUIButton("Continue Playing", () =>
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

            MultiOptionDialog dialog = journalActive
                ? new MultiOptionDialog(
                    DialogName,
                    body,
                    title,
                    HighLogic.UISkin,
                    retryButton,
                    cancelButton)
                : new MultiOptionDialog(
                    DialogName,
                    body,
                    title,
                    HighLogic.UISkin,
                    retryButton,
                    discardButton,
                    cancelButton);

            PopupDialog.SpawnPopupDialog(
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                dialog,
                false,
                HighLogic.UISkin);
        }

        /// <summary>
        /// Builds the dialog body copy for the given revert-target context.
        /// Launch variant says "returns you to the Space Center"; Prelaunch
        /// variant says "returns you to the VAB or SPH" and clarifies that
        /// Retry still returns the player to FLIGHT (not the editor)
        /// regardless of which button they clicked. When
        /// <paramref name="journalActive"/> is true the Discard Re-fly
        /// bullet is replaced with a notice explaining the button is hidden
        /// while a merge is in progress.
        /// </summary>
        internal static string BuildBody(RevertTarget target, bool journalActive = false)
        {
            string retryLine = target == RevertTarget.Prelaunch
                ? "- Retry from Rewind Point: discard this attempt and re-load the " +
                  "rewind-point quicksave. This returns you to FLIGHT at the split " +
                  "moment regardless of which Revert button you clicked. The " +
                  "Unfinished Flight entry stays available so you can try again.\n"
                : "- Retry from Rewind Point: discard this attempt and re-load the " +
                  "rewind-point quicksave. The Unfinished Flight entry stays available " +
                  "so you can try again.\n";

            string discardLine;
            if (journalActive)
            {
                discardLine =
                    "- Discard Re-Fly is unavailable while a merge is in progress. " +
                    "Finish or roll back the merge (load the save) and try again.\n";
            }
            else if (target == RevertTarget.Prelaunch)
            {
                discardLine =
                    "- Discard Re-Fly: throw away the current Re-Fly attempt and reload " +
                    "the rewind point; returns you to the VAB or SPH at the moment you " +
                    "opened the Rewind Point. The tree's other Rewind Points stay intact. " +
                    "STASH still shows this entry so you can try again.\n";
            }
            else
            {
                discardLine =
                    "- Discard Re-Fly: throw away the current Re-Fly attempt and reload " +
                    "the rewind point; returns you to the Space Center at the moment you " +
                    "opened the Rewind Point. The tree's other Rewind Points stay intact. " +
                    "STASH still shows this entry so you can try again.\n";
            }

            return
                "You are re-flying an unfinished mission. What would you like to do?\n\n" +
                retryLine +
                discardLine +
                "- Continue Playing: keep the current attempt; do nothing.";
        }

        private static bool IsMergeJournalActive()
        {
            var scenario = ParsekScenario.Instance;
            if (ReferenceEquals(null, scenario)) return false;
            return scenario.ActiveMergeJournal != null;
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
            ButtonsHookForTesting = null;
        }
    }
}
