using System;
using UnityEngine;

namespace Parsek.TestCommands
{
    /// <summary>
    /// GUI-census partial: the applier for <c>UiAction op=state</c>, the op that drives a
    /// window's SCALAR view state. The key table, the arg-shape rules and the payload live in
    /// the pure sibling <see cref="TestCommandUiWindowState"/>; this file writes the live
    /// fields and reads them back, and nothing else.
    ///
    /// <para>THE SAME ARGUMENT AS THE REST OF <c>UiAction</c>: every state below is a plain
    /// property over a field whose only other writer is a <c>GUILayout.Toggle</c> handler in
    /// the window's own file, or - for <c>preset</c> - the window's own click body called
    /// directly. No synthesised input, no new player-facing surface.</para>
    ///
    /// <para>ONE STATE IS NOT A FIELD ON A WINDOW. <c>archived</c> is
    /// <c>GroupHierarchyStore.HideActive</c>, a PERSISTED store bool two windows draw a
    /// control for, reached through <c>TimelineWindowUI.ShowArchivedRecordings</c> so the
    /// polarity flip lives where production already put it. It therefore survives the save,
    /// unlike every other key here - which is why a census lane that sets it runs on a
    /// staged, throwaway save exactly as the mission-selection op does.</para>
    /// </summary>
    public partial class ParsekTestCommandAddon
    {
        private void UiActionStateOp(ParsedCommand cmd, ParsekUI ui, UiWindowSpec spec)
        {
            string rawKey = ArgOrNull(cmd, TestCommandUiState.KeyArg);
            if (!TestCommandUiWindowState.TryResolveKey(spec.Name, rawKey,
                                                        out UiStateKeySpec keySpec,
                                                        out string keyReject))
            {
                string detail =
                    keyReject == TestCommandUiWindowState.StateUnsupportedWindowReason
                        ? $"{keyReject} window={spec.Name} "
                          + $"valid={TestCommandUiWindowState.StatefulWindowNames}"
                        : keyReject == TestCommandUiWindowState.StateKeyInvalidReason
                            ? $"{keyReject} window={spec.Name} key={rawKey ?? string.Empty} "
                              + $"keys={TestCommandUiWindowState.StateKeyNamesFor(spec.Name)}"
                            : $"{keyReject} window={spec.Name}";
                ParsekLog.Warn(Tag, $"uiaction rejected reason={keyReject} "
                    + $"window={spec.Name} key={rawKey ?? string.Empty}");
                SetExecResult("REJECTED", null, detail);
                return;
            }

            string rawState = ArgOrNull(cmd, TestCommandUiState.StateArg);
            string rawValue = ArgOrNull(cmd, TestCommandUiWindowState.ValueArg);
            if (!TestCommandUiWindowState.TryCheckArgShape(keySpec, rawState, rawValue,
                                                           out string shapeReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={shapeReject} "
                    + $"window={spec.Name} key={keySpec.Key}");
                SetExecResult("REJECTED", null,
                    $"{shapeReject} window={spec.Name} key={keySpec.Key}");
                return;
            }

            if (keySpec.Kind == UiStateKeyKind.Bool)
            {
                UiActionStateBoolKey(ui, spec, keySpec, rawState);
                return;
            }
            UiActionStateValueKey(ui, spec, keySpec, rawValue);
        }

        // ----- the bool keys -----

        private void UiActionStateBoolKey(ParsekUI ui, UiWindowSpec spec,
                                          UiStateKeySpec keySpec, string rawState)
        {
            if (!TestCommandUiWindowState.TryParseBoolState(rawState, out bool want,
                                                            out string stateReject))
            {
                ParsekLog.Warn(Tag, $"uiaction rejected reason={stateReject} "
                    + $"state={rawState ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{stateReject} state={rawState ?? string.Empty}");
                return;
            }

            // PRE-WRITE: one combination the active tab's own draw pass would undo. Named
            // rather than written-and-reported, so the answer carries the remedy.
            string tabToken = ResolveLiveTabToken(ui, spec);
            if (TestCommandUiWindowState.IsSourceToggleClampedByTab(
                    spec.Name, keySpec.Key, want, tabToken))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiWindowState.StateClampedByTabReason
                    + $" window={spec.Name} key={keySpec.Key} tab={tabToken}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiWindowState.StateClampedByTabReason} "
                    + $"window={spec.Name} key={keySpec.Key} tab={tabToken}");
                return;
            }

            bool before = ReadBoolState(ui, spec.Name, keySpec.Key);
            if (before != want) WriteBoolState(ui, spec.Name, keySpec.Key, want);
            bool after = ReadBoolState(ui, spec.Name, keySpec.Key);
            if (after != want)
            {
                // Only reachable if a property declines the value with no draw in between.
                // ERROR, not REJECTED: we acted and the game did not follow.
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiWindowState.StateNotAppliedReason
                    + $" window={spec.Name} key={keySpec.Key} "
                    + $"want={TestCommandUiWindowState.BoolToken(want)} "
                    + $"after={TestCommandUiWindowState.BoolToken(after)}");
                SetExecResult("ERROR", null,
                    $"{TestCommandUiWindowState.StateNotAppliedReason} "
                    + $"window={spec.Name} key={keySpec.Key}");
                return;
            }

            ArmStatePending(spec.Name, keySpec.Key,
                            TestCommandUiWindowState.BoolToken(want), before != want);
        }

        // ----- the value keys -----

        private void UiActionStateValueKey(ParsekUI ui, UiWindowSpec spec,
                                           UiStateKeySpec keySpec, string rawValue)
        {
            if (keySpec.Key == TestCommandUiWindowState.ScrollXKey)
            {
                if (!TestCommandUiWindowState.TryParseScrollOffset(
                        rawValue, out float offsetX, out string offsetXReject))
                {
                    ParsekLog.Warn(Tag, $"uiaction rejected reason={offsetXReject} "
                        + $"key={keySpec.Key} value={rawValue ?? string.Empty}");
                    SetExecResult("REJECTED", null,
                        $"{offsetXReject} key={keySpec.Key} "
                        + $"value={rawValue ?? string.Empty} "
                        + $"max={TestCommandUiWindowState.OffsetToken(TestCommandUiWindowState.MaxScrollOffset)}");
                    return;
                }
                WideWindowScroll wide = ui.GetRecordingsTableUI().WideScroll;
                float beforeX = wide.ScrollX;
                wide.ScrollX = offsetX;
                // Settled read-back only: the horizontal scroll view clamps the offset to
                // its content on the next draw, and reads 0 while the window fits.
                ArmStatePending(spec.Name, keySpec.Key,
                                TestCommandUiWindowState.OffsetToken(offsetX),
                                Math.Abs(beforeX - offsetX) > 0.001f);
                return;
            }

            if (keySpec.Key == TestCommandUiWindowState.ScrollYKey)
            {
                if (!TestCommandUiWindowState.TryParseScrollOffset(
                        rawValue, out float offset, out string offsetReject))
                {
                    ParsekLog.Warn(Tag, $"uiaction rejected reason={offsetReject} "
                        + $"key={keySpec.Key} value={rawValue ?? string.Empty}");
                    SetExecResult("REJECTED", null,
                        $"{offsetReject} key={keySpec.Key} "
                        + $"value={rawValue ?? string.Empty} "
                        + $"max={TestCommandUiWindowState.OffsetToken(TestCommandUiWindowState.MaxScrollOffset)}");
                    return;
                }
                TimelineWindowUI tl = ui.GetTimelineUI();
                float beforeY = tl.EntryScrollYForTesting;
                tl.EntryScrollYForTesting = offset;
                // No immediate read-back assertion: the scroll view CLAMPS the offset to its
                // own content during the draw, so the only honest report is the settled one.
                ArmStatePending(spec.Name, keySpec.Key,
                                TestCommandUiWindowState.OffsetToken(offset),
                                Math.Abs(beforeY - offset) > 0.001f);
                return;
            }

            // key=preset
            if (!TestCommandUiWindowState.IsKnownPreset(rawValue))
            {
                ParsekLog.Warn(Tag, "uiaction rejected reason="
                    + TestCommandUiWindowState.StateValueArgInvalidReason
                    + $" key={keySpec.Key} value={rawValue ?? string.Empty}");
                SetExecResult("REJECTED", null,
                    $"{TestCommandUiWindowState.StateValueArgInvalidReason} "
                    + $"key={keySpec.Key} value={rawValue ?? string.Empty} "
                    + $"presets={TestCommandUiWindowState.PresetTokenNames}");
                return;
            }
            TimelineWindowUI timeline = ui.GetTimelineUI();
            string beforePreset = timeline.ActiveTimeRangePresetForTesting;
            timeline.ApplyTimeRangePresetForTesting(rawValue, ReadUniversalTimeOrZero());
            string afterPreset = timeline.ActiveTimeRangePresetForTesting;
            ArmStatePending(spec.Name, keySpec.Key, rawValue,
                            !string.Equals(beforePreset, afterPreset, StringComparison.Ordinal));
        }

        // ----- shared arm / settle -----

        private void ArmStatePending(string window, string key, string want, bool changed)
        {
            uiActionPending = new UiActionPending
            {
                Op = UiActionOp.State,
                Window = window,
                StartFrame = Time.frameCount,
                StateKey = key,
                StateWant = want,
                StateChanged = changed,
            };
            ParsekLog.Info(Tag, $"uiaction state initiated window={window} key={key} "
                + $"want={want} changed={Bool(changed)} (awaiting one drawn frame)");
            SetExecResult(PendingVerdict, null, null);
        }

        private void CompleteUiActionState(UiActionSettleContext ctx,
                                           UiActionPending pending)
        {
            string after = ReadStateAsToken(ctx.Ui, pending.Window, pending.StateKey);

            // `scrollY` is the ONE key whose settled value may legitimately differ from the
            // request (the scroll view clamps it to its content), so a disagreement is not an
            // error for it. Every other key is a plain bool or the window's own stored preset
            // name: those the game either followed or did not.
            bool mayDiffer = pending.StateKey == TestCommandUiWindowState.ScrollYKey
                             || pending.StateKey == TestCommandUiWindowState.ScrollXKey;
            if (!mayDiffer
                && !string.Equals(after, pending.StateWant, StringComparison.Ordinal))
            {
                ParsekLog.Error(Tag, "uiaction error reason="
                    + TestCommandUiWindowState.StateNotAppliedReason
                    + $" window={pending.Window} key={pending.StateKey} "
                    + $"want={pending.StateWant} after={after} frames={Int(ctx.Frames)}");
                EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "ERROR", null,
                    $"{TestCommandUiWindowState.StateNotAppliedReason} "
                    + $"window={pending.Window} key={pending.StateKey} "
                    + $"want={pending.StateWant} after={after}",
                    dequeueHead: true);
                return;
            }

            ParsekLog.Info(Tag, $"uiaction state window={pending.Window} "
                + $"key={pending.StateKey} want={pending.StateWant} after={after} "
                + $"changed={Bool(pending.StateChanged)} frames={Int(ctx.Frames)}");
            EmitExecutedTerminal(ctx.Id, ctx.Seq, ctx.Verb, "OK",
                TestCommandUiWindowState.BuildStatePayload(
                    pending.Window, pending.StateKey, pending.StateWant, after,
                    pending.StateChanged),
                null, dequeueHead: true);
        }

        // ----- the live reads and writes, one site per key -----

        /// <summary>The window's live tab token, or null when it has no selector. Used by the
        /// one pre-write clamp gate.</summary>
        private static string ResolveLiveTabToken(ParsekUI ui, UiWindowSpec spec)
        {
            if (spec.Name == TestCommandUiAction.TimelineWindow)
                return TestCommandUiAction.TabTokenAt(
                    spec, ui.GetTimelineUI().TierFilterModeIndexForTesting);
            if (spec.Name == TestCommandUiAction.MissionsWindow)
                return TestCommandUiAction.TabTokenAt(
                    spec, ui.GetRecordingsTableUI().SelectedTabForTesting);
            return null;
        }

        private static bool ReadBoolState(ParsekUI ui, string window, string key)
        {
            switch (key)
            {
                case TestCommandUiWindowState.SrcRecordingsKey:
                    return ui.GetTimelineUI().ShowRecordingEntriesForTesting;
                case TestCommandUiWindowState.SrcActionsKey:
                    return ui.GetTimelineUI().ShowActionEntriesForTesting;
                case TestCommandUiWindowState.SrcEventsKey:
                    return ui.GetTimelineUI().ShowEventEntriesForTesting;
                case TestCommandUiWindowState.CustomRangeKey:
                    return ui.GetTimelineUI().ShowCustomRangeForTesting;
                // ONE flag, reached through the production property that owns the polarity
                // flip. Valid on both windows and identical on both.
                case TestCommandUiWindowState.ArchivedKey:
                    return TimelineWindowUI.ShowArchivedRecordings;
                case TestCommandUiWindowState.ArchivedMissionsKey:
                    return MissionStore.HideArchived;
                default:
                    // Unreachable through the parse (the key came from that window's own
                    // table), so a miss here means the table and this switch have drifted.
                    throw new InvalidOperationException(
                        "no live read for op=state key=" + (key ?? "<null>")
                        + " window=" + (window ?? "<null>"));
            }
        }

        private static void WriteBoolState(ParsekUI ui, string window, string key, bool value)
        {
            switch (key)
            {
                case TestCommandUiWindowState.SrcRecordingsKey:
                    ui.GetTimelineUI().ShowRecordingEntriesForTesting = value;
                    return;
                case TestCommandUiWindowState.SrcActionsKey:
                    ui.GetTimelineUI().ShowActionEntriesForTesting = value;
                    return;
                case TestCommandUiWindowState.SrcEventsKey:
                    ui.GetTimelineUI().ShowEventEntriesForTesting = value;
                    return;
                case TestCommandUiWindowState.CustomRangeKey:
                    ui.GetTimelineUI().ShowCustomRangeForTesting = value;
                    return;
                case TestCommandUiWindowState.ArchivedKey:
                    TimelineWindowUI.ShowArchivedRecordings = value;
                    return;
                case TestCommandUiWindowState.ArchivedMissionsKey:
                    MissionStore.HideArchived = value;
                    return;
                default:
                    throw new InvalidOperationException(
                        "no live write for op=state key=" + (key ?? "<null>")
                        + " window=" + (window ?? "<null>"));
            }
        }

        /// <summary>The settled value of any key, in the same spelling the request used, so
        /// the settle can compare the two as strings.</summary>
        private static string ReadStateAsToken(ParsekUI ui, string window, string key)
        {
            if (key == TestCommandUiWindowState.ScrollYKey)
                return TestCommandUiWindowState.OffsetToken(
                    ui.GetTimelineUI().EntryScrollYForTesting);
            if (key == TestCommandUiWindowState.ScrollXKey)
                return TestCommandUiWindowState.OffsetToken(
                    ui.GetRecordingsTableUI().WideScroll.ScrollX);
            if (key == TestCommandUiWindowState.PresetKey)
                return ui.GetTimelineUI().ActiveTimeRangePresetForTesting ?? string.Empty;
            return TestCommandUiWindowState.BoolToken(ReadBoolState(ui, window, key));
        }

        /// <summary>Universal time, or zero when Planetarium is not up. Zero is safe for the
        /// one caller (a preset resolved at UT 0 clamps to the slider bounds like any other),
        /// and the alternative - refusing - would refuse a legitimate op for a clock read.
        /// </summary>
        private static double ReadUniversalTimeOrZero()
        {
            try { return Planetarium.GetUniversalTime(); }
            catch { return 0.0; }
        }
    }
}
