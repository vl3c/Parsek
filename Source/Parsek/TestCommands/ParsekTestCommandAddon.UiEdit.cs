using System;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=edit</c>. The field table, the arg
    /// parses and the payload live in the pure sibling <see cref="TestCommandUiEdit"/>; this
    /// file calls the window classes' own arm and commit wrappers and reads the sentinel
    /// back, and nothing else.
    ///
    /// <para>THE ARM IS A WRAPPER ON THE WINDOW, NOT THREE FIELD WRITES FROM HERE. Each
    /// editor needs a row key, a draft and a focus sentinel written together, and the
    /// window class owns which three; a seam that wrote two of them would draw a text field
    /// with no keyboard focus, which photographs as an ordinary label. The wrappers also
    /// carry the refusals the ARMING GESTURE itself carries - an unknown row, and a
    /// permanent root group, whose double-click is blocked - so the seam refuses exactly
    /// what a player's double-click refuses.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionEditOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            string rawField = ArgOrNull(cmd, TestCommandUiEdit.FieldArg);
            if (!TestCommandUiEdit.TryResolveField(spec.Name, rawField,
                                                   out UiEditFieldSpec field,
                                                   out string fieldReject))
            {
                string detail =
                    fieldReject == TestCommandUiEdit.EditUnsupportedWindowReason
                        ? $"{fieldReject} window={spec.Name} "
                          + $"valid={TestCommandUiEdit.EditableWindowNames}"
                        : fieldReject == TestCommandUiEdit.EditFieldInvalidReason
                            ? $"{fieldReject} window={spec.Name} "
                              + $"field={rawField ?? string.Empty} "
                              + $"fields={TestCommandUiEdit.FieldNamesFor(spec.Name)}"
                            : $"{fieldReject} window={spec.Name} "
                              + $"fields={TestCommandUiEdit.FieldNamesFor(spec.Name)}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={fieldReject} "
                    + $"window={spec.Name} field={rawField ?? string.Empty}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            string key = ArgOrNull(cmd, TestCommandUiState.KeyArg);
            if (string.IsNullOrEmpty(key))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiEdit.EditKeyArgMissingReason
                    + $" window={spec.Name} field={field.Field}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiEdit.EditKeyArgMissingReason} field={field.Field} "
                    + $"needs={field.KeyMeaning}");
                return;
            }

            if (!TestCommandUiEdit.TryParseCommit(
                    ArgOrNull(cmd, TestCommandUiEdit.CommitArg), out bool commit,
                    out string commitReject))
            {
                string raw = ArgOrNull(cmd, TestCommandUiEdit.CommitArg) ?? string.Empty;
                ParsekLog.Warn(Tag, $"uiaction rejected reason={commitReject} "
                    + $"commit={raw}");
                SetExecResult("REJECTED", null, $"{commitReject} commit={raw}");
                return;
            }

            string draft = TestCommandUiEdit.ResolveDraft(
                ArgOrNull(cmd, TestCommandUiEdit.DraftArg));

            if (!TryArmEditor(ui, field.Field, key, draft))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiEdit.EditTargetUnavailableReason
                    + $" window={spec.Name} field={field.Field} key={key}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiEdit.EditTargetUnavailableReason} "
                    + $"field={field.Field} key={key} needs={field.KeyMeaning}");
                return;
            }

            if (commit)
            {
                // Opt-in, and the reason it is opt-in is that none of these commits is
                // "write a name": a group or mission-title commit renames a root group plus
                // its auto subgroups plus Mission.Name atomically and can reject both
                // halves on a collision, and a recording commit silently drops a row that
                // has left the committed list. The commit body's own logging says which
                // happened; this op reports only that it ran.
                CommitEditor(ui, field.Field, key);
            }

            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.Edit,
                Window = spec.Name,
                StartFrame = Time.frameCount,
                EditField = field.Field,
                EditRowKey = key,
                EditDraft = draft,
                EditCommitted = commit,
            };
            ParsekLog.Info(Tag, $"uiaction edit initiated window={spec.Name} "
                + $"field={field.Field} key={key} draft={draft} "
                + $"commit={Bool(commit)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionEdit(UiActionSettleContext ctx,
                                          UiActionPending pending)
        {
            string armedKey = ReadArmedEditorKey(ctx.Ui, pending.EditField);
            bool armed = string.Equals(armedKey, pending.EditRowKey,
                                       StringComparison.Ordinal);
            // The row key is a field this op WROTE, so reading it back proves only that
            // nothing cleared it. The FOCUS SENTINEL is written by the draw, so it is the
            // one signal here the seam cannot fake - see EditNotDrawnReason.
            bool drew = ReadEditorDrewFlag(ctx.Ui, pending.EditField);

            // An ARM step must still be armed after the drawn frame - that frame is the one
            // that turns the sentinel into a drawn text field, so a sentinel the draw
            // cleared means the editor is not open and a capture would photograph a plain
            // label. A COMMIT step is the mirror: its own body clears the sentinel by
            // design, so `armed=false` there is the success shape and is reported rather
            // than checked.
            if (!pending.EditCommitted && !armed)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiEdit.EditNotArmedReason
                    + $" window={pending.Window} field={pending.EditField} "
                    + $"key={pending.EditRowKey} armedKey={armedKey ?? "-"} "
                    + $"frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiEdit.EditNotArmedReason} field={pending.EditField} "
                    + $"key={pending.EditRowKey}",
                    dequeueHead: true);
                return;
            }

            // An ARM step must have DRAWN its text field, not merely kept its row key: a
            // row inside a collapsed group, a row on the unselected tab, or any row with
            // the window scrolled past it leaves the key standing and draws nothing.
            if (!pending.EditCommitted && !drew)
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiEdit.EditNotDrawnReason
                    + $" window={pending.Window} field={pending.EditField} "
                    + $"key={pending.EditRowKey} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiEdit.EditNotDrawnReason} field={pending.EditField} "
                    + $"key={pending.EditRowKey} (the row key survived but the text field "
                    + "never drew: select its tab, expand its group, size the window)",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction edit window={pending.Window} "
                + $"field={pending.EditField} key={pending.EditRowKey} "
                + $"draft={pending.EditDraft} committed={Bool(pending.EditCommitted)} "
                + $"armed={Bool(armed)} drew={Bool(drew)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiEdit.BuildEditPayload(
                    pending.Window, pending.EditField, pending.EditRowKey,
                    pending.EditDraft, pending.EditCommitted, armed, drew),
                null, dequeueHead: true);
        }

        // ----- one arm / commit / read site per field -----

        private static bool TryArmEditor(ParsekUI ui, string field, string key, string draft)
        {
            switch (field)
            {
                case TestCommandUiEdit.RecordingNameField:
                    return ui.GetRecordingsTableUI()
                             .TryBeginRecordingRenameForTesting(key, draft);
                case TestCommandUiEdit.GroupNameField:
                    return ui.GetRecordingsTableUI()
                             .TryBeginGroupRenameForTesting(key, draft);
                case TestCommandUiEdit.MissionTitleField:
                    return ui.GetMissionsUI().TryBeginMissionRenameForTesting(key, draft);
                default:
                    // Unreachable through the parse (the field came from the table), so a
                    // miss here means the table and this switch have drifted - loud rather
                    // than silent, the ResolveExpandSets rule.
                    throw new InvalidOperationException(
                        "no arm site for op=edit field=" + (field ?? "<null>"));
            }
        }

        private static void CommitEditor(ParsekUI ui, string field, string key)
        {
            switch (field)
            {
                case TestCommandUiEdit.RecordingNameField:
                    ui.GetRecordingsTableUI().CommitRecordingRenameForTesting();
                    return;
                case TestCommandUiEdit.GroupNameField:
                    // Takes the OLD name, which is this editor's row key: the commit reads
                    // the draft off the window and needs to be told what it is renaming.
                    ui.GetRecordingsTableUI().CommitGroupRenameForTesting(key);
                    return;
                case TestCommandUiEdit.MissionTitleField:
                    ui.GetMissionsUI().CommitMissionRenameForTesting();
                    return;
                default:
                    throw new InvalidOperationException(
                        "no commit site for op=edit field=" + (field ?? "<null>"));
            }
        }

        /// <summary>Whether the named editor's text field has actually DRAWN since the
        /// arm, read off the sentinel the DRAW writes rather than one the arm wrote.</summary>
        private static bool ReadEditorDrewFlag(ParsekUI ui, string field)
        {
            switch (field)
            {
                case TestCommandUiEdit.RecordingNameField:
                    return ui.GetRecordingsTableUI().RenamingRecordingFocusedForTesting;
                case TestCommandUiEdit.GroupNameField:
                    return ui.GetRecordingsTableUI().RenamingGroupFocusedForTesting;
                case TestCommandUiEdit.MissionTitleField:
                    return ui.GetMissionsUI().RenamingMissionFocusedForTesting;
                default:
                    throw new InvalidOperationException(
                        "no drew-flag read for op=edit field=" + (field ?? "<null>"));
            }
        }

        /// <summary>The row key the named editor is currently armed on, or null.</summary>
        private static string ReadArmedEditorKey(ParsekUI ui, string field)
        {
            switch (field)
            {
                case TestCommandUiEdit.RecordingNameField:
                    return ui.GetRecordingsTableUI().RenamingRecordingIdForTesting;
                case TestCommandUiEdit.GroupNameField:
                    return ui.GetRecordingsTableUI().RenamingGroupForTesting;
                case TestCommandUiEdit.MissionTitleField:
                    return ui.GetMissionsUI().RenamingMissionIdForTesting;
                default:
                    throw new InvalidOperationException(
                        "no read site for op=edit field=" + (field ?? "<null>"));
            }
        }
    }
}
