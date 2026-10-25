using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the <c>LaunchSiteProbe</c> seam verb (lane LS-1, todo
    /// GC9-LAUNCH-SITE-CLEAR-RECOVERS-CLAIMED-VESSEL): the per-scene arg parse, the pre-press
    /// gates, the dialog wait and settle decisions, the button roles of stock's two
    /// launch-site dialogs, and the line / payload shapes the lane pins.
    /// </summary>
    public class TestCommandLaunchSiteProbeTests
    {
        private static bool Parse(TestCommandScene scene, string facility, string craft, string press,
            out LaunchSiteProbeRequest r, out string reason)
            => TestCommandLaunchSiteProbe.TryParse(scene, facility, craft, press, out r, out reason, out _);

        // ----- parse -----

        [Fact]
        public void Parse_Editor_DefaultsToAbort()
        {
            Assert.True(Parse(TestCommandScene.Editor, null, null, null, out var r, out var reason));
            Assert.Null(reason);
            Assert.Equal(LaunchSiteProbePath.Editor, r.Path);
            Assert.Equal(LaunchSiteProbePress.Abort, r.Press);
        }

        [Fact]
        public void Parse_Editor_TakesPressProceed()
        {
            Assert.True(Parse(TestCommandScene.Editor, null, null, "proceed", out var r, out _));
            Assert.Equal(LaunchSiteProbePress.Proceed, r.Press);
        }

        [Theory]
        [InlineData("VAB", null)]
        [InlineData(null, "Kerbal X")]
        public void Parse_Editor_RefusesTheSpaceCenterArgs(string facility, string craft)
        {
            Assert.False(Parse(TestCommandScene.Editor, facility, craft, null, out _, out var reason));
            Assert.Equal(TestCommandLaunchSiteProbe.ArgNotForSceneReason, reason);
        }

        [Theory]
        [InlineData("Proceed")]
        [InlineData("recover")]
        [InlineData("")]
        public void Parse_BadPress_RefusedBeforeTheScene(string press)
        {
            Assert.False(Parse(TestCommandScene.Flight, null, null, press, out _, out var reason));
            Assert.Equal(TestCommandLaunchSiteProbe.AnswerArgInvalidReason, reason);
        }

        [Fact]
        public void Parse_SpaceCenter_DefaultsToTheVab()
        {
            Assert.True(Parse(TestCommandScene.SpaceCenter, null, "Jumping Flea", "proceed", out var r, out _));
            Assert.Equal(LaunchSiteProbePath.SpaceCenter, r.Path);
            Assert.Equal(EditorRouteFacility.VAB, r.Facility);
            Assert.Equal("Jumping Flea", r.Craft);
            Assert.Equal(LaunchSiteProbePress.Proceed, r.Press);
        }

        [Fact]
        public void Parse_SpaceCenter_TakesTheSph()
        {
            Assert.True(Parse(TestCommandScene.SpaceCenter, "SPH", "Aeris 3A", null, out var r, out _));
            Assert.Equal(EditorRouteFacility.SPH, r.Facility);
        }

        [Fact]
        public void Parse_SpaceCenter_BadFacility()
        {
            Assert.False(Parse(TestCommandScene.SpaceCenter, "vab", "Kerbal X", null, out _, out var reason));
            Assert.Equal(TestCommandLaunchSiteProbe.FacilityArgInvalidReason, reason);
        }

        [Fact]
        public void Parse_SpaceCenter_NeedsACraft()
        {
            Assert.False(Parse(TestCommandScene.SpaceCenter, null, null, null, out _, out var reason));
            Assert.Equal(TestCommandLaunchSiteProbe.CraftArgMissingReason, reason);
            Assert.False(Parse(TestCommandScene.SpaceCenter, null, "  ", null, out _, out reason));
            Assert.Equal(TestCommandLaunchSiteProbe.CraftArgInvalidReason, reason);
        }

        [Fact]
        public void Parse_OtherScenes_Refused()
        {
            foreach (TestCommandScene scene in new[]
                { TestCommandScene.Flight, TestCommandScene.TrackingStation, TestCommandScene.MainMenu })
            {
                Assert.False(Parse(scene, null, "Kerbal X", null, out _, out var reason));
                Assert.Equal(TestCommandLaunchSiteProbe.WrongSceneReason, reason);
            }
        }

        // ----- gates -----

        [Fact]
        public void EditorGate_Order()
        {
            Assert.Equal(TestCommandLaunchSiteProbe.NoShipReason,
                TestCommandLaunchSiteProbe.DecideEditorGate(false, 5, true, 1));
            Assert.Equal(TestCommandLaunchSiteProbe.NoShipReason,
                TestCommandLaunchSiteProbe.DecideEditorGate(true, 0, true, 1));
            Assert.Equal(TestCommandLaunchSiteProbe.LaunchLockedReason,
                TestCommandLaunchSiteProbe.DecideEditorGate(true, 5, false, 1));
            Assert.Equal(TestCommandLaunchSiteProbe.SiteClearReason,
                TestCommandLaunchSiteProbe.DecideEditorGate(true, 5, true, 0));
            Assert.Null(TestCommandLaunchSiteProbe.DecideEditorGate(true, 5, true, 1));
        }

        [Fact]
        public void EditorLock_IsWaitedOutOnlyWhenItIsTheOnlyBlocker()
        {
            Assert.True(TestCommandLaunchSiteProbe.EditorLockIsOnlyBlocker(true, 5, false, 1));
            Assert.False(TestCommandLaunchSiteProbe.EditorLockIsOnlyBlocker(true, 5, true, 1));
            // A clear site behind the lock is refused at once: waiting buys nothing.
            Assert.False(TestCommandLaunchSiteProbe.EditorLockIsOnlyBlocker(true, 5, false, 0));
            Assert.False(TestCommandLaunchSiteProbe.EditorLockIsOnlyBlocker(true, 0, false, 1));
        }

        [Fact]
        public void SpaceCenterGate_AndSiteGate()
        {
            Assert.Equal(TestCommandLaunchSiteProbe.FacilityNotFoundReason,
                TestCommandLaunchSiteProbe.DecideSpaceCenterGate(false, false));
            Assert.Equal(TestCommandLaunchSiteProbe.PickerOpenReason,
                TestCommandLaunchSiteProbe.DecideSpaceCenterGate(true, true));
            Assert.Null(TestCommandLaunchSiteProbe.DecideSpaceCenterGate(true, false));
            Assert.Equal(TestCommandLaunchSiteProbe.SiteClearReason, TestCommandLaunchSiteProbe.DecideSiteGate(0));
            Assert.Null(TestCommandLaunchSiteProbe.DecideSiteGate(2));
        }

        // ----- await / settle -----

        [Fact]
        public void Await_SceneLeftWins_ThenTarget_ThenOther_ThenBudget()
        {
            Assert.Equal(LaunchSiteProbeAwaitOutcome.Launched,
                TestCommandLaunchSiteProbe.DecideAwait(true, true, true, 0));
            Assert.Equal(LaunchSiteProbeAwaitOutcome.Dialog,
                TestCommandLaunchSiteProbe.DecideAwait(false, true, true, 99));
            Assert.Equal(LaunchSiteProbeAwaitOutcome.OtherDialog,
                TestCommandLaunchSiteProbe.DecideAwait(false, false, true, 0));
            Assert.Equal(LaunchSiteProbeAwaitOutcome.NotYet,
                TestCommandLaunchSiteProbe.DecideAwait(false, false, false, 9.9));
            Assert.Equal(LaunchSiteProbeAwaitOutcome.TimedOut,
                TestCommandLaunchSiteProbe.DecideAwait(false, false, false,
                    TestCommandLaunchSiteProbe.DialogWaitSeconds));
        }

        [Fact]
        public void Settle_NeedsBothFloorsAndTheDialogGone()
        {
            int f = TestCommandLaunchSiteProbe.MinSettleFrames;
            double s = TestCommandLaunchSiteProbe.MinSettleSeconds;
            Assert.Equal(LaunchSiteProbeSettleOutcome.Launched,
                TestCommandLaunchSiteProbe.DecideSettle(true, true, 0, 0, false));
            Assert.Equal(LaunchSiteProbeSettleOutcome.Stayed,
                TestCommandLaunchSiteProbe.DecideSettle(false, false, f, s, false));
            Assert.Equal(LaunchSiteProbeSettleOutcome.NotYet,
                TestCommandLaunchSiteProbe.DecideSettle(false, false, f - 1, s, false));
            Assert.Equal(LaunchSiteProbeSettleOutcome.NotYet,
                TestCommandLaunchSiteProbe.DecideSettle(false, false, f, s - 0.01, false));
            Assert.Equal(LaunchSiteProbeSettleOutcome.NotYet,
                TestCommandLaunchSiteProbe.DecideSettle(false, true, f * 10, s * 10, false));
            Assert.Equal(LaunchSiteProbeSettleOutcome.TimedOut,
                TestCommandLaunchSiteProbe.DecideSettle(false, true, f * 10, s * 10, true));
        }

        // ----- button roles -----

        [Fact]
        public void Buttons_Editor_TwoButtons_AbortByLabel()
        {
            var labels = new List<string> { "Recover and launch", "Abort" };
            var b = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor, labels, "Abort");
            Assert.True(b.ShapeOk);
            Assert.Equal(1, b.AbortIndex);
            Assert.Equal(0, b.ProceedIndex);
        }

        [Fact]
        public void Buttons_Editor_AbortOnly_HasNoProceed()
        {
            // The fix nulls GetProceedOption, so stock's PreFlightCheck builds the abort alone.
            var b = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor,
                new List<string> { "Abort" }, "Abort");
            Assert.True(b.ShapeOk);
            Assert.Equal(0, b.AbortIndex);
            Assert.Equal(-1, b.ProceedIndex);
            Assert.Equal(0, TestCommandLaunchSiteProbe.ChooseButton(LaunchSiteProbePress.Proceed, b, out var token));
            Assert.Equal("abort", token);
        }

        [Fact]
        public void Buttons_Editor_UnknownShape()
        {
            Assert.False(TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor,
                new List<string> { "A", "B" }, "Abort").ShapeOk);
            Assert.False(TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor,
                new List<string> { "Abort", "B", "C" }, "Abort").ShapeOk);
            Assert.False(TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor,
                new List<string>(), "Abort").ShapeOk);
        }

        [Fact]
        public void Buttons_SpaceCenter_FixedThreeButtonOrder()
        {
            var b = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.SpaceCenter,
                new List<string> { "Go to vessel", "Recover and launch", "Cancel" }, null);
            Assert.True(b.ShapeOk);
            Assert.Equal(1, b.ProceedIndex);
            Assert.Equal(2, b.AbortIndex);
            Assert.Equal(1, TestCommandLaunchSiteProbe.ChooseButton(LaunchSiteProbePress.Proceed, b, out var t1));
            Assert.Equal("proceed", t1);
            Assert.Equal(2, TestCommandLaunchSiteProbe.ChooseButton(LaunchSiteProbePress.Abort, b, out var t2));
            Assert.Equal("abort", t2);
            Assert.False(TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.SpaceCenter,
                new List<string> { "a", "b" }, null).ShapeOk);
        }

        [Fact]
        public void ProceedInteractable_Token()
        {
            var sc = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.SpaceCenter,
                new List<string> { "a", "b", "c" }, null);
            Assert.Equal("0", TestCommandLaunchSiteProbe.ProceedInteractableToken(sc,
                new List<bool> { true, false, true }));
            Assert.Equal("1", TestCommandLaunchSiteProbe.ProceedInteractableToken(sc,
                new List<bool> { true, true, true }));
            var abortOnly = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.Editor,
                new List<string> { "Abort" }, "Abort");
            Assert.Equal("-", TestCommandLaunchSiteProbe.ProceedInteractableToken(abortOnly,
                new List<bool> { true }));
        }

        // ----- text and lines -----

        [Fact]
        public void OneLine_EscapesBreaksAndCaps()
        {
            Assert.Equal("-", TestCommandLaunchSiteProbe.OneLine(null));
            Assert.Equal("a\\nb\\nc", TestCommandLaunchSiteProbe.OneLine("a\r\nb\rc"));
            string longText = new string('x', TestCommandLaunchSiteProbe.MaxLoggedTextLength + 50);
            string capped = TestCommandLaunchSiteProbe.OneLine(longText);
            Assert.Equal(TestCommandLaunchSiteProbe.MaxLoggedTextLength + 3, capped.Length);
            Assert.EndsWith("...", capped);
        }

        [Fact]
        public void Lines_AreInvariant_AndCarryTheLanePinnedKeys()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                var r = new LaunchSiteProbeRequest
                {
                    Path = LaunchSiteProbePath.SpaceCenter,
                    Facility = EditorRouteFacility.VAB,
                    Craft = "Jumping Flea",
                    Press = LaunchSiteProbePress.Proceed,
                };
                Assert.Equal(
                    "launchsiteprobe start path=spacecenter scene=SPACECENTER facility=VAB craft=Jumping_Flea"
                    + " answer=proceed site=LaunchPad obstructing=1 first=Logi_Cargo_Rig",
                    TestCommandLaunchSiteProbe.FormatStartLine(r, "SPACECENTER", "LaunchPad", 1, "Logi Cargo Rig"));

                var labels = new List<string> { "Go", "Recover", "Cancel" };
                var interactable = new List<bool> { true, false, true };
                var b = TestCommandLaunchSiteProbe.ClassifyButtons(LaunchSiteProbePath.SpaceCenter, labels, null);
                Assert.Equal(
                    "launchsiteprobe dialog path=spacecenter name=LaunchSiteWarning buttons=3"
                    + " labels=[Go|Recover|Cancel] interactable=1,0,1 proceedOffered=1 proceedInteractable=0"
                    + " title=Site\\nBlocked description=Held",
                    TestCommandLaunchSiteProbe.FormatDialogLine(LaunchSiteProbePath.SpaceCenter,
                        "LaunchSiteWarning", "Site\nBlocked", labels, interactable, b, "Held"));

                string press = TestCommandLaunchSiteProbe.FormatPressLine(LaunchSiteProbePath.SpaceCenter,
                    "proceed", "Recover", false, LaunchSiteProbePress.Proceed);
                Assert.StartsWith("launchsiteprobe press path=spacecenter requested=proceed pressed=proceed"
                    + " label=Recover interactable=0 - through the button's own callback (OptionSelected);", press);

                Assert.Equal(
                    "launchsiteprobe complete path=spacecenter site=LaunchPad dialog=LaunchSiteWarning buttons=3"
                    + " proceedOffered=1 proceedInteractable=0 pressed=proceed outcome=stayed"
                    + " obstructingBefore=1 obstructingAfter=1 firstAfter=Logi_Cargo_Rig lockHeld=0 elapsed=2.5s",
                    TestCommandLaunchSiteProbe.FormatCompleteLine(LaunchSiteProbePath.SpaceCenter, "LaunchPad",
                        "LaunchSiteWarning", 3, true, "0", "proceed", "stayed", 1, 1, "Logi Cargo Rig",
                        false, 2.5));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void Payload_KeysInOrder()
        {
            var payload = TestCommandLaunchSiteProbe.BuildPayload(LaunchSiteProbePath.Editor, "LaunchPad",
                "TestWarning", 1, false, "-", "abort", "stayed", 1, 1);
            Assert.Equal(new[]
            {
                "path", "site", "dialog", "buttons", "proceedOffered", "proceedInteractable",
                "pressed", "outcome", "obstructingBefore", "obstructingAfter",
            }, payload.Select(kv => kv.Key).ToArray());
            Assert.Equal("editor", payload[0].Value);
            Assert.Equal("0", payload[4].Value);
            Assert.Equal("-", payload[5].Value);
        }

        [Fact]
        public void Vocabularies_AreDistinctAndPrefixed()
        {
            var all = TestCommandLaunchSiteProbe.Reasons.Concat(TestCommandLaunchSiteProbe.ErrorReasons).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
            Assert.All(all, r => Assert.StartsWith("launchsiteprobe-", r));
            Assert.Equal(new[] { "abort", "proceed" }, TestCommandLaunchSiteProbe.AnswerTokens);
        }
    }
}
