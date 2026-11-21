using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for <c>UiAction op=warp window=timeline</c>
    /// (<see cref="TestCommandUiWarpTime"/>): the date and confirm grammar, the spawncontrol
    /// stray-arg refusal, the post-confirm verdict, the payload shape, and two source gates
    /// that keep the seam pressing the Timeline button's OWN click body and the dialog's own
    /// labels.
    /// </summary>
    public class TestCommandUiWarpTimeTests
    {
        // ----- date grammar -----

        [Fact]
        public void TryParseDate_AllFourIntegers_Parses()
        {
            Assert.True(TestCommandUiWarpTime.TryParseDate("1", "9", "3", "41",
                out UiWarpDate date, out string reject));
            Assert.Null(reject);
            Assert.Equal(1, date.Year);
            Assert.Equal(9, date.Day);
            Assert.Equal(3, date.Hour);
            Assert.Equal(41, date.Minute);
        }

        [Fact]
        public void TryParseDate_FloorsLikeTheWarpBoxes()
        {
            // The box's own parse floors Year / Day at 1 and Hour / Minute at 0, so the seam
            // commits exactly what a player typing the same digits would.
            Assert.True(TestCommandUiWarpTime.TryParseDate("0", "-2", "-1", "-5",
                out UiWarpDate date, out _));
            Assert.Equal(1, date.Year);
            Assert.Equal(1, date.Day);
            Assert.Equal(0, date.Hour);
            Assert.Equal(0, date.Minute);
        }

        [Theory]
        [InlineData(null, "1", "0", "0")]
        [InlineData("1", null, "0", "0")]
        [InlineData("1", "1", null, "0")]
        [InlineData("1", "1", "0", null)]
        public void TryParseDate_AnyMissingField_RejectsMissing(
            string y, string d, string h, string m)
        {
            Assert.False(TestCommandUiWarpTime.TryParseDate(y, d, h, m, out _, out string reject));
            Assert.Equal(TestCommandUiWarpTime.DateArgMissingReason, reject);
            Assert.Equal("warp-date-arg-missing", reject);
        }

        [Theory]
        [InlineData("one", "1", "0", "0")]
        [InlineData("1", "1.5", "0", "0")]
        [InlineData("1", "1", "", "0")]
        [InlineData("1", "1", "0", "3:00")]
        public void TryParseDate_NonInteger_RejectsInvalid(string y, string d, string h, string m)
        {
            Assert.False(TestCommandUiWarpTime.TryParseDate(y, d, h, m, out _, out string reject));
            Assert.Equal(TestCommandUiWarpTime.DateArgInvalidReason, reject);
            Assert.Equal("warp-date-arg-invalid", reject);
        }

        // ----- confirm grammar -----

        [Fact]
        public void TryParseConfirm_WarpAndCancel_AreTheTwoTokens()
        {
            Assert.True(TestCommandUiWarpTime.TryParseConfirm("warp", out bool warp, out string r1));
            Assert.True(warp);
            Assert.Null(r1);
            Assert.True(TestCommandUiWarpTime.TryParseConfirm("cancel", out bool cancel, out string r2));
            Assert.False(cancel);
            Assert.Null(r2);
        }

        [Fact]
        public void TryParseConfirm_Missing_IsRequiredNotDefaulted()
        {
            Assert.False(TestCommandUiWarpTime.TryParseConfirm(null, out bool warp, out string reject));
            Assert.False(warp);
            Assert.Equal("warp-confirm-arg-missing", reject);
        }

        [Theory]
        [InlineData("Warp")]
        [InlineData("yes")]
        [InlineData("")]
        public void TryParseConfirm_OtherValue_RejectsCaseSensitive(string raw)
        {
            Assert.False(TestCommandUiWarpTime.TryParseConfirm(raw, out bool warp, out string reject));
            Assert.False(warp);
            Assert.Equal("warp-confirm-arg-invalid", reject);
        }

        [Fact]
        public void ConfirmButtonLabel_IsTheDialogsOwnLabel()
        {
            Assert.Equal(WarpToTimeController.ConfirmWarpButtonText,
                ParsekTestCommandAddon.TimelineWarpConfirmButtonLabel(true));
            Assert.Equal(WarpToTimeController.ConfirmCancelButtonText,
                ParsekTestCommandAddon.TimelineWarpConfirmButtonLabel(false));
            Assert.Equal("Warp", ParsekTestCommandAddon.TimelineWarpConfirmButtonLabel(true));
            Assert.Equal("Cancel", ParsekTestCommandAddon.TimelineWarpConfirmButtonLabel(false));
        }

        // ----- spawncontrol stray args -----

        [Fact]
        public void FirstTimelineOnlyArg_FindsADateOrConfirmArg()
        {
            Assert.Null(TestCommandUiWarpTime.FirstTimelineOnlyArg(null));
            Assert.Null(TestCommandUiWarpTime.FirstTimelineOnlyArg(
                new Dictionary<string, string> { { "op", "warp" }, { "window", "spawncontrol" } }));
            Assert.Equal("day", TestCommandUiWarpTime.FirstTimelineOnlyArg(
                new Dictionary<string, string> { { "op", "warp" }, { "day", "3" } }));
            Assert.Equal("confirm", TestCommandUiWarpTime.FirstTimelineOnlyArg(
                new Dictionary<string, string> { { "confirm", "warp" } }));
        }

        // ----- the post-confirm verdict -----

        [Fact]
        public void DecideConfirmOutcome_Cancel_IsAlwaysOk()
        {
            Assert.Null(TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.RewindThenForward, inFlight: false, confirmWarp: false,
                pendingAfter: false, deferredKscAfter: false, utAfter: 500, targetUT: 0));
        }

        [Fact]
        public void DecideConfirmOutcome_KscForward_NeedsTheClockAtTarget()
        {
            Assert.Null(TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.ForwardOnly, false, true, false, false,
                utAfter: 186060, targetUT: 186060));
            // Within the at-target epsilon still counts.
            Assert.Null(TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.ForwardOnly, false, true, false, false,
                utAfter: 186059.5, targetUT: 186060));
            Assert.Equal("warp-forward-not-applied", TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.ForwardOnly, false, true, false, false,
                utAfter: 185950, targetUT: 186060));
        }

        [Fact]
        public void DecideConfirmOutcome_KscRewind_NeedsThePendingWarpArmed()
        {
            // The controller keeps the pending forward warp only when the rewind requested
            // its Space Center load; a refused rewind drops it.
            Assert.Null(TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.RewindThenForward, false, true,
                pendingAfter: true, deferredKscAfter: false, utAfter: 600, targetUT: 0));
            Assert.Equal("warp-rewind-not-launched", TestCommandUiWarpTime.DecideConfirmOutcome(
                WarpToTimeMath.WarpPlanKind.RewindThenForward, false, true,
                pendingAfter: false, deferredKscAfter: false, utAfter: 600, targetUT: 0));
        }

        [Fact]
        public void DecideConfirmOutcome_InFlight_NeedsTheDeferredKscWarp()
        {
            // In flight both plans defer the whole warp to the Space Center; the clock does
            // not move inside the call, so a forward plan is not judged by the clock there.
            foreach (var plan in new[] { WarpToTimeMath.WarpPlanKind.ForwardOnly,
                                         WarpToTimeMath.WarpPlanKind.RewindThenForward })
            {
                Assert.Null(TestCommandUiWarpTime.DecideConfirmOutcome(
                    plan, inFlight: true, confirmWarp: true, pendingAfter: false,
                    deferredKscAfter: true, utAfter: 100, targetUT: 5000));
                Assert.Equal("warp-not-deferred", TestCommandUiWarpTime.DecideConfirmOutcome(
                    plan, inFlight: true, confirmWarp: true, pendingAfter: false,
                    deferredKscAfter: false, utAfter: 100, targetUT: 5000));
            }
        }

        // ----- tokens and payload -----

        [Fact]
        public void PlanAndRewindKindTokens_AreStable()
        {
            Assert.Equal("forward", TestCommandUiWarpTime.PlanToken(WarpToTimeMath.WarpPlanKind.ForwardOnly));
            Assert.Equal("rewind", TestCommandUiWarpTime.PlanToken(WarpToTimeMath.WarpPlanKind.RewindThenForward));
            Assert.Equal("at-target", TestCommandUiWarpTime.PlanToken(WarpToTimeMath.WarpPlanKind.AtTarget));
            Assert.Equal("unreachable", TestCommandUiWarpTime.PlanToken(WarpToTimeMath.WarpPlanKind.Unreachable));
            Assert.Equal("career-start",
                ParsekTestCommandAddon.TimelineWarpRewindKindToken(WarpToTimeController.RewindTargetKind.CareerStart));
            Assert.Equal("launch",
                ParsekTestCommandAddon.TimelineWarpRewindKindToken(WarpToTimeController.RewindTargetKind.Launch));
            Assert.Equal("none",
                ParsekTestCommandAddon.TimelineWarpRewindKindToken(WarpToTimeController.RewindTargetKind.None));
        }

        [Fact]
        public void PayloadOpAndWindowTokens_AreTheUiActionTokens()
        {
            Assert.Equal(TestCommandUiAction.WarpOpToken, TestCommandUiWarpTime.OpToken);
            Assert.Equal(TestCommandUiAction.TimelineWindow, TestCommandUiWarpTime.WindowToken);
        }

        [Fact]
        public void BuildPayload_EchoesDatePlanAndStandingRequests()
        {
            var payload = TestCommandUiWarpTime.BuildPayload(
                new UiWarpDate { Year = 1, Day = 1, Hour = 0, Minute = 0 }, 0.0,
                WarpToTimeMath.WarpPlanKind.RewindThenForward,
                "career-start",
                confirmWarp: true, pendingAfter: true, deferredKscAfter: false);
            Assert.Equal(new[] { "op", "window", "year", "day", "hour", "minute", "targetUT",
                                 "plan", "rewindKind", "confirm", "pending", "deferredKsc" },
                payload.Select(kv => kv.Key).ToArray());
            Assert.Equal(new[] { "warp", "timeline", "1", "1", "0", "0", "0.0", "rewind",
                                 "career-start", "warp", "true", "false" },
                payload.Select(kv => kv.Value).ToArray());
        }

        [Fact]
        public void BuildPayload_TargetUT_IsInvariantCulture()
        {
            var prior = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");
                var payload = TestCommandUiWarpTime.BuildPayload(
                    new UiWarpDate { Year = 1, Day = 9, Hour = 3, Minute = 41 }, 1234.56,
                    WarpToTimeMath.WarpPlanKind.ForwardOnly,
                    "none", true, false, false);
                Assert.Equal("1234.6", payload.Single(kv => kv.Key == "targetUT").Value);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = prior;
            }
        }

        // ----- source gates -----

        private static string RepoRoot()
            => Path.GetFullPath(Path.Combine(System.AppContext.BaseDirectory,
                "..", "..", "..", "..", ".."));

        private static string ReadSource(params string[] parts)
            => File.ReadAllText(Path.Combine(new[] { RepoRoot(), "Source", "Parsek" }
                .Concat(parts).ToArray()));

        [Fact]
        public void TheDrawnButtonAndTheSeamShareOneClickBodyAndOneResolver()
        {
            // The seam must press the button the frame draws, through the body the frame
            // runs: the drawn button's click calls ExecuteWarpToTimeClick, the seam's press
            // calls it too, and both read the state from ResolveWarpToTimeButtonState. A
            // second spelling of either (a direct RequestWarp in the seam, a gate computed
            // twice) would let the seam warp where a player could not.
            string ui = ReadSource("UI", "TimelineWindowUI.cs");
            Assert.Contains("if (warpToTimeClicked)\n                ExecuteWarpToTimeClick(flight, inFlight);",
                ui.Replace("\r\n", "\n"));
            Assert.Equal(1, CountOf(ui, "WarpToTimeController.RequestWarp("));
            Assert.Equal(2, CountOf(ui, "ResolveWarpToTimeButtonState(inFlight,"));
            Assert.Equal(2, CountOf(ui, "ExecuteWarpToTimeClick(flight, inFlight)"));

            string applier = ReadSource("TestCommands", "ParsekTestCommandAddon.UiWarp.cs");
            Assert.DoesNotContain("RequestWarp(", applier);
            Assert.Contains("TryPressWarpToTimeForTesting(", applier);
        }

        [Fact]
        public void TheConfirmDialogIsSpawnedUnderTheNameAndLabelsTheSeamLooksFor()
        {
            string ctl = ReadSource("Controllers", "WarpToTimeController.cs");
            Assert.Contains("ConfirmDialogName = \"ParsekWarpToTimeConfirm\"", ctl);
            Assert.Contains("new DialogGUIButton(ConfirmWarpButtonText,", ctl);
            Assert.Contains("new DialogGUIButton(ConfirmCancelButtonText,", ctl);
            Assert.Contains("                    ConfirmDialogName,", ctl);
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0)
            {
                n++;
                at += needle.Length;
            }
            return n;
        }
    }
}
