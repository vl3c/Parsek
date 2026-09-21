using System;
using System.Collections.Generic;

namespace Parsek.TestCommands
{
    /// <summary>One driveable in-place text editor: its wire field name and its window.</summary>
    internal struct UiEditFieldSpec
    {
        internal string Field;

        /// <summary>The window token whose <c>op=edit</c> steps may name this field.</summary>
        internal string Window;

        /// <summary>What the <c>key=</c> arg names for this editor: a RecordingId, a group
        /// NAME, a Mission id. Carried as prose because it is what the reject message has to
        /// say, and because the three are not interchangeable.</summary>
        internal string KeyMeaning;
    }

    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=edit</c>: the op that puts one of the
    /// GUI's in-place text editors into EDIT MODE with a given draft, and commits only when
    /// asked.
    ///
    /// <para><b>WHY THE MID-EDIT STATE IS WORTH AN OP.</b> Every one of these editors is a
    /// LAYOUT change and not a restyle: a label becomes a <c>GUILayout.TextField</c>, so the
    /// row's control count and widths change. Nothing in the census can reach it - the
    /// arming gesture is a double-click, which this seam does not synthesise - and the state
    /// is drawn only while a row-key sentinel is set. So the op writes the same three fields
    /// the double-click writes, through an arm wrapper on the window class.</para>
    ///
    /// <para><b>WHY ARMING AND COMMITTING ARE SEPARATE, AND WHY COMMIT IS OPT-IN.</b>
    /// <c>commit=</c> defaults to FALSE, which is the opposite of how the rest of the seam's
    /// write ops default, and the asymmetry is the point: these commits are not "write a
    /// name". A group rename of a tree root runs
    /// <c>MissionGroupLink.RenameMissionGroup</c>, which renames the root group plus its
    /// auto <c>/ Debris</c> and <c>/ Crew</c> subgroups plus <c>Mission.Name</c> atomically
    /// and rejects BOTH halves on a collision; a mission-title rename does the same; a
    /// recording rename silently DROPS if the id has left the committed list. A census wants
    /// the mid-edit PICTURE, and a lane that only wanted the picture must not be able to
    /// rename the fixture's history by omission.</para>
    ///
    /// <para><b>THE THREE FIELDS ARE THE THREE RENAME EDITORS.</b> The GUI has nine in-place
    /// editors across three idioms. These three share ONE idiom - a row key, a draft, and a
    /// focus sentinel the next draw pass consumes with a single
    /// <c>GUI.FocusControl</c> - which is the idiom a seam can arm completely. The other six
    /// are recorded as not-yet-driveable rather than faked: the two Logistics editors
    /// (route rename, interval) need their route's detail panel expanded first and are
    /// mutually exclusive with each other, and the four period / auto-loop / warp editors
    /// use a DIFFERENT idiom whose edit mode is entered only when Unity's focused control
    /// name already matches, so a seam that wrote their two fields would draw an unfocused
    /// text box - a picture no click can produce. See the todo entry.</para>
    /// </summary>
    internal static class TestCommandUiEdit
    {
        // ----- arg keys -----

        /// <summary>WHICH editor. Closed and per-window.</summary>
        internal const string FieldArg = "field";

        /// <summary>The draft text to put in the field. SPELLED <c>draft</c> and not
        /// <c>text</c> because <c>text=</c> is already <c>op=find</c>'s, and the harness's
        /// arg table admits one owner op per key - a <c>text=</c> here would be flagged
        /// pre-launch as an arg only <c>op=find</c> reads. The name also says what it is: a
        /// draft is not a committed value.</summary>
        internal const string DraftArg = "draft";

        /// <summary>Whether to run the editor's own commit body after arming. ABSENT MEANS
        /// FALSE - see the class header.</summary>
        internal const string CommitArg = "commit";

        // ----- field tokens -----

        internal const string RecordingNameField = "recordingname";
        internal const string GroupNameField = "groupname";
        internal const string MissionTitleField = "missiontitle";

        private static readonly UiEditFieldSpec[] FieldTable = new[]
        {
            // Both live on the Missions WINDOW (RecordingsTableUI owns that window's
            // chrome); the first is drawn by its Recordings tab and the second by the group
            // tree, so a lane selects the tab before it photographs either.
            NewField(RecordingNameField, TestCommandUiAction.MissionsWindow, "a RecordingId"),
            NewField(GroupNameField, TestCommandUiAction.MissionsWindow, "a group name"),
            NewField(MissionTitleField, TestCommandUiAction.MissionsWindow, "a Mission id"),
        };

        private static UiEditFieldSpec NewField(string field, string window, string keyMeaning)
            => new UiEditFieldSpec { Field = field, Window = window, KeyMeaning = keyMeaning };

        /// <summary>The windows that own a driveable editor, comma-joined for the reject
        /// message.</summary>
        internal static string EditableWindowNames => TestCommandUiAction.MissionsWindow;

        /// <summary>One window's field tokens, comma-joined, for the reject message.</summary>
        internal static string FieldNamesFor(string window)
        {
            var names = new List<string>();
            for (int i = 0; i < FieldTable.Length; i++)
                if (string.Equals(FieldTable[i].Window, window, StringComparison.Ordinal))
                    names.Add(FieldTable[i].Field);
            return string.Join(",", names.ToArray());
        }

        /// <summary>Whether any editor belongs to this window.</summary>
        internal static bool WindowHasEditors(string window)
            => FieldNamesFor(window).Length > 0;

        // ----- reject reasons -----

        /// <summary><c>op=edit</c> against a window with no driveable in-place editor.
        /// The message names the ones that have one.</summary>
        internal const string EditUnsupportedWindowReason = "edit-unsupported-window";

        /// <summary>No <c>field=</c>. REQUIRED: a window has several editors and there is no
        /// "the" one.</summary>
        internal const string EditFieldArgMissingReason = "edit-field-arg-missing";

        /// <summary>A <c>field=</c> the named window does not own. The message carries that
        /// window's own field list.</summary>
        internal const string EditFieldInvalidReason = "edit-field-invalid";

        /// <summary>No <c>key=</c>. Every one of these editors is keyed by a ROW, so an
        /// unkeyed arm would have to pick a row for the lane.</summary>
        internal const string EditKeyArgMissingReason = "edit-key-arg-missing";

        /// <summary>A <c>commit=</c> outside {true,false}.</summary>
        internal const string EditCommitArgInvalidReason = "edit-commit-arg-invalid";

        /// <summary>
        /// The arm wrapper declined: the key names no live row, or it names something the
        /// ARMING GESTURE ITSELF refuses (a permanent root group, whose double-click is
        /// blocked). REJECTED rather than a silent OK: the alternative is a capture of an
        /// unedited row under a label claiming an editor.
        /// </summary>
        internal const string EditTargetUnavailableReason = "edit-target-unavailable";

        /// <summary>POST-SETTLE terminal: the editor was armed and, after a drawn frame, the
        /// window's row-key sentinel no longer names the requested row. ERROR - we acted and
        /// the game did not follow. Not reachable through a commit step, which clears the
        /// sentinel on purpose.</summary>
        internal const string EditNotArmedReason = "edit-not-armed";

        // ----- parses -----

        /// <summary>Resolves a <c>field=</c> against a window's table.</summary>
        internal static bool TryResolveField(string window, string rawField,
                                             out UiEditFieldSpec spec,
                                             out string rejectReason)
        {
            spec = default(UiEditFieldSpec);
            if (!WindowHasEditors(window))
            {
                rejectReason = EditUnsupportedWindowReason;
                return false;
            }
            if (string.IsNullOrEmpty(rawField))
            {
                rejectReason = EditFieldArgMissingReason;
                return false;
            }
            for (int i = 0; i < FieldTable.Length; i++)
            {
                if (!string.Equals(FieldTable[i].Window, window, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(FieldTable[i].Field, rawField, StringComparison.Ordinal))
                    continue;
                spec = FieldTable[i];
                rejectReason = null;
                return true;
            }
            rejectReason = EditFieldInvalidReason;
            return false;
        }

        /// <summary>Parses the optional <c>commit=</c>. Absent means FALSE - the class
        /// header's whole argument.</summary>
        internal static bool TryParseCommit(string raw, out bool commit,
                                            out string rejectReason)
        {
            commit = false;
            rejectReason = null;
            if (raw == null) return true;
            if (raw == TestCommandUiState.StateTrueToken) { commit = true; return true; }
            if (raw == TestCommandUiState.StateFalseToken) { commit = false; return true; }
            rejectReason = EditCommitArgInvalidReason;
            return false;
        }

        /// <summary>
        /// The draft an arm writes. An ABSENT <c>draft=</c> means the EMPTY string rather
        /// than "leave whatever is there": nothing is there - the arm is what creates the
        /// buffer - so the only other reading would be a stale draft from a previous edit,
        /// which is a state no click produces.
        /// </summary>
        internal static string ResolveDraft(string raw) => raw ?? string.Empty;

        // ----- payload -----

        /// <summary>
        /// OK payload for <c>edit</c>:
        /// <c>op=edit window= field= key= draft= committed= armed=</c>.
        ///
        /// <para><c>armed</c> is the SETTLED read-back of the window's row-key sentinel, and
        /// it is the field a lane asserts on: it separates "the editor is open on that row"
        /// from "the op ran". On a commit step it reads FALSE by design, the commit body
        /// having cleared the sentinel - which is why <c>committed</c> is reported beside it
        /// rather than inferred from it.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildEditPayload(
            string window, string field, string key, string draft, bool committed,
            bool armed)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.EditOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("field", field ?? string.Empty),
                new KeyValuePair<string, string>("key", key ?? string.Empty),
                new KeyValuePair<string, string>("draft", draft ?? string.Empty),
                new KeyValuePair<string, string>("committed", committed ? "true" : "false"),
                new KeyValuePair<string, string>("armed", armed ? "true" : "false"),
            };
    }
}
