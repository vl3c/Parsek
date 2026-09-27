using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <c>UiAction op=state</c> (<see cref="TestCommandUiWindowState"/>):
    /// the per-window key table, the two-shaped arg grammar, the value parses, the one
    /// tab-clamp refusal and the payload.
    ///
    /// <para>Three properties carry the weight. First, the ARG SHAPE must be refused rather
    /// than resolved by precedence: a <c>value=</c> on a bool key or a <c>state=</c> on a
    /// value key means the step has misunderstood the key, and silently applying the other
    /// half would report OK over a state nobody asked for. Second, <c>archived</c> must be
    /// ONE key on BOTH windows - two spellings of one persisted flag would make every lane
    /// that touched it read the source to learn the polarity. Third, the tab clamp must be a
    /// PRE-WRITE refusal, because the Timeline's own Rewind/FF and Re-Fly draw passes force
    /// that toggle back on, so a written-then-settled read-back would report an ERROR over a
    /// state the window does not have rather than naming the tab.</para>
    /// </summary>
    public class TestCommandUiWindowStateTests
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ----- the window table -----

        [Fact]
        public void OnlyTimelineAndMissionsHaveDriveableScalarState()
        {
            Assert.NotNull(TestCommandUiWindowState.StateKeysFor(
                TestCommandUiAction.TimelineWindow));
            Assert.NotNull(TestCommandUiWindowState.StateKeysFor(
                TestCommandUiAction.MissionsWindow));

            // Every other window in the seam's own table answers "no scalar state", which is
            // what makes the ABSENCE meaningful: op=state against it is a typed REJECTED
            // naming the two that do, not a silent no-op.
            foreach (UiWindowSpec spec in TestCommandUiAction.Windows)
            {
                if (spec.Name == TestCommandUiAction.TimelineWindow
                    || spec.Name == TestCommandUiAction.MissionsWindow)
                    continue;
                Assert.Null(TestCommandUiWindowState.StateKeysFor(spec.Name));
            }
        }

        [Fact]
        public void TheStatefulWindowNamesListNamesExactlyTheTwo()
        {
            string[] named = TestCommandUiWindowState.StatefulWindowNames.Split(',');
            Assert.Equal(
                new[] { TestCommandUiAction.TimelineWindow, TestCommandUiAction.MissionsWindow },
                named);
        }

        [Fact]
        public void EveryTimelineKeyIsInTheKeyNameList()
        {
            string names = TestCommandUiWindowState.StateKeyNamesFor(
                TestCommandUiAction.TimelineWindow);
            foreach (UiStateKeySpec key in TestCommandUiWindowState.StateKeysFor(
                         TestCommandUiAction.TimelineWindow))
                Assert.Contains(key.Key, names.Split(','));
        }

        [Fact]
        public void TheArchivedKeyIsOneKeyOnBothWindows()
        {
            // One persisted flag (GroupHierarchyStore.HideActive) that two windows draw a
            // control for, so it is spelled ONCE and is valid on both.
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, TestCommandUiWindowState.ArchivedKey,
                out UiStateKeySpec onTimeline, out string timelineReject));
            Assert.Null(timelineReject);
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.MissionsWindow, TestCommandUiWindowState.ArchivedKey,
                out UiStateKeySpec onMissions, out string missionsReject));
            Assert.Null(missionsReject);
            Assert.Equal(onTimeline.Kind, onMissions.Kind);
            Assert.Equal(UiStateKeyKind.Bool, onTimeline.Kind);
        }

        [Fact]
        public void TheMissionsOwnArchiveFilterIsASeparateKey()
        {
            // MissionStore.HideArchived is a different flag over a different list, so it is
            // a different key - and it keeps the store's own HIDE sense, there being no
            // second control to disagree with.
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.MissionsWindow,
                TestCommandUiWindowState.ArchivedMissionsKey, out UiStateKeySpec spec,
                out string reject));
            Assert.Null(reject);
            Assert.Equal(UiStateKeyKind.Bool, spec.Kind);
            Assert.NotEqual(TestCommandUiWindowState.ArchivedKey, spec.Key);
        }

        [Theory]
        [InlineData("srcRecordings")]
        [InlineData("srcActions")]
        [InlineData("srcEvents")]
        [InlineData("customRange")]
        public void TheTimelineToggleKeysAreBoolShaped(string key)
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, key, out UiStateKeySpec spec,
                out string reject));
            Assert.Null(reject);
            Assert.Equal(UiStateKeyKind.Bool, spec.Kind);
        }

        [Theory]
        [InlineData("preset")]
        [InlineData("scrollY")]
        public void ThePresetAndScrollKeysAreValueShaped(string key)
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, key, out UiStateKeySpec spec,
                out string reject));
            Assert.Null(reject);
            Assert.Equal(UiStateKeyKind.Value, spec.Kind);
        }

        // catches: the Missions window's horizontal scroll key dropped from its table, made
        // bool-shaped, or leaking onto the Timeline (whose list scrolls only vertically).
        [Fact]
        public void ScrollXIsAValueShapedMissionsKeyAndNotATimelineOne()
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.MissionsWindow, TestCommandUiWindowState.ScrollXKey,
                out UiStateKeySpec spec, out string ok));
            Assert.Null(ok);
            Assert.Equal(UiStateKeyKind.Value, spec.Kind);
            Assert.Equal("scrollX", spec.Key);

            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, TestCommandUiWindowState.ScrollXKey,
                out UiStateKeySpec _, out string reject));
            Assert.Equal(TestCommandUiWindowState.StateKeyInvalidReason, reject);
        }

        [Fact]
        public void ArchivedMissionsIsAMissionsKeyAndNotATimelineOne()
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.MissionsWindow,
                TestCommandUiWindowState.ArchivedMissionsKey, out UiStateKeySpec spec,
                out string ok));
            Assert.Null(ok);
            Assert.Equal(UiStateKeyKind.Bool, spec.Kind);

            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.ArchivedMissionsKey, out UiStateKeySpec _,
                out string reject));
            Assert.Equal(TestCommandUiWindowState.StateKeyInvalidReason, reject);
        }

        // catches: the removed Recordings-tab Info toggle's key coming back (the toggle,
        // its columns and the window-widening state were removed 2026-09-26).
        [Fact]
        public void ExpandedStatsIsNoLongerAKeyOfAnyWindow()
        {
            foreach (string window in new[] { TestCommandUiAction.MissionsWindow, TestCommandUiAction.TimelineWindow })
            {
                Assert.False(TestCommandUiWindowState.TryResolveKey(
                    window, "expandedStats", out UiStateKeySpec _, out string reject));
                Assert.Equal(TestCommandUiWindowState.StateKeyInvalidReason, reject);
            }
        }

        // ----- key resolution rejects -----

        [Fact]
        public void AWindowWithNoScalarStateIsItsOwnReject()
        {
            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.SettingsWindow, "srcActions", out UiStateKeySpec _,
                out string reject));
            Assert.Equal(TestCommandUiWindowState.StateUnsupportedWindowReason, reject);
        }

        [Fact]
        public void AMissingKeyIsDistinctFromAnUnknownOne()
        {
            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, null, out UiStateKeySpec _,
                out string missing));
            Assert.Equal(TestCommandUiWindowState.StateKeyArgMissingReason, missing);

            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, "", out UiStateKeySpec _,
                out string empty));
            Assert.Equal(TestCommandUiWindowState.StateKeyArgMissingReason, empty);

            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, "nosuchkey", out UiStateKeySpec _,
                out string unknown));
            Assert.Equal(TestCommandUiWindowState.StateKeyInvalidReason, unknown);
        }

        [Fact]
        public void TheKeyParseIsCaseSensitive()
        {
            // Fail-closed and ordinal, the whole seam's rule: a case variant is a typed
            // REJECTED rather than a guess.
            Assert.False(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow, "srcrecordings", out UiStateKeySpec _,
                out string reject));
            Assert.Equal(TestCommandUiWindowState.StateKeyInvalidReason, reject);
        }

        // ----- the arg shape -----

        [Fact]
        public void ABoolKeyRefusesAValueArg()
        {
            var spec = BoolKey();
            Assert.False(TestCommandUiWindowState.TryCheckArgShape(
                spec, null, "Last Day", out string reject));
            Assert.Equal(TestCommandUiWindowState.StateValueArgNotForKeyReason, reject);
        }

        [Fact]
        public void ABoolKeyRequiresItsStateArg()
        {
            var spec = BoolKey();
            Assert.False(TestCommandUiWindowState.TryCheckArgShape(
                spec, null, null, out string reject));
            // REQUIRED and not defaulted to `true` the way op=expand's is: every key here is
            // driven both ways by a census, so a default direction would silently run the
            // opposite half of a lane's check.
            Assert.Equal(TestCommandUiAction.StateArgMissingReason, reject);
        }

        [Fact]
        public void AValueKeyRefusesAStateArg()
        {
            var spec = ValueKey();
            Assert.False(TestCommandUiWindowState.TryCheckArgShape(
                spec, "true", null, out string reject));
            Assert.Equal(TestCommandUiWindowState.StateBoolArgNotForKeyReason, reject);
        }

        [Fact]
        public void AValueKeyRequiresItsValueArg()
        {
            var spec = ValueKey();
            Assert.False(TestCommandUiWindowState.TryCheckArgShape(
                spec, null, null, out string reject));
            Assert.Equal(TestCommandUiWindowState.StateValueArgMissingReason, reject);
        }

        [Fact]
        public void TheWellFormedShapesPass()
        {
            Assert.True(TestCommandUiWindowState.TryCheckArgShape(
                BoolKey(), "false", null, out string boolReject));
            Assert.Null(boolReject);
            Assert.True(TestCommandUiWindowState.TryCheckArgShape(
                ValueKey(), null, "Last 7d", out string valueReject));
            Assert.Null(valueReject);
        }

        // ----- the bool parse -----

        [Fact]
        public void TheBoolStateParseReusesTheOneStateVocabulary()
        {
            Assert.True(TestCommandUiWindowState.TryParseBoolState(
                "true", out bool yes, out string yesReject));
            Assert.True(yes);
            Assert.Null(yesReject);

            Assert.True(TestCommandUiWindowState.TryParseBoolState(
                "false", out bool no, out string noReject));
            Assert.False(no);
            Assert.Null(noReject);

            // The invalid half reuses the seam's single state-arg-invalid reason: `state=` is
            // one arg key with one closed vocabulary across every op that reads it.
            Assert.False(TestCommandUiWindowState.TryParseBoolState(
                "True", out bool _, out string caseReject));
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, caseReject);
            Assert.False(TestCommandUiWindowState.TryParseBoolState(
                "yes", out bool _, out string wordReject));
            Assert.Equal(TestCommandUiState.StateArgInvalidReason, wordReject);
        }

        // ----- presets -----

        [Theory]
        [InlineData("Last Day")]
        [InlineData("Last 7d")]
        [InlineData("Last 30d")]
        [InlineData("This Year")]
        [InlineData("All")]
        public void EveryPresetRowButtonIsAKnownToken(string token)
        {
            Assert.True(TestCommandUiWindowState.IsKnownPreset(token));
            Assert.Contains(token, TestCommandUiWindowState.PresetTokenNames.Split(','));
        }

        [Theory]
        [InlineData("last day")]
        [InlineData("Last  Day")]
        [InlineData("Yesterday")]
        [InlineData("")]
        [InlineData(null)]
        public void AnythingElseIsNotAPreset(string token)
        {
            Assert.False(TestCommandUiWindowState.IsKnownPreset(token));
        }

        [Fact]
        public void TheFourRangedPresetsResolveAndAllDoesNot()
        {
            // The resolver is the ONE copy of this arithmetic: the preset row's four buttons
            // and the seam applier both read it, so a census capture of a preset is a
            // statement about the button.
            const double ut = 1000000.0;
            foreach (string name in new[] { "Last Day", "Last 7d", "Last 30d", "This Year" })
            {
                Assert.True(TimeRangeFilterLogic.TryResolvePresetRange(
                    name, ut, out double min, out double max), name);
                Assert.True(max > min, name);
            }
            // "All" is the row's CLEAR button, not a range, so it reports no bounds.
            Assert.False(TimeRangeFilterLogic.TryResolvePresetRange(
                "All", ut, out double _, out double _));
            Assert.False(TimeRangeFilterLogic.TryResolvePresetRange(
                "nonsense", ut, out double _, out double _));
        }

        [Fact]
        public void TheRelativePresetsEndAtTheCurrentMomentAndNestByLength()
        {
            const double ut = 5000000.0;
            TimeRangeFilterLogic.TryResolvePresetRange("Last Day", ut,
                out double dayMin, out double dayMax);
            TimeRangeFilterLogic.TryResolvePresetRange("Last 7d", ut,
                out double weekMin, out double weekMax);
            TimeRangeFilterLogic.TryResolvePresetRange("Last 30d", ut,
                out double monthMin, out double monthMax);
            Assert.Equal(ut, dayMax);
            Assert.Equal(ut, weekMax);
            Assert.Equal(ut, monthMax);
            Assert.True(monthMin < weekMin);
            Assert.True(weekMin < dayMin);
        }

        [Fact]
        public void ThisYearBracketsTheCurrentMoment()
        {
            const double ut = 5000000.0;
            Assert.True(TimeRangeFilterLogic.TryResolvePresetRange(
                "This Year", ut, out double min, out double max));
            Assert.True(min <= ut);
            Assert.True(max > ut);
        }

        // ----- scrollY -----

        [Theory]
        [InlineData("0", 0f)]
        [InlineData("412", 412f)]
        [InlineData("412.5", 412.5f)]
        public void AWellFormedScrollOffsetParses(string raw, float expected)
        {
            Assert.True(TestCommandUiWindowState.TryParseScrollOffset(
                raw, out float offset, out string reject));
            Assert.Null(reject);
            // Both operands widened: Assert.Equal(float, float, int) is ambiguous
            // against the float-tolerance overload, and the precision form is the one
            // this cell means.
            Assert.Equal((double)expected, (double)offset, 3);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("1e9")]
        [InlineData("412,5")]
        [InlineData("nope")]
        [InlineData("")]
        [InlineData(null)]
        public void AMalformedOrOutOfRangeScrollOffsetIsRefused(string raw)
        {
            Assert.False(TestCommandUiWindowState.TryParseScrollOffset(
                raw, out float _, out string reject));
            Assert.Equal(TestCommandUiWindowState.StateValueArgInvalidReason, reject);
        }

        [Fact]
        public void TheScrollOffsetParseIsInvariantCulture()
        {
            // The comma form must stay a REJECT in every culture: this number crosses the
            // wire and the harness reads it back.
            using (new CultureScope("de-DE"))
            {
                Assert.False(TestCommandUiWindowState.TryParseScrollOffset(
                    "412,5", out float _, out string reject));
                Assert.Equal(TestCommandUiWindowState.StateValueArgInvalidReason, reject);
                Assert.True(TestCommandUiWindowState.TryParseScrollOffset(
                    "412.5", out float dot, out string _));
                Assert.Equal(412.5d, (double)dot, 3);
            }
        }

        [Fact]
        public void TheOffsetTokenIsInvariantCulture()
        {
            using (new CultureScope("de-DE"))
            {
                Assert.Equal("412.5", TestCommandUiWindowState.OffsetToken(412.5f));
            }
        }

        // ----- the tab clamp -----

        [Theory]
        [InlineData("rewindff")]
        [InlineData("refly")]
        public void TurningTheRecordingsSourceOffIsRefusedOnTheActionTabs(string tab)
        {
            // Those two tabs' own draw pass forces the toggle back on and draws it disabled,
            // so the write would be undone by the very frame the settle waits for.
            Assert.True(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcRecordingsKey, want: false, tabToken: tab));
        }

        [Theory]
        [InlineData("overview")]
        [InlineData("details")]
        public void TheSameRequestIsFineOnTheTwoTierTabs(string tab)
        {
            Assert.False(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcRecordingsKey, want: false, tabToken: tab));
        }

        [Fact]
        public void TheClampIsOneDirectionOneKeyAndOneWindow()
        {
            // Turning it ON is never clamped (the tab forces exactly that value).
            Assert.False(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcRecordingsKey, want: true, tabToken: "refly"));
            // The other two source toggles are not forced there (they are hidden, not
            // written back), so a write to them is not refused.
            Assert.False(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcActionsKey, want: false, tabToken: "refly"));
            // And the rule is the Timeline's alone.
            Assert.False(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.MissionsWindow,
                TestCommandUiWindowState.SrcRecordingsKey, want: false, tabToken: "refly"));
            // A window with no selector answers null for its tab token; that must not clamp.
            Assert.False(TestCommandUiWindowState.IsSourceToggleClampedByTab(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcRecordingsKey, want: false, tabToken: null));
        }

        [Fact]
        public void TheClampedTabTokensAreRealTimelineTabs()
        {
            // The gate is keyed by TAB TOKEN rather than by the window's private enum, so
            // this cell is what keeps the two literals agreeing with the seam's own table.
            Assert.True(TestCommandUiAction.TryResolveWindow(
                TestCommandUiAction.TimelineWindow, out UiWindowSpec spec, out string _));
            Assert.Contains("rewindff", spec.Tabs);
            Assert.Contains("refly", spec.Tabs);
        }

        // ----- the payload -----

        [Fact]
        public void ThePayloadCarriesTheRequestAndTheSettledReadBack()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiWindowState.BuildStatePayload(
                    TestCommandUiAction.TimelineWindow,
                    TestCommandUiWindowState.ScrollYKey, "9000", "412", changed: true);
            Assert.Equal(
                new[] { "op", "window", "key", "want", "after", "changed" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(TestCommandUiAction.StateOpToken, Value(payload, "op"));
            Assert.Equal(TestCommandUiAction.TimelineWindow, Value(payload, "window"));
            Assert.Equal(TestCommandUiWindowState.ScrollYKey, Value(payload, "key"));
            // want and after DIFFER on purpose: a scroll view clamps the offset to its own
            // content, so echoing the request would have hidden a list shorter than it.
            Assert.Equal("9000", Value(payload, "want"));
            Assert.Equal("412", Value(payload, "after"));
            Assert.Equal("true", Value(payload, "changed"));
        }

        [Fact]
        public void ThePayloadNeverCarriesANullValue()
        {
            List<KeyValuePair<string, string>> payload =
                TestCommandUiWindowState.BuildStatePayload(null, null, null, null, false);
            Assert.All(payload, kv => Assert.NotNull(kv.Value));
        }

        [Fact]
        public void TheBoolTokenIsTheOneStateVocabulary()
        {
            Assert.Equal(TestCommandUiState.StateTrueToken,
                TestCommandUiWindowState.BoolToken(true));
            Assert.Equal(TestCommandUiState.StateFalseToken,
                TestCommandUiWindowState.BoolToken(false));
        }

        // ----- the op registration -----

        [Fact]
        public void TheStateOpIsRegisteredWindowScopedAndTwoPhase()
        {
            Assert.True(TestCommandUiAction.TryParseOp(
                TestCommandUiAction.StateOpToken, out UiActionOp op, out string reject));
            Assert.Null(reject);
            Assert.Equal(UiActionOp.State, op);
            Assert.Equal(TestCommandUiAction.StateOpToken, TestCommandUiAction.OpToken(op));
            // It names a window, so the harness's own per-op arg validation must require one.
            Assert.True(TestCommandUiAction.OpNeedsWindow(op));
            // And it changes DRAWN state, so the read-back only means something after a frame
            // - for scrollY the frame is what clamps the value at all.
            Assert.True(TestCommandUiAction.OpIsTwoPhase(op));
            // IT IS in the host-showUI settle gate, unlike op=expand, and the difference is
            // what this op is FOR: it produces a picture of a drawn surface, so a frame that
            // never reached the window would leave the read-back comparing the field with
            // the value just written to it. Same reason it refuses a closed window pre-call.
            Assert.True(TestCommandUiAction.SettleChecksHostShowUi(op));
            Assert.True(TestCommandUiAction.OpRequiresWindowOpen(op));
            Assert.Contains(TestCommandUiAction.StateOpToken,
                TestCommandUiAction.ValidOpNames.Split(','));
        }

        // ----- helpers -----

        private static UiStateKeySpec BoolKey()
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.SrcActionsKey, out UiStateKeySpec spec,
                out string _));
            return spec;
        }

        private static UiStateKeySpec ValueKey()
        {
            Assert.True(TestCommandUiWindowState.TryResolveKey(
                TestCommandUiAction.TimelineWindow,
                TestCommandUiWindowState.PresetKey, out UiStateKeySpec spec, out string _));
            return spec;
        }

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.First(kv => kv.Key == key).Value;

        /// <summary>Runs a block under a named culture and restores the ambient one. The
        /// xUnit host runs under the OS culture, so a cell that means to PROVE a site is
        /// invariant has to pin a comma-decimal culture itself.</summary>
        private sealed class CultureScope : System.IDisposable
        {
            private readonly CultureInfo previous;

            internal CultureScope(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    CultureInfo.CreateSpecificCulture(name);
            }

            public void Dispose()
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
