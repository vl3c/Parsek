using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half of the editor scene-route verbs (<c>GoToEditor</c> /
    /// <c>LaunchFromEditor</c>): arg parsing, the craft lookup order, the settle poll, the
    /// launch gate and the launch completion.
    /// </summary>
    public class TestCommandEditorRouteTests
    {
        // ---- GoToEditor args ----

        [Theory]
        [InlineData("VAB", (int)EditorRouteFacility.VAB)]
        [InlineData("SPH", (int)EditorRouteFacility.SPH)]
        public void TryParseGoToEditor_AcceptsBothFacilities(string raw, int expectedValue)
        {
            var expected = (EditorRouteFacility)expectedValue;
            Assert.True(TestCommandEditorRoute.TryParseGoToEditor(
                raw, null, out EditorRouteFacility f, out string craft, out string reason, out _));
            Assert.Equal(expected, f);
            Assert.Null(craft);
            Assert.Null(reason);
        }

        [Fact]
        public void TryParseGoToEditor_MissingFacility_IsTyped()
        {
            Assert.False(TestCommandEditorRoute.TryParseGoToEditor(
                null, "Kerbal X", out _, out _, out string reason, out string detail));
            Assert.Equal(TestCommandEditorRoute.FacilityArgMissingReason, reason);
            Assert.Contains("valid=VAB,SPH", detail);
        }

        [Theory]
        [InlineData("vab")]
        [InlineData("Hangar")]
        [InlineData("None")]
        public void TryParseGoToEditor_FacilityIsCaseSensitiveAndClosed(string raw)
        {
            Assert.False(TestCommandEditorRoute.TryParseGoToEditor(
                raw, null, out _, out _, out string reason, out string detail));
            Assert.Equal(TestCommandEditorRoute.FacilityArgInvalidReason, reason);
            Assert.Contains("facility=" + raw, detail);
        }

        [Theory]
        [InlineData("../persistent")]
        [InlineData("..")]
        [InlineData("VAB/Kerbal X")]
        [InlineData("C:\\ships\\x")]
        [InlineData("  ")]
        [InlineData("")]
        public void TryParseGoToEditor_UnsafeCraftName_IsRefused(string raw)
        {
            Assert.False(TestCommandEditorRoute.TryParseGoToEditor(
                "VAB", raw, out _, out _, out string reason, out _));
            Assert.Equal(TestCommandEditorRoute.CraftArgInvalidReason, reason);
        }

        [Fact]
        public void TryParseGoToEditor_CraftWithSpacesIsABareStem()
        {
            Assert.True(TestCommandEditorRoute.TryParseGoToEditor(
                "SPH", "Kerbal X", out EditorRouteFacility f, out string craft, out _, out _));
            Assert.Equal(EditorRouteFacility.SPH, f);
            Assert.Equal("Kerbal X", craft);
        }

        // ---- craft lookup ----

        [Fact]
        public void CraftCandidates_OwnFolderFirst_ThenOther_ThenStock()
        {
            List<string> c = TestCommandEditorRoute.CraftCandidates(
                "/ksp/saves/run/Ships", "/ksp/Ships/", EditorRouteFacility.SPH, "Kerbal X");
            Assert.Equal(new[]
            {
                "/ksp/saves/run/Ships/SPH/Kerbal X.craft",
                "/ksp/saves/run/Ships/VAB/Kerbal X.craft",
                "/ksp/Ships/SPH/Kerbal X.craft",
                "/ksp/Ships/VAB/Kerbal X.craft",
            }, c);
        }

        [Fact]
        public void ResolveCraftPath_SaveCraftShadowsStock()
        {
            var present = new HashSet<string>
            {
                "/ksp/saves/run/Ships/VAB/Kerbal X.craft",
                "/ksp/Ships/VAB/Kerbal X.craft",
            };
            Assert.Equal("/ksp/saves/run/Ships/VAB/Kerbal X.craft",
                TestCommandEditorRoute.ResolveCraftPath(
                    "/ksp/saves/run/Ships", "/ksp/Ships", EditorRouteFacility.VAB, "Kerbal X", present.Contains));
        }

        [Fact]
        public void ResolveCraftPath_FallsBackToStockOtherFacility()
        {
            var present = new HashSet<string> { "/ksp/Ships/VAB/Kerbal X.craft" };
            Assert.Equal("/ksp/Ships/VAB/Kerbal X.craft",
                TestCommandEditorRoute.ResolveCraftPath(
                    "/ksp/saves/run/Ships", "/ksp/Ships", EditorRouteFacility.SPH, "Kerbal X", present.Contains));
        }

        [Fact]
        public void ResolveCraftPath_NothingPresent_IsNull()
        {
            Assert.Null(TestCommandEditorRoute.ResolveCraftPath(
                "/a", "/b", EditorRouteFacility.VAB, "Nope", _ => false));
            Assert.Null(TestCommandEditorRoute.ResolveCraftPath(
                "/a", "/b", EditorRouteFacility.VAB, "Nope", null));
            Assert.Empty(TestCommandEditorRoute.CraftCandidates("/a", "/b", EditorRouteFacility.VAB, null));
        }

        // ---- GoToEditor settle poll ----

        [Fact]
        public void Poll_MainMenu_IsTheFastFailure_EvenBeforeTheBudget()
        {
            Assert.Equal(GoToEditorPollOutcome.ReturnedToMenu, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.MainMenu, false, false, false, false, 100, false));
        }

        [Fact]
        public void Poll_StillAtTheSpaceCenter_WaitsThenTimesOut()
        {
            Assert.Equal(GoToEditorPollOutcome.NotYet, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.SpaceCenter, false, false, false, false, 100, false));
            Assert.Equal(GoToEditorPollOutcome.TimedOut, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.SpaceCenter, false, false, false, false, 100, true));
        }

        [Fact]
        public void Poll_EditorUp_NeedsTheFrameFloor()
        {
            int floor = TestCommandEditorRoute.MinSettleFrames;
            Assert.Equal(GoToEditorPollOutcome.NotYet, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, false, false, false, floor - 1, false));
            Assert.Equal(GoToEditorPollOutcome.Ready, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, false, false, false, floor, false));
        }

        [Fact]
        public void Poll_EditorSceneButNotUp_Waits()
        {
            Assert.Equal(GoToEditorPollOutcome.NotYet, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, false, true, false, false, 50, false));
        }

        [Fact]
        public void Poll_CraftRequested_IssuesTheLoadOnce_ThenWaitsForTheStage()
        {
            int floor = TestCommandEditorRoute.MinSettleFrames;
            Assert.Equal(GoToEditorPollOutcome.IssueCraftLoad, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, true, false, false, floor, false));
            // Issued, not yet on the stage: keep waiting (never a second load).
            Assert.Equal(GoToEditorPollOutcome.NotYet, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, true, true, false, floor + 10, false));
            // On the stage but the phase frame floor not reached after the restart.
            Assert.Equal(GoToEditorPollOutcome.NotYet, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, true, true, true, floor - 1, false));
            Assert.Equal(GoToEditorPollOutcome.Ready, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, true, true, true, floor, false));
        }

        [Fact]
        public void Poll_CraftNeverLands_TimesOut()
        {
            Assert.Equal(GoToEditorPollOutcome.TimedOut, TestCommandEditorRoute.DecideGoToEditorPoll(
                TestCommandScene.Editor, true, true, true, false, 500, true));
        }

        // ---- launch gate ----

        [Fact]
        public void Gate_Proceeds_OnAClearValidSiteWithAShip()
        {
            Assert.Equal(LaunchFromEditorGate.Proceed, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, true, 30, true, true, 0));
            Assert.Null(TestCommandEditorRoute.GateReason(LaunchFromEditorGate.Proceed));
        }

        [Theory]
        [InlineData((int)TestCommandScene.SpaceCenter)]
        [InlineData((int)TestCommandScene.Flight)]
        [InlineData((int)TestCommandScene.MainMenu)]
        public void Gate_WrongScene_WinsOverEverything(int sceneValue)
        {
            var scene = (TestCommandScene)sceneValue;
            Assert.Equal(LaunchFromEditorGate.WrongScene, TestCommandEditorRoute.DecideLaunchGate(
                scene, false, 0, false, false, 5));
        }

        [Fact]
        public void Gate_Order_NoShip_Locked_Site_Obstructed()
        {
            Assert.Equal(LaunchFromEditorGate.NoShip, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, false, 30, false, false, 5));
            Assert.Equal(LaunchFromEditorGate.NoShip, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, true, 0, false, false, 5));
            Assert.Equal(LaunchFromEditorGate.LaunchLocked, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, true, 30, false, false, 5));
            Assert.Equal(LaunchFromEditorGate.SiteInvalid, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, true, 30, true, false, 5));
            Assert.Equal(LaunchFromEditorGate.SiteObstructed, TestCommandEditorRoute.DecideLaunchGate(
                TestCommandScene.Editor, true, 30, true, true, 1));
        }

        [Fact]
        public void GateReasons_AreDistinctAndListed()
        {
            var gates = new[]
            {
                LaunchFromEditorGate.WrongScene, LaunchFromEditorGate.NoShip, LaunchFromEditorGate.LaunchLocked,
                LaunchFromEditorGate.SiteInvalid, LaunchFromEditorGate.SiteObstructed,
            };
            var reasons = gates.Select(TestCommandEditorRoute.GateReason).ToList();
            Assert.Equal(reasons.Count, reasons.Distinct().Count());
            foreach (string r in reasons)
                Assert.Contains(r, TestCommandEditorRoute.Reasons);
            Assert.Equal(TestCommandEditorRoute.Reasons.Length, TestCommandEditorRoute.Reasons.Distinct().Count());
        }

        // ---- launch completion ----

        [Fact]
        public void Completion_FlightWithoutActiveVessel_KeepsWaiting()
        {
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandEditorRoute.DecideLaunchCompletion(
                5, TestCommandScene.Flight, true, false, 180));
            Assert.Equal(LoadCompletionDecision.CompleteOk, TestCommandEditorRoute.DecideLaunchCompletion(
                5, TestCommandScene.Flight, true, true, 180));
        }

        [Fact]
        public void Completion_StillInEditor_WaitsThenTimesOut()
        {
            Assert.Equal(LoadCompletionDecision.StillWaiting, TestCommandEditorRoute.DecideLaunchCompletion(
                10, TestCommandScene.Editor, true, false, 180));
            Assert.Equal(LoadCompletionDecision.LoadTimeout, TestCommandEditorRoute.DecideLaunchCompletion(
                180, TestCommandScene.Editor, true, false, 180));
            Assert.Equal(LoadCompletionDecision.LoadTimeout, TestCommandEditorRoute.DecideLaunchCompletion(
                181, TestCommandScene.Flight, true, false, 180));
        }

        [Fact]
        public void Completion_MainMenu_IsTheFastFailure()
        {
            Assert.Equal(LoadCompletionDecision.LoadFailedMenu, TestCommandEditorRoute.DecideLaunchCompletion(
                1, TestCommandScene.MainMenu, true, false, 180));
        }

        // ---- payloads, log lines, budgets ----

        [Fact]
        public void Lines_AreInvariantAndCarryTheIdentity()
        {
            using (new CultureScope("de-DE"))
            {
                string go = TestCommandEditorRoute.FormatGoCompleteLine(
                    EditorRouteFacility.SPH, "Kerbal X", "EDITOR", 43, "Kerbal X", 12.25);
                Assert.Equal("goeditor complete facility=SPH craft=Kerbal X scene=EDITOR parts=43 ship=Kerbal X elapsed=12.3s", go);
                string launch = TestCommandEditorRoute.FormatLaunchCompleteLine(
                    "Kerbal X", 123456u, "Runway", "PRELAUNCH", 40.04);
                Assert.Equal("launchfromeditor complete scene=FLIGHT vessel=Kerbal X pid=123456 site=Runway situation=PRELAUNCH elapsed=40.0s", launch);
            }
            Assert.Equal("goeditor start facility=VAB craft=- scene=SPACECENTER craftPath=-",
                TestCommandEditorRoute.FormatGoStartLine(EditorRouteFacility.VAB, null, "SPACECENTER", null));
            Assert.Equal("launchfromeditor start ship=Kerbal X parts=43 site=Runway crew=3 scene=EDITOR",
                TestCommandEditorRoute.FormatLaunchStartLine("Kerbal X", 43, "Runway", 3, "EDITOR"));
        }

        [Fact]
        public void Payloads_NameTheSceneFirst()
        {
            var go = TestCommandEditorRoute.BuildGoPayload(EditorRouteFacility.VAB, null, "EDITOR", 0, null);
            Assert.Equal("scene", go[0].Key);
            Assert.Equal("EDITOR", go[0].Value);
            Assert.Contains(go, kv => kv.Key == "craft" && kv.Value == "-");
            var launch = TestCommandEditorRoute.BuildLaunchPayload("Kerbal X", 7u, "Runway", "PRELAUNCH");
            Assert.Equal("scene", launch[0].Key);
            Assert.Equal("FLIGHT", launch[0].Value);
            Assert.Contains(launch, kv => kv.Key == "pid" && kv.Value == "7");
        }

        [Fact]
        public void Budgets_SitInTheSceneChangeClass()
        {
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds("GoToEditor"));
            Assert.Equal(180.0, DeferralBudget.BudgetSeconds("LaunchFromEditor"));
        }

        [Fact]
        public void BothVerbsAreStateMutating()
        {
            Assert.True(TestCommandVerbs.IsStateMutatingVerb("GoToEditor"));
            Assert.True(TestCommandVerbs.IsStateMutatingVerb("LaunchFromEditor"));
        }

        private sealed class CultureScope : System.IDisposable
        {
            private readonly CultureInfo previous;

            internal CultureScope(string name)
            {
                previous = System.Threading.Thread.CurrentThread.CurrentCulture;
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(name);
            }

            public void Dispose() => System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
