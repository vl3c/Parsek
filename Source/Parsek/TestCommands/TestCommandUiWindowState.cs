using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek.TestCommands
{
    /// <summary>Whether one <c>op=state</c> key carries a boolean or an open value.</summary>
    internal enum UiStateKeyKind
    {
        /// <summary>Driven by <c>state=true|false</c>: a plain bool the window or a store
        /// owns.</summary>
        Bool = 0,

        /// <summary>Driven by <c>value=</c>: a token or a number, parsed per key.</summary>
        Value = 1,
    }

    /// <summary>One driveable scalar window state: its wire key and its kind.</summary>
    internal struct UiStateKeySpec
    {
        internal string Key;
        internal UiStateKeyKind Kind;
    }

    /// <summary>
    /// Pure decision / payload half of <c>UiAction op=state</c>: the automation-only op that
    /// drives a window's SCALAR view state - the filter toggles, the archive filters, the
    /// expanded-stats columns, the time-range preset and the scroll offset.
    ///
    /// <para><b>WHY A SEPARATE OP FROM <c>op=expand</c>.</b> <c>expand</c> drives SETS of
    /// keys a window enumerates (group folders, chain blocks, mission rows); every state
    /// here is a single named field with no enumeration behind it, so there is nothing for
    /// <c>key=all</c> to mean and nothing for an <c>expand-key-unknown</c> candidate list to
    /// list. Folding them into <c>expand</c> would have given that op two key grammars - a
    /// <c>&lt;prefix&gt;:&lt;value&gt;</c> pair AND a bare field name - which is the one
    /// thing its own header says the namespaced grammar exists to prevent.</para>
    ///
    /// <para><b>WHY ONE OP AND NOT THREE.</b> The read-only state audit proposed
    /// <c>op=columns</c> for the Recordings tab's expanded-stats columns and
    /// <c>op=filter</c> for the two archive filters, beside this op for the Timeline's own
    /// toggles. All three would have been the same four lines - parse a name, write a bool
    /// on a window or a store, read it back after a drawn frame - so they are one op with a
    /// per-window key table. A spec author then learns one grammar, and a fourth such state
    /// is one row here rather than a fourth op.</para>
    ///
    /// <para><b>THE KEYS ARE PER-WINDOW AND CLOSED</b> (mirrored in
    /// <c>hlib.UIACTION_STATE_KEYS</c>), for the <c>op=tab</c> reason: a spec names the key
    /// in a step and a capture label is built from it, so an unknown key must be a typed
    /// REJECTED carrying that window's own list rather than a silent no-op that photographs
    /// an unchanged window under a label claiming the state.</para>
    ///
    /// <para><b><c>archived</c> IS ONE FLAG WITH ONE POLARITY, NAMED ONCE.</b> The Timeline's
    /// Archived toggle and the Recordings tab's Archive header checkbox write the SAME
    /// persisted bool (<c>GroupHierarchyStore.HideActive</c>, via
    /// <c>TimelineWindowUI.ShowArchivedRecordings</c>) in OPPOSITE label senses. So the wire
    /// carries the key exactly once, valid on BOTH windows, always in the Timeline's
    /// positive sense - <c>state=true</c> means archived rows contribute. Two keys with
    /// opposite polarities for one flag would have made every lane that touched it read the
    /// source to find out which it had. The Missions window's OWN archive filter
    /// (<c>MissionStore.HideArchived</c>) is a genuinely different flag over a different
    /// list, so it is a different key (<c>archivedMissions</c>) - and it keeps the store's
    /// own HIDE sense, because there is no second control to disagree with.</para>
    ///
    /// <para><b>ONE KEY IS REFUSED RATHER THAN WRITTEN.</b> <c>srcRecordings state=false</c>
    /// while the Timeline's tier filter is Rewind/FF or Re-Fly: that tab's own draw pass
    /// FORCES the toggle back on (<c>TimelineWindowUI.DrawFilterBar</c>, the
    /// <c>actionFilterMode</c> arm) and draws it disabled, so the write would be undone by
    /// the very frame the settle waits for. Refused pre-write
    /// (<see cref="StateClampedByTabReason"/>) and named, rather than reported as an
    /// applied-then-disagreeing ERROR: the game is not failing to follow, the lane asked for
    /// a state that tab does not have.</para>
    /// </summary>
    internal static class TestCommandUiWindowState
    {
        // ----- arg keys -----

        /// <summary>The open-valued half of the grammar, for the keys that are not bools.
        /// SPELLED <c>value</c> and deliberately NOT a closed-vocabulary row on the harness
        /// side: the two value keys take different shapes (a preset NAME, a scroll OFFSET),
        /// so the check is per key and lives beside the key table.</summary>
        internal const string ValueArg = "value";

        // ----- key tokens -----

        internal const string SrcRecordingsKey = "srcRecordings";
        internal const string SrcActionsKey = "srcActions";
        internal const string SrcEventsKey = "srcEvents";
        internal const string ArchivedKey = "archived";
        internal const string CustomRangeKey = "customRange";
        internal const string PresetKey = "preset";
        internal const string ScrollYKey = "scrollY";
        internal const string ExpandedStatsKey = "expandedStats";
        internal const string ArchivedMissionsKey = "archivedMissions";

        // ----- the time-range presets -----
        //
        // The five labels TimelineWindowUI's preset row draws, in its own left-to-right
        // order. They are the LABELS on purpose: the window stores
        // TimeRangeFilterState.ActivePresetName from `content.text`, so the wire token, the
        // button label and the stored name are one string and cannot drift. `All` is the
        // row's clear button rather than a range, which the applier expresses by calling
        // Clear() - the same thing that button's own handler does.

        internal const string PresetLastDayToken = "Last Day";
        internal const string PresetLast7dToken = "Last 7d";
        internal const string PresetLast30dToken = "Last 30d";
        internal const string PresetThisYearToken = "This Year";
        internal const string PresetAllToken = "All";

        private static readonly string[] PresetTokens = new[]
        {
            PresetLastDayToken, PresetLast7dToken, PresetLast30dToken,
            PresetThisYearToken, PresetAllToken,
        };

        /// <summary>The preset tokens, comma-joined for the reject message.</summary>
        internal static string PresetTokenNames => string.Join(",", PresetTokens);

        /// <summary>Whether a <c>value=</c> names one of the preset row's buttons. ORDINAL
        /// and case-sensitive, the whole seam's parse rule.</summary>
        internal static bool IsKnownPreset(string raw)
        {
            if (raw == null) return false;
            for (int i = 0; i < PresetTokens.Length; i++)
                if (string.Equals(PresetTokens[i], raw, StringComparison.Ordinal)) return true;
            return false;
        }

        // ----- the per-window key table -----

        private static readonly UiStateKeySpec[] TimelineKeys = new[]
        {
            // The three source toggles of the filter row. Each is an independent
            // row-population branch and all three read `true` in every census dump.
            NewKey(SrcRecordingsKey, UiStateKeyKind.Bool),
            NewKey(SrcActionsKey, UiStateKeyKind.Bool),
            NewKey(SrcEventsKey, UiStateKeyKind.Bool),

            // The shared archive filter, in the Timeline's positive sense. Also valid on
            // `missions` - see the class header.
            NewKey(ArchivedKey, UiStateKeyKind.Bool),

            // Reveals the From: / To: sliders, the window's only sliders.
            NewKey(CustomRangeKey, UiStateKeyKind.Bool),

            // The time-range preset row.
            NewKey(PresetKey, UiStateKeyKind.Value),

            // The entry list's scroll offset in pixels. There is no scroll op for the GUI
            // tree and none is needed (a dump carries below-fold content with full rects),
            // but the PNG beside it is a viewport-sized sample, so a lane that wants a
            // picture of a lower band of a long list needs this.
            NewKey(ScrollYKey, UiStateKeyKind.Value),
        };

        private static readonly UiStateKeySpec[] MissionsKeys = new[]
        {
            // Same flag, same polarity, same key as the Timeline's. The Recordings tab's
            // Archive header checkbox is the other control over it.
            NewKey(ArchivedKey, UiStateKeyKind.Bool),

            // The Missions tab's own archive filter, in MissionStore's HIDE sense.
            NewKey(ArchivedMissionsKey, UiStateKeyKind.Bool),

            // The Recordings tab's Info toggle: six extra columns and a window that widens
            // to its own DefaultExpandedWindowWidth. The largest single layout change in
            // the window, and absent from every capture.
            NewKey(ExpandedStatsKey, UiStateKeyKind.Bool),
        };

        private static UiStateKeySpec NewKey(string key, UiStateKeyKind kind)
            => new UiStateKeySpec { Key = key, Kind = kind };

        /// <summary>The keys a window accepts, or null when the window keeps no scalar
        /// state this op drives.</summary>
        internal static UiStateKeySpec[] StateKeysFor(string window)
        {
            if (window == TestCommandUiAction.TimelineWindow) return TimelineKeys;
            if (window == TestCommandUiAction.MissionsWindow) return MissionsKeys;
            return null;
        }

        /// <summary>The windows this op drives, comma-joined for the reject
        /// message.</summary>
        internal static string StatefulWindowNames =>
            TestCommandUiAction.TimelineWindow + "," + TestCommandUiAction.MissionsWindow;

        /// <summary>One window's key tokens, comma-joined, for the reject message.</summary>
        internal static string StateKeyNamesFor(string window)
        {
            UiStateKeySpec[] keys = StateKeysFor(window);
            if (keys == null) return string.Empty;
            var names = new List<string>(keys.Length);
            for (int i = 0; i < keys.Length; i++) names.Add(keys[i].Key);
            return string.Join(",", names.ToArray());
        }

        // ----- reject reasons -----

        /// <summary><c>op=state</c> against a window with no driveable scalar state. The
        /// message names the ones that have it.</summary>
        internal const string StateUnsupportedWindowReason = "state-unsupported-window";

        /// <summary>No <c>key=</c>. REQUIRED, never defaulted: there is no "the" state of a
        /// window, and guessing one would write a field the lane never named.</summary>
        internal const string StateKeyArgMissingReason = "state-key-arg-missing";

        /// <summary>A <c>key=</c> the named window does not keep. The message carries that
        /// window's own key list.</summary>
        internal const string StateKeyInvalidReason = "state-key-invalid";

        /// <summary><c>value=</c> on a BOOL key. Refused rather than ignored, the
        /// <c>expand-state-with-bulk-key</c> rule: a step that carries the wrong half of the
        /// grammar has misunderstood the key, and silently applying the other half would
        /// report OK over a state nobody asked for.</summary>
        internal const string StateValueArgNotForKeyReason = "state-value-arg-not-for-key";

        /// <summary><c>state=</c> on a VALUE key, the mirror of the above.</summary>
        internal const string StateBoolArgNotForKeyReason = "state-bool-arg-not-for-key";

        /// <summary>A VALUE key with no <c>value=</c>.</summary>
        internal const string StateValueArgMissingReason = "state-value-arg-missing";

        /// <summary>A <c>value=</c> outside the key's own shape: a preset name the row does
        /// not draw, or a scroll offset that is not a finite non-negative invariant-culture
        /// number. The message carries the shape.</summary>
        internal const string StateValueArgInvalidReason = "state-value-arg-invalid";

        /// <summary>
        /// PRE-WRITE gate: the requested state is one the window's ACTIVE TAB forces back.
        /// Today exactly one combination reaches it - <c>key=srcRecordings state=false</c>
        /// on the Timeline while the tier filter is Rewind/FF or Re-Fly - and the message
        /// names the tab so the remedy (an <c>op=tab</c> step first) is readable from the
        /// answer.
        /// </summary>
        internal const string StateClampedByTabReason = "state-clamped-by-tab";

        /// <summary>POST-SETTLE terminal: the write went through and, after a drawn frame,
        /// the state reads back different. ERROR - we acted and the game did not
        /// follow.</summary>
        internal const string StateNotAppliedReason = "state-not-applied";

        // ----- bounds -----

        /// <summary>Largest accepted <c>scrollY</c>. A scroll view clamps its own offset to
        /// its content, so an over-large value is harmless rather than wrong; the bound
        /// exists for the <c>op=rect</c> reason - a mis-typed exponent must not reach the
        /// layout pass.</summary>
        internal const float MaxScrollOffset = 100000f;

        // ----- parses -----

        /// <summary>Resolves a <c>key=</c> against a window's table.</summary>
        internal static bool TryResolveKey(string window, string rawKey,
                                           out UiStateKeySpec keySpec,
                                           out string rejectReason)
        {
            keySpec = default(UiStateKeySpec);
            UiStateKeySpec[] keys = StateKeysFor(window);
            if (keys == null)
            {
                rejectReason = StateUnsupportedWindowReason;
                return false;
            }
            if (string.IsNullOrEmpty(rawKey))
            {
                rejectReason = StateKeyArgMissingReason;
                return false;
            }
            for (int i = 0; i < keys.Length; i++)
            {
                if (!string.Equals(keys[i].Key, rawKey, StringComparison.Ordinal)) continue;
                keySpec = keys[i];
                rejectReason = null;
                return true;
            }
            rejectReason = StateKeyInvalidReason;
            return false;
        }

        /// <summary>
        /// Checks the two args against the resolved key's kind: a bool key takes
        /// <c>state=</c> and refuses <c>value=</c>, a value key the mirror.
        /// </summary>
        /// <param name="rawState">The <c>state=</c> arg, or null.</param>
        /// <param name="rawValue">The <c>value=</c> arg, or null.</param>
        internal static bool TryCheckArgShape(UiStateKeySpec keySpec, string rawState,
                                              string rawValue, out string rejectReason)
        {
            bool hasState = rawState != null;
            bool hasValue = rawValue != null;
            if (keySpec.Kind == UiStateKeyKind.Bool)
            {
                if (hasValue)
                {
                    rejectReason = StateValueArgNotForKeyReason;
                    return false;
                }
                if (!hasState)
                {
                    // REQUIRED here and not defaulted to `true` the way `op=expand`'s is:
                    // every key in this table is driven BOTH ways by a census (a filter ON
                    // is one picture, OFF is another), so a default direction would
                    // silently run the opposite half of a lane's check - the `op=playback`
                    // argument verbatim.
                    rejectReason = TestCommandUiAction.StateArgMissingReason;
                    return false;
                }
                rejectReason = null;
                return true;
            }
            if (hasState)
            {
                rejectReason = StateBoolArgNotForKeyReason;
                return false;
            }
            if (!hasValue)
            {
                rejectReason = StateValueArgMissingReason;
                return false;
            }
            rejectReason = null;
            return true;
        }

        /// <summary>Parses a BOOL key's <c>state=</c>. Reuses the one <c>state=</c>
        /// vocabulary and the one invalid reason the rest of the seam uses.</summary>
        internal static bool TryParseBoolState(string raw, out bool want,
                                               out string rejectReason)
        {
            // The seam's ONE boolean parse. Absent cannot reach here - TryCheckArgShape
            // already refused a bool key with no `state=` - so the whenAbsent value is the
            // unreachable arm rather than a default this op has.
            return TestCommandUiState.TryParseBoolArg(
                raw, whenAbsent: false, TestCommandUiState.StateArgInvalidReason,
                out want, out rejectReason);
        }

        /// <summary>Parses <c>key=scrollY</c>'s <c>value=</c>: a finite, non-negative,
        /// invariant-culture number within <see cref="MaxScrollOffset"/>.</summary>
        internal static bool TryParseScrollOffset(string raw, out float offset,
                                                  out string rejectReason)
        {
            offset = 0f;
            float parsed;
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture,
                                out parsed)
                || float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                rejectReason = StateValueArgInvalidReason;
                return false;
            }
            if (parsed < 0f || parsed > MaxScrollOffset)
            {
                rejectReason = StateValueArgInvalidReason;
                return false;
            }
            offset = parsed;
            rejectReason = null;
            return true;
        }

        /// <summary>
        /// Whether the Timeline's active tier filter forces
        /// <see cref="SrcRecordingsKey"/> back on, so a <c>state=false</c> against it is
        /// <see cref="StateClampedByTabReason"/> rather than a write.
        ///
        /// <para>Keyed by the TAB TOKEN rather than by the private enum so the rule has a
        /// cell on it without widening <c>TimelineTierFilterMode</c>'s accessibility - the
        /// <c>TierFilterModeIndexForTesting</c> argument. The two tokens are the two
        /// <c>actionFilterMode</c> arms of <c>DrawFilterBar</c>.</para>
        /// </summary>
        internal static bool IsSourceToggleClampedByTab(string window, string key,
                                                        bool want, string tabToken)
        {
            if (want) return false;
            if (window != TestCommandUiAction.TimelineWindow) return false;
            if (!string.Equals(key, SrcRecordingsKey, StringComparison.Ordinal)) return false;
            return string.Equals(tabToken, "rewindff", StringComparison.Ordinal)
                   || string.Equals(tabToken, "refly", StringComparison.Ordinal);
        }

        // ----- payload -----

        /// <summary>
        /// OK payload for <c>state</c>:
        /// <c>op=state window= key= want= after= changed=</c>.
        ///
        /// <para><c>after</c> is the SETTLED read-back rather than an echo of the request,
        /// because for one key it can legitimately differ from it: a scroll offset is
        /// clamped by the scroll view to its own content, so <c>want=9000 after=412</c> is
        /// the honest report of a list shorter than the offset asked for. <c>changed</c>
        /// separates "the window was already in that state" from "this op moved it", which a
        /// bare OK cannot - the <c>op=expand</c> rule.</para>
        /// </summary>
        internal static List<KeyValuePair<string, string>> BuildStatePayload(
            string window, string key, string want, string after, bool changed)
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("op", TestCommandUiAction.StateOpToken),
                new KeyValuePair<string, string>("window", window ?? string.Empty),
                new KeyValuePair<string, string>("key", key ?? string.Empty),
                new KeyValuePair<string, string>("want", want ?? string.Empty),
                new KeyValuePair<string, string>("after", after ?? string.Empty),
                new KeyValuePair<string, string>("changed", changed ? "true" : "false"),
            };

        /// <summary>The wire spelling of a bool state, shared by the payload and the log
        /// line so a reader greps one token.</summary>
        internal static string BoolToken(bool value)
            => value ? TestCommandUiState.StateTrueToken : TestCommandUiState.StateFalseToken;

        /// <summary>The wire spelling of a scroll offset. Invariant-culture and
        /// round-trippable, the serialization rule: the harness reads this number.</summary>
        internal static string OffsetToken(float value)
            => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
