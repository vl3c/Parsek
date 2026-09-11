using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=dialog</c> - the read-only report
    /// of the live Parsek <c>PopupDialog</c>. The vocabulary and the payload live in the
    /// pure sibling <see cref="TestCommandUiDialog"/>.
    ///
    /// <para>
    /// WHY THIS IS THE ONLY WAY TO ASSERT ON A DIALOG. Every Parsek modal is a stock
    /// <c>PopupDialog</c>, which is uGUI. <c>GuiTreeRecorder</c> patches
    /// <c>GUI.DoWindow</c> - the IMGUI funnel - so a dialog cannot appear in a
    /// <c>.gui.json</c> at all, and the census's only evidence of one would be a PNG.
    /// This op supplies the machine-readable half, so a lane reads
    /// <c>UiAction op=dialog</c> -&gt; <c>CaptureScreenshot</c> -&gt;
    /// <c>AnswerMergeDialog</c> and pins WHAT it photographed rather than trusting the
    /// label.
    /// </para>
    ///
    /// <para>
    /// SINGLE-PHASE, unlike every other new op, and the reason is the rule rather than an
    /// exception to it: this op writes nothing. It changes no drawn state, so there is
    /// nothing a drawn frame could disagree with.
    /// </para>
    ///
    /// <para>
    /// IT DOES NOT HOLD THE DIALOG OPEN, and nothing needs to. A <c>PopupDialog</c> stands
    /// until something dismisses it, and the only dismissers are its own buttons, Esc, and
    /// <c>MergeDialog.DismissAndClearPendingFlag</c>. The seam presses none of those
    /// between a report and a capture, so a spec CAN hold a dialog across a
    /// <c>CaptureScreenshot</c> step - which is the question the census could not answer
    /// before this op existed. The tree-merge dialog additionally locks
    /// <c>ControlTypes.All</c> while it stands, which is why the census's dialog lane must
    /// answer it before any verb that needs input.
    /// </para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private static readonly FieldInfo MultiOptionDialogTitleField =
            typeof(MultiOptionDialog).GetField("title",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private void UiActionDialogOp()
        {
            List<PopupDialog> popups = FindParsekPopups();
            if (popups.Count == 0)
            {
                ParsekLog.Info(Tag, "uiaction dialog open=false count=0");
                SetExecResult("OK",
                    TestCommandUiDialog.BuildPayload(false, 0, null, null, null), null);
                return;
            }

            // The FIRST live Parsek popup. `count` carries how many there were, so a
            // reader can see that the reported one was picked arbitrarily rather than
            // reading a two-modal state as a one-modal one. Parsek's own contract is that
            // at most one stands (every spawn site dismisses first), so count > 1 is itself
            // a finding.
            PopupDialog popup = popups[0];
            string name = ReadDialogName(popup);
            string title = ReadDialogTitle(popup);
            var labels = new List<string>();
            List<DialogGUIButton> buttons = GetDialogButtons(popup);
            for (int i = 0; i < buttons.Count; i++)
                labels.Add(buttons[i] != null ? buttons[i].OptionText : null);

            ParsekLog.Info(Tag, $"uiaction dialog open=true count={Int(popups.Count)} "
                + $"name={name ?? "-"} title={title ?? "-"} "
                + $"nbuttons={Int(labels.Count)} "
                + $"buttons={TestCommandUiDialog.FormatButtons(labels)}");
            SetExecResult("OK",
                TestCommandUiDialog.BuildPayload(true, popups.Count, name, title, labels),
                null);
        }

        /// <summary>
        /// Every live <c>PopupDialog</c> whose <c>MultiOptionDialog</c> name is Parsek's.
        ///
        /// <para>Scanned by NAME PREFIX rather than against a list of the 21 spawn sites:
        /// a list would go stale the first time a dialog was added, and its failure mode is
        /// "no dialog open" reported over a live modal - the worst possible answer for a
        /// verb whose whole job is to say what is on screen.</para>
        /// </summary>
        private static List<PopupDialog> FindParsekPopups()
        {
            var found = new List<PopupDialog>();
            if (PopupDialogToDisplayField == null || MultiOptionDialogNameField == null)
                return found;
            PopupDialog[] popups = UnityEngine.Object.FindObjectsOfType<PopupDialog>();
            if (popups == null) return found;
            for (int i = 0; i < popups.Length; i++)
            {
                string name = ReadDialogName(popups[i]);
                if (TestCommandUiDialog.IsParsekDialogName(name)) found.Add(popups[i]);
            }
            return found;
        }

        private static string ReadDialogName(PopupDialog popup)
        {
            if (popup == null || PopupDialogToDisplayField == null
                || MultiOptionDialogNameField == null)
            {
                return null;
            }
            var dialog =
                PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
            if (dialog == null) return null;
            return MultiOptionDialogNameField.GetValue(dialog) as string;
        }

        private static string ReadDialogTitle(PopupDialog popup)
        {
            if (popup == null || PopupDialogToDisplayField == null
                || MultiOptionDialogTitleField == null)
            {
                return null;
            }
            var dialog =
                PopupDialogToDisplayField.GetValue(popup) as MultiOptionDialog;
            if (dialog == null) return null;
            return MultiOptionDialogTitleField.GetValue(dialog) as string;
        }
    }
}
