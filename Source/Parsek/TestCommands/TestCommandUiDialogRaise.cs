using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>What a lane is allowed to PRESS on a raised dialog.</summary>
    internal enum UiDialogPressPolicy
    {
        /// <summary>Every button is harmless (an <c>OK</c>-only informational popup), so
        /// <c>press=</c> may name any of them.</summary>
        AnyButton = 0,

        /// <summary>A confirm dialog: the confirm button MUTATES the save (a wipe, a warp,
        /// a permanent seal) and is NOT pressable through the seam; only the cancel-shaped
        /// button named by <see cref="UiRaisableDialog.SafeButton"/> is.</summary>
        SafeButtonOnly = 1,
    }

    /// <summary>
    /// One dialog <c>UiAction op=raise</c> can put on screen: its wire token, the live
    /// <c>MultiOptionDialog</c> name to confirm it by, what it needs from the host, and
    /// what a lane may press on it.
    /// </summary>
    internal struct UiRaisableDialog
    {
        /// <summary>The lower-case wire token. Stable: a spec names it and a capture label
        /// is built from it.</summary>
        internal string Name;

        /// <summary>The <c>MultiOptionDialog</c> name literal the spawn site passes. The
        /// settle confirms the standing popup carries EXACTLY this, so a raise that spawned
        /// nothing (a guarded early return) cannot be read as a success over some OTHER
        /// Parsek popup that happened to be up.</summary>
        internal string PopupName;

        /// <summary>The dialog's player-facing title, for the response and the log line.
        /// Reported from the table rather than read back, because the live title is a
        /// private field the report op already reaches with reflection - one reader is
        /// enough, and a table entry that drifts from the source is caught by
        /// <c>UiRaisableDialogSourceGateTests</c>.</summary>
        internal string Title;

        /// <summary>Button labels in layout order, as the spawn site writes them.</summary>
        internal string[] Buttons;

        /// <summary>True when the raise needs a committed <c>Recording</c> from the live
        /// store. A host with none answers
        /// <see cref="TestCommandUiDialogRaise.TargetUnavailableReason"/> rather than
        /// raising nothing and reporting OK.</summary>
        internal bool NeedsRecording;

        /// <summary>Whether the raise needs the recording to additionally resolve a rewind
        /// owner (<c>RecordingStore.GetRewindRecording</c>), which the spawn site silently
        /// returns on.</summary>
        internal bool NeedsRewindOwner;

        internal UiDialogPressPolicy Press;

        /// <summary>The one button <c>press=</c> may name under
        /// <see cref="UiDialogPressPolicy.SafeButtonOnly"/>, or null.</summary>
        internal string SafeButton;

        /// <summary>True when the spawn site takes a <c>ControlTypes.All</c> input lock
        /// that only its own button callbacks release, so a dismiss WITHOUT a press must
        /// clear it explicitly or the lane wedges behind it.</summary>
        internal bool OwnsInputLock;
    }

    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=raise</c> and <c>op=dismiss</c>: the
    /// half of the GUI census that puts a modal on screen and LEAVES IT STANDING to be
    /// photographed.
    ///
    /// <para><b>WHY THE CENSUS NEEDED THIS.</b> After wave 2 the <c>modal dialogs</c> row of
    /// <c>design-gui-inventory.md</c> section 2 stood at <b>0 of 21</b>, and not for want of
    /// a reporting op: <c>op=dialog</c> shipped and every one of the six wave-2 lanes
    /// answered <c>open=false count=0</c> with it, because no seam path could raise a Parsek
    /// modal at all. The three that come close each refuse by design -
    /// <c>ExitToSpaceCenter</c> answers <c>REJECTED dialog-required</c> rather than driving
    /// an exit into an outstanding merge decision, <c>AnswerMergeDialog</c> raises AND
    /// presses inside one completion pass (and is <c>markerLive</c>-gated on top), and
    /// <c>SimulateStockSwitchClick</c> turns all three pre-switch cases into typed
    /// REJECTEDs. All three refusals are wedge guards and none of them should change. What
    /// was missing was a DIFFERENT door: a raise that calls the production spawn site and
    /// then stops.</para>
    ///
    /// <para><b>THE CLOSED SET IS THE DESIGN.</b> Only dialogs whose spawn is reachable by
    /// a pure in-process call with data the host already carries are in
    /// <see cref="Dialogs"/> - no scene transition, no live Re-Fly marker, no synthesised
    /// <c>Vessel</c>. The rest stay FILED with their reason in
    /// <c>docs/dev/todo-and-known-bugs.md</c> rather than being faked: a dialog raised over
    /// synthetic state photographs a screen the game cannot actually be in, which is the
    /// one thing a census must not produce.</para>
    ///
    /// <para><b>DISMISS WITHOUT PRESSING IS THE DEFAULT, and <c>press=</c> is opt-in.</b>
    /// Most of these dialogs' confirm buttons MUTATE the save - a wipe deletes every
    /// recording, a seal is permanent, a warp moves UT - so a census lane that pressed them
    /// would destroy the very fixture it is photographing. <c>op=dismiss</c> therefore
    /// defaults to <c>PopupDialog.DismissPopup</c>, and <c>press=</c> must name a button
    /// the dialog's <see cref="UiDialogPressPolicy"/> allows
    /// (<see cref="PressNotAllowedReason"/>).</para>
    ///
    /// <para><b>ONE AT A TIME.</b> A raise while any Parsek popup stands is
    /// <see cref="AlreadyOpenReason"/>. Parsek's own contract is that at most one modal is
    /// up (every spawn site dismisses first), so raising a second would both violate it and
    /// make the following <c>op=dialog</c> report an arbitrary one of two - which is exactly
    /// the <c>count &gt; 1</c> state that op flags as a finding.</para>
    /// </summary>
    internal static class TestCommandUiDialogRaise
    {
        // ----- arg keys -----

        /// <summary>
        /// Which dialog to raise / dismiss.
        ///
        /// <para><b>WHY IT IS NOT SPELLED <c>dialog</c>.</b> That key is already OWNED by
        /// <c>AnswerMergeDialog</c> (<c>TestCommandUiDialog.DialogArg</c>), and
        /// <c>hlib.VERB_SCOPED_CLOSED_ARGS</c> admits exactly ONE owner verb per key - a
        /// second owner makes every <c>UiAction popup=</c> step a hard pre-launch error
        /// ("only the AnswerMergeDialog verb reads it"). The house answer to that collision
        /// is a different word, which is the same reason <c>op=find</c> spells its control
        /// filter <c>ctrl</c> rather than <c>kind</c>. So: <c>popup=</c>.</para>
        /// </summary>
        internal const string PopupArg = "popup";

        /// <summary>
        /// Optional on <c>op=dismiss</c>: press this button instead of dismissing the popup
        /// outright.
        ///
        /// <para>The value is the button's LABEL, verbatim, and every pressable label is
        /// space-free by construction (<c>OK</c> on the two informational popups,
        /// <c>Cancel</c> on every confirm) - which matters, because the command wire is
        /// space-separated <c>key=value</c> pairs and <c>TestCommandProtocol</c> encodes
        /// only <c>%</c> and <c>=</c>. A future dialog whose safe button carries a space
        /// needs the encoder widened, not a second spelling here.</para>
        /// </summary>
        internal const string PressArg = "press";

        /// <summary>Every accepted <see cref="PressArg"/> value across the table,
        /// comma-joined: the closed set <c>hlib</c> mirrors. Derived from
        /// <see cref="Dialogs"/> rather than written out, so a table row cannot introduce a
        /// label the harness validates as illegal.</summary>
        internal static string ValidPressButtons
        {
            get
            {
                var seen = new List<string>();
                foreach (UiRaisableDialog d in DialogTable)
                {
                    if (d.Buttons == null) continue;
                    for (int i = 0; i < d.Buttons.Length; i++)
                    {
                        if (!IsPressAllowed(d, d.Buttons[i])) continue;
                        if (!seen.Contains(d.Buttons[i])) seen.Add(d.Buttons[i]);
                    }
                }
                return string.Join(",", seen.ToArray());
            }
        }

        // ----- wire tokens -----

        internal const string ActionBlockedDialog = "actionblocked";
        internal const string SaveFailedDialog = "savefailed";
        internal const string WipeRecordingsDialog = "wiperecordings";
        internal const string WipeMilestonesDialog = "wipemilestones";
        internal const string RewindDialog = "rewind";
        internal const string FastForwardDialog = "fastforward";
        internal const string SealDialog = "seal";

        // ----- reject / error reasons -----

        /// <summary>No <c>popup=</c> arg. REQUIRED: there is no default modal, and
        /// guessing one would photograph a dialog the lane never asked for.</summary>
        internal const string PopupArgMissingReason = "popup-arg-missing";

        /// <summary>A <c>popup=</c> value outside <see cref="Dialogs"/>. The message
        /// carries the full list, so a spec author does not read the source for the
        /// spelling.</summary>
        internal const string PopupUnknownReason = "popup-unknown";

        /// <summary>A raise while another Parsek popup stands. REJECTED - see the class
        /// header.</summary>
        internal const string AlreadyOpenReason = "dialog-already-open";

        /// <summary>PRE-CALL: the raise needs something the host does not carry (no
        /// committed recording; no rewind owner for the one that needs one). REJECTED
        /// naming what was missing, rather than calling a spawn site that silently returns
        /// and then reporting a modal that is not there.</summary>
        internal const string TargetUnavailableReason = "dialog-target-unavailable";

        /// <summary>POST-SETTLE on <c>op=raise</c>: the spawn site ran and no popup with
        /// the table's <see cref="UiRaisableDialog.PopupName"/> stands after a drawn frame.
        /// ERROR - we called production code and the game did not follow.</summary>
        internal const string NotRaisedReason = "dialog-not-raised";

        /// <summary><c>op=dismiss</c> with no popup of that name standing. REJECTED: a
        /// cheerful OK over nothing would let a lane's raise step fail silently and its
        /// capture photograph an empty screen under a dialog label.</summary>
        internal const string NotOpenReason = "dialog-not-open";

        /// <summary>POST-SETTLE on <c>op=dismiss</c>: the popup is still standing.</summary>
        internal const string NotDismissedReason = "dialog-not-dismissed";

        /// <summary>A <c>press=</c> value that is not one of the dialog's buttons.</summary>
        internal const string PressUnknownReason = "press-unknown";

        /// <summary>A <c>press=</c> naming a button the dialog's policy forbids - i.e. a
        /// confirm that would wipe, seal or warp. The refusal is the point: a census lane
        /// must be unable to destroy its own fixture by naming the wrong
        /// button.</summary>
        internal const string PressNotAllowedReason = "press-not-allowed";

        // ----- the table -----
        //
        // ORDER is cheapest-first: the two that need nothing at all, then the two that need
        // only a count, then the three that need a committed recording. A census lane reads
        // down it, and a host that carries no recordings still photographs the first four.
        //
        // WHAT IS NOT HERE, and why - each of these is FILED in
        // docs/dev/todo-and-known-bugs.md rather than raised over synthetic state:
        //   - the tree merge dialog (`ParsekMerge`): its spawn takes a RecordingTree and
        //     BOTH its buttons act on it (commit / discard), so raising it needs either the
        //     live pending tree - which a census host does not have - or a synthetic one,
        //     whose commit would write invented history into the fixture.
        //   - the pre-switch decision dialog (`ParsekPreSwitch`): needs a live Vessel (so
        //     FLIGHT only) and RE-SPAWNS ITSELF on any non-button teardown by design, so a
        //     dismiss-without-press cannot close it at all.
        //   - the ghost icon context menu (`ParsekGhostIconMenu`): spawned inside a Harmony
        //     Prefix over a live ghost ProtoVessel in map view; there is no method to call.
        //   - the Tracking Station ghost popup: its host exists only in TRACKSTATION, which
        //     hosts no ParsekUI and therefore no UiAction at all.
        //   - Re-Fly invoke / Re-Fly revert: a RewindPoint with a child slot, and a live
        //     ReFlySessionMarker, respectively.
        //   - the three Logistics confirms and Disband Group: a live Route / RouteCandidate
        //     / group closure.

        private static readonly UiRaisableDialog[] DialogTable = new[]
        {
            // CommittedActionDialog.ShowBlocked - three strings and nothing else. The one
            // dialog in the whole set with no state behind it at all, which is why it leads.
            new UiRaisableDialog
            {
                Name = ActionBlockedDialog,
                PopupName = "ParsekResourceBlock",
                Title = "Action Blocked",
                Buttons = new[] { "OK" },
                Press = UiDialogPressPolicy.AnyButton,
            },

            // SceneExitInterceptor.ShowSaveFailedPopup - zero args, no mutation.
            new UiRaisableDialog
            {
                Name = SaveFailedDialog,
                PopupName = "ParsekSceneExitSaveFailed",
                Title = "Save failed",
                Buttons = new[] { "OK" },
                Press = UiDialogPressPolicy.AnyButton,
            },

            // ParsekUI.ShowWipeRecordingsConfirmation(count). `Wipe All` clears every
            // committed recording and unreserves every crew - never pressable here.
            new UiRaisableDialog
            {
                Name = WipeRecordingsDialog,
                PopupName = "ParsekWipeRecordingsConfirm",
                Title = "Confirm: Wipe Recordings",
                Buttons = new[] { "Wipe All", "Cancel" },
                Press = UiDialogPressPolicy.SafeButtonOnly,
                SafeButton = "Cancel",
            },

            // ParsekUI.ShowWipeMilestonesConfirmation(count).
            new UiRaisableDialog
            {
                Name = WipeMilestonesDialog,
                PopupName = "ParsekWipeMilestonesConfirm",
                Title = "Confirm: Wipe Milestones",
                Buttons = new[] { "Wipe All", "Cancel" },
                Press = UiDialogPressPolicy.SafeButtonOnly,
                SafeButton = "Cancel",
            },

            // RecordingsTableUI.ShowRewindConfirmation(rec). It SILENTLY RETURNS when
            // RecordingStore.GetRewindRecording(rec) is null, so the applier resolves that
            // owner first and answers TargetUnavailableReason instead of raising nothing.
            new UiRaisableDialog
            {
                Name = RewindDialog,
                PopupName = "ParsekRewindConfirm",
                Title = "Confirm: Rewind",
                Buttons = new[] { "Rewind", "Cancel" },
                NeedsRecording = true,
                NeedsRewindOwner = true,
                Press = UiDialogPressPolicy.SafeButtonOnly,
                SafeButton = "Cancel",
            },

            // RecordingsTableUI.ShowFastForwardConfirmation(rec). Needs only a committed
            // recording; its confirm warps time.
            new UiRaisableDialog
            {
                Name = FastForwardDialog,
                PopupName = "ParsekFastForwardConfirm",
                Title = "Confirm: Fast-Forward",
                Buttons = new[] { "Fast-Forward", "Cancel" },
                NeedsRecording = true,
                Press = UiDialogPressPolicy.SafeButtonOnly,
                SafeButton = "Cancel",
            },

            // UnfinishedFlightSealHandler.ShowConfirmation(rec). The ONE spawn site in the
            // set that takes a ControlTypes.All lock which only its own button callbacks
            // release, so a dismiss-without-press has to clear it - see OwnsInputLock.
            new UiRaisableDialog
            {
                Name = SealDialog,
                PopupName = "ParsekUFSealDialog",
                Title = "Confirm: Seal Unfinished Flight",
                Buttons = new[] { "Seal Permanently", "Cancel" },
                NeedsRecording = true,
                Press = UiDialogPressPolicy.SafeButtonOnly,
                SafeButton = "Cancel",
                OwnsInputLock = true,
            },
        };

        /// <summary>The raisable set, in table order.</summary>
        internal static IReadOnlyList<UiRaisableDialog> Dialogs => DialogTable;

        /// <summary>Every valid <c>popup=</c> token, comma-joined in table order. What the
        /// <see cref="PopupUnknownReason"/> message carries.</summary>
        internal static string ValidPopupNames
        {
            get
            {
                var names = new List<string>(DialogTable.Length);
                foreach (UiRaisableDialog d in DialogTable) names.Add(d.Name);
                return string.Join(",", names.ToArray());
            }
        }

        // ----- parses -----

        /// <summary>Resolves a <c>popup=</c> token against the table. Missing and unknown
        /// are DISTINCT rejects, the <c>window=</c> rule.</summary>
        internal static bool TryResolveDialog(string raw, out UiRaisableDialog spec,
                                             out string rejectReason)
        {
            spec = default(UiRaisableDialog);
            if (raw == null)
            {
                rejectReason = PopupArgMissingReason;
                return false;
            }
            foreach (UiRaisableDialog candidate in DialogTable)
            {
                if (candidate.Name == raw)
                {
                    spec = candidate;
                    rejectReason = null;
                    return true;
                }
            }
            rejectReason = PopupUnknownReason;
            return false;
        }

        /// <summary>
        /// Resolves <c>op=dismiss</c>'s optional <c>press=</c> arg.
        ///
        /// <para>Absent yields NULL, which means "dismiss without pressing" - the default,
        /// because most of these confirms mutate the save. A named button must exist on the
        /// dialog (<see cref="PressUnknownReason"/>) AND be allowed by its policy
        /// (<see cref="PressNotAllowedReason"/>); the two are separate rejects because a
        /// typo and a forbidden confirm send an author to different fixes.</para>
        /// </summary>
        internal static bool TryResolvePress(UiRaisableDialog spec, string rawPress,
                                             out string button, out string rejectReason)
        {
            button = null;
            rejectReason = null;
            if (string.IsNullOrEmpty(rawPress)) return true;

            bool exists = false;
            if (spec.Buttons != null)
            {
                for (int i = 0; i < spec.Buttons.Length; i++)
                {
                    if (string.Equals(spec.Buttons[i], rawPress, StringComparison.Ordinal))
                    {
                        exists = true;
                        break;
                    }
                }
            }
            if (!exists)
            {
                rejectReason = PressUnknownReason;
                return false;
            }
            if (!IsPressAllowed(spec, rawPress))
            {
                rejectReason = PressNotAllowedReason;
                return false;
            }
            button = rawPress;
            return true;
        }

        /// <summary>Whether a dialog's policy permits pressing
        /// <paramref name="button"/>.</summary>
        internal static bool IsPressAllowed(UiRaisableDialog spec, string button)
        {
            if (spec.Press == UiDialogPressPolicy.AnyButton) return true;
            return string.Equals(spec.SafeButton, button, StringComparison.Ordinal);
        }

        /// <summary>The comma-joined buttons <c>press=</c> may name for a dialog, or
        /// <c>-</c> for none. Carried by the <see cref="PressNotAllowedReason"/>
        /// message.</summary>
        internal static string PressableButtonsOf(UiRaisableDialog spec)
        {
            if (spec.Press == UiDialogPressPolicy.AnyButton)
                return spec.Buttons == null || spec.Buttons.Length == 0
                    ? "-" : string.Join("|", spec.Buttons);
            return string.IsNullOrEmpty(spec.SafeButton) ? "-" : spec.SafeButton;
        }

        /// <summary>The dialog's buttons as the payload reports them, reusing
        /// <c>op=dialog</c>'s own <c>|</c> separator so both ops' <c>buttons=</c> values
        /// read the same way.</summary>
        internal static string FormatButtons(UiRaisableDialog spec)
            => TestCommandUiDialog.FormatButtons(spec.Buttons);

        /// <summary>
        /// POST-SETTLE predicate for <c>op=raise</c>: the standing popup must be the
        /// EXPECTED one.
        ///
        /// <para>Name-equality rather than "some Parsek popup is up", because every spawn
        /// site in the table has a guarded early return: a raise that returned silently
        /// while an unrelated Parsek modal happened to stand would otherwise read as a
        /// success and the capture beside it would photograph the wrong dialog.</para>
        /// </summary>
        internal static bool RaiseConfirmed(UiRaisableDialog spec, string standingName)
            => !string.IsNullOrEmpty(standingName)
               && string.Equals(spec.PopupName, standingName, StringComparison.Ordinal);

        // ----- payloads -----

        /// <summary>OK payload for <c>raise</c>:
        /// <c>op=raise popup= name= title= buttons= nbuttons= pressable=</c>. The shape
        /// deliberately echoes <c>op=dialog</c>'s (name / title / buttons / nbuttons) so a
        /// lane's raise step and its following report step are comparable key for
        /// key.</summary>
        internal static List<KeyValuePair<string, string>> BuildRaisePayload(
            UiRaisableDialog spec, string observedTitle, IList<string> observedButtons)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int n = observedButtons != null ? observedButtons.Count : 0;
            return new List<KeyValuePair<string, string>>
            {
                Kv("op", TestCommandUiAction.RaiseOpToken),
                Kv("popup", spec.Name ?? string.Empty),
                Kv("name", spec.PopupName ?? string.Empty),
                Kv("title", string.IsNullOrEmpty(observedTitle)
                    ? (spec.Title ?? "-") : observedTitle),
                Kv("buttons", observedButtons != null && n > 0
                    ? TestCommandUiDialog.FormatButtons(observedButtons)
                    : FormatButtons(spec)),
                Kv("nbuttons", n.ToString(ic)),
                Kv("pressable", PressableButtonsOf(spec)),
            };
        }

        /// <summary>OK payload for <c>dismiss</c>:
        /// <c>op=dismiss popup= name= pressed= open=</c>. <c>pressed=</c> is the button
        /// label or <c>-</c>, so a reader can tell a plain dismissal from a pressed one
        /// without re-reading the spec; <c>open=</c> is the settled read-back and is always
        /// <c>false</c> on an OK.</summary>
        internal static List<KeyValuePair<string, string>> BuildDismissPayload(
            UiRaisableDialog spec, string pressedButton, bool openAfter)
            => new List<KeyValuePair<string, string>>
            {
                Kv("op", TestCommandUiAction.DismissOpToken),
                Kv("popup", spec.Name ?? string.Empty),
                Kv("name", spec.PopupName ?? string.Empty),
                Kv("pressed", string.IsNullOrEmpty(pressedButton) ? "-" : pressedButton),
                Kv("open", openAfter ? "true" : "false"),
            };

        private static KeyValuePair<string, string> Kv(string k, string v)
            => new KeyValuePair<string, string>(k, v);
    }
}
