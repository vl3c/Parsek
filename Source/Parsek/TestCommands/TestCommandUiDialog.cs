using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=dialog</c> (report the live Parsek
    /// popup) and of <c>AnswerMergeDialog</c>'s new <c>dialog=</c> arg (say WHICH popup is
    /// being answered).
    ///
    /// <para><b>WHY A DIALOG REPORT EXISTS AT ALL.</b> Every Parsek modal is a stock
    /// <c>PopupDialog</c>, which is uGUI. The GUI-tree recorder patches
    /// <c>GUI.DoWindow</c>, the IMGUI funnel, so a dialog is structurally invisible to
    /// <c>DumpGuiTree</c> and visible only in a <c>CaptureScreenshot</c> PNG. That makes a
    /// dialog capture the one census step with no machine-readable half: a lane would
    /// photograph a modal and have nothing to assert about it. <c>op=dialog</c> is that
    /// half - name, title and the ordered button labels, read out of the live
    /// <c>MultiOptionDialog</c> - so a spec can pin WHAT it photographed
    /// (<c>op=dialog</c> -&gt; <c>CaptureScreenshot</c> -&gt; <c>AnswerMergeDialog</c>) and
    /// the PNG stops being the only evidence.</para>
    ///
    /// <para><b>WHY THE ANSWER VERB NOW NAMES ITS DIALOG.</b> Three spawn sites shared one
    /// dialog name. <c>MergeDialog.ShowTreeDialog</c>'s two overloads (post-transition and
    /// pre-transition) are both the tree merge dialog and are both what
    /// <c>AnswerMergeDialog</c> means; the third,
    /// <c>MergeDialog.ShowPreSwitchDecisionDialog</c>, is a DIFFERENT decision that happens
    /// to carry Merge / Discard buttons in the same order. <c>AnswerMergeDialog</c> selects
    /// its button BY ORDER, so with a pre-switch dialog live it would have invoked that
    /// dialog's merge action believing it was concluding a re-fly. The pre-switch dialog
    /// now carries <c>MergeDialog.PreSwitchDialogName</c>, and the answer verb looks its
    /// target up by the name this class resolves, so the two can no longer be confused in
    /// either direction.</para>
    ///
    /// <para><b>WHY <c>preswitch</c> IS NOT ANSWERABLE, checked in the mirror direction
    /// rather than assumed.</b> <c>AnswerMergeDialog</c>'s completion contract is
    /// answer-applied AND the post-answer scene SETTLED out of FLIGHT
    /// (<c>TestCommandMergeAnswer.DecideAnswerCompletion</c>). The pre-switch dialog's
    /// buttons end in <c>FlightGlobals.SetActiveVessel</c>, which for a LOADED target
    /// changes no scene at all - so answering it through this verb would hold the FIFO head
    /// until the budget expired and then report a timeout over an answer that landed. The
    /// arg's closed set is therefore exactly the dialogs the verb can honestly complete,
    /// and the pre-switch popup is reachable for INSPECTION through <c>op=dialog</c>, which
    /// is what a census needs of it.</para>
    /// </summary>
    internal static class TestCommandUiDialog
    {
        // ----- arg keys and tokens -----

        /// <summary>The <c>AnswerMergeDialog</c> arg. Also the <c>op=dialog</c> op token's
        /// spelling, which is a coincidence of vocabulary and not a shared key: one is an
        /// arg NAME on one verb, the other an <c>op=</c> VALUE on another.</summary>
        internal const string DialogArg = "dialog";

        /// <summary>The tree merge dialog, both overloads. The DEFAULT, so every existing
        /// spec keeps its exact behaviour.</summary>
        internal const string MergeDialogToken = "merge";

        /// <summary>Every accepted <c>dialog=</c> value on <c>AnswerMergeDialog</c>,
        /// comma-joined for the reject message. One entry today, and it is deliberately a
        /// closed SET rather than a bare default: the arg's whole point is that the verb
        /// states which popup it is answering, and a value outside the set must fail
        /// pre-launch rather than silently fall back to the merge dialog.</summary>
        internal static string ValidAnswerDialogNames => MergeDialogToken;

        /// <summary>The prefix a Parsek-owned <c>MultiOptionDialog</c> name carries.
        /// <c>op=dialog</c> scans by it rather than by a hand-kept list of the twenty-odd
        /// spawn sites: a list would go stale the first time a dialog was added, and the
        /// failure mode of a stale list is "no dialog open" over a live modal. The cost of
        /// the prefix scan is that the PREFIX must actually be there, which one site got
        /// wrong - see <see cref="IsParsekDialogName"/> for the gate that now holds
        /// it.</summary>
        internal const string ParsekDialogNamePrefix = "Parsek";

        // ----- reject reasons -----

        /// <summary>A <c>dialog=</c> value outside the closed set.</summary>
        internal const string DialogArgInvalidReason = "dialog-arg-invalid";

        // ----- parses -----

        /// <summary>
        /// Maps <c>AnswerMergeDialog</c>'s <c>dialog=</c> arg to the live
        /// <c>PopupDialog</c> name to look up. Absent means
        /// <see cref="MergeDialogToken"/>, which is what every spec written before the arg
        /// existed meant.
        /// </summary>
        internal static bool TryParseAnswerDialog(string raw, out string token,
                                                  out string rejectReason)
        {
            token = MergeDialogToken;
            rejectReason = null;
            if (raw == null) return true;
            if (raw == MergeDialogToken) return true;
            rejectReason = DialogArgInvalidReason;
            return false;
        }

        /// <summary>True when a live dialog's name belongs to Parsek.
        ///
        /// <para>The premise this rests on - that EVERY Parsek-spawned
        /// <c>MultiOptionDialog</c> carries the prefix - is kept mechanically true by the
        /// source-derived gate <c>ParsekDialogNamePrefixSourceGateTests</c>, not by
        /// convention. It had already failed once: the flight-map ghost icon menu was named
        /// <c>"GhostIconMenu"</c>, so <c>op=dialog</c> answered <c>open=false</c> over a
        /// live modal.</para></summary>
        internal static bool IsParsekDialogName(string name)
            => !string.IsNullOrEmpty(name)
               && name.StartsWith(ParsekDialogNamePrefix, StringComparison.Ordinal);

        // ----- button discovery -----

        /// <summary>
        /// Depth bound on <see cref="CollectButtons"/>. A <c>DialogGUIBase.children</c>
        /// graph is authored by hand and is three or four deep at the very worst; the bound
        /// exists because the walk follows a mutable PUBLIC field that a stock or modded
        /// layout could in principle make cyclic, and a census verb must not hang the FIFO
        /// head over a malformed dialog.
        /// </summary>
        internal const int ButtonWalkMaxDepth = 8;

        /// <summary>
        /// Every <c>DialogGUIButton</c> reachable from a <c>MultiOptionDialog</c>'s
        /// <c>options</c>, in DEPTH-FIRST, left-to-right order - which is the order the
        /// dialog lays them out, and therefore the order <c>AnswerMergeDialog</c>'s
        /// by-position selection (first = Merge, last = Discard) means.
        ///
        /// <para><b>WHY IT RECURSES.</b> The scan used to read the top level of
        /// <c>options</c> alone. A dialog that wraps its buttons in a
        /// <c>DialogGUIHorizontalLayout</c> / <c>DialogGUIVerticalLayout</c> - the ordinary
        /// way to put two buttons on one row - therefore reported <c>nbuttons=0</c> to
        /// <c>op=dialog</c> over a popup that plainly has buttons, and a census step would
        /// have photographed the modal and asserted it has none.</para>
        ///
        /// <para>A button's OWN children are not descended into: a <c>DialogGUIButton</c>
        /// is a leaf control, and anything nested under one is decoration, not a separate
        /// option a caller could press.</para>
        /// </summary>
        internal static List<DialogGUIButton> CollectButtons(IList<DialogGUIBase> nodes)
        {
            var found = new List<DialogGUIButton>();
            CollectButtonsInto(nodes, found, 0);
            return found;
        }

        private static void CollectButtonsInto(IList<DialogGUIBase> nodes,
                                               List<DialogGUIButton> into, int depth)
        {
            if (nodes == null || depth > ButtonWalkMaxDepth) return;
            for (int i = 0; i < nodes.Count; i++)
            {
                DialogGUIBase node = nodes[i];
                if (node == null) continue;
                var button = node as DialogGUIButton;
                if (button != null)
                {
                    into.Add(button);
                    continue;
                }
                CollectButtonsInto(node.children, into, depth + 1);
            }
        }

        /// <summary>
        /// Joins the ordered button labels into ONE payload value.
        ///
        /// <para>Separated by <c>|</c>, which rides the wire raw (it is printable ASCII and
        /// neither <c>%</c> nor <c>=</c>, the only two characters
        /// <c>TestCommandProtocol.NeedsEncoding</c> escapes inside the printable range), so
        /// a reader sees the labels rather than a percent-soup. A comma would collide with
        /// the labels themselves, which are player-facing sentences.</para>
        /// </summary>
        internal static string FormatButtons(IList<string> labels)
        {
            if (labels == null || labels.Count == 0) return "-";
            var parts = new List<string>(labels.Count);
            for (int i = 0; i < labels.Count; i++)
                parts.Add(string.IsNullOrEmpty(labels[i]) ? "-" : labels[i]);
            return string.Join("|", parts.ToArray());
        }

        // ----- payload -----

        /// <summary>
        /// OK payload for <c>op=dialog</c>:
        /// <c>op=dialog open= count= name= title= buttons= nbuttons=</c>.
        ///
        /// <para>ALWAYS all seven keys, both ways. A closed-dialog answer reports
        /// <c>open=false</c> with <c>-</c> sentinels rather than a short line, for the
        /// <c>describe</c> reason: a missing key is indistinguishable from an older seam
        /// build, and a lane that asserts "no dialog is up" needs a key to assert
        /// on.</para>
        ///
        /// <para><c>count</c> is how many Parsek popups were live, and it is separate from
        /// <c>nbuttons</c> on purpose: two live modals mean the reported one was picked
        /// arbitrarily, which a reader must be able to see.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildPayload(
            bool open, int liveCount, string name, string title, IList<string> buttons)
        {
            CultureInfo ic = CultureInfo.InvariantCulture;
            int nbuttons = buttons != null ? buttons.Count : 0;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.DialogOpToken),
                new KeyValuePair<string, string>("open", open ? "true" : "false"),
                new KeyValuePair<string, string>("count", liveCount.ToString(ic)),
                new KeyValuePair<string, string>(
                    "name", string.IsNullOrEmpty(name) ? "-" : name),
                new KeyValuePair<string, string>(
                    "title", string.IsNullOrEmpty(title) ? "-" : title),
                new KeyValuePair<string, string>("buttons", FormatButtons(buttons)),
                new KeyValuePair<string, string>("nbuttons", nbuttons.ToString(ic)),
            };
        }
    }
}
