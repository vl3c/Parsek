using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>TrackingStationRecover pid=</c>: recover a
    /// vessel through the stock Tracking Station path, issued at the Space Center.
    /// </summary>
    public class TestCommandTrackingStationRecoverTests
    {
        private const string Verb = "TrackingStationRecover";

        private static DispatchState At(TestCommandScene scene) => new DispatchState
        {
            Scene = scene,
            GameLoaded = true,
            SettingsPresent = true,
        };

        private static ParsedCommand Cmd(string args)
            => TestCommandParser.ParseLine("id=1 cmd=" + Verb + " " + args, 1);

        private static string Value(List<KeyValuePair<string, string>> payload, string key)
            => payload.Single(kv => kv.Key == key).Value;

        // DecidePoll with every input named; defaults describe a settled TS, nothing selected.
        private static TsRecoverPollAction Poll(
            TsRecoverPhase phase,
            TestCommandScene scene = TestCommandScene.TrackingStation,
            bool gameLoaded = true, bool tsUp = true, int frames = 100,
            bool selected = false, bool recoverLive = false, bool popup = false,
            bool summary = false, bool leaveLive = false, bool expired = false)
            => TestCommandTrackingStationRecover.DecidePoll(
                phase, scene, gameLoaded, tsUp, frames, selected, recoverLive, popup,
                summary, leaveLive, expired);

        // ----- dispatch -----

        [Fact]
        public void Dispatch_AtTheSpaceCenter_Executes()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), At(TestCommandScene.SpaceCenter));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_InFlight_ExecutesSoTheVerbRefusesTyped()
        {
            // RequiresGameLoaded: the wrong scene is the executor's typed REJECTED, never a
            // defer that would ride the whole budget.
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), At(TestCommandScene.Flight));
            Assert.Equal(DispatchDecision.Execute, r.Decision);
        }

        [Fact]
        public void Dispatch_LoadInFlight_RejectsAheadOfMergeJournal()
        {
            var st = At(TestCommandScene.SpaceCenter);
            st.LoadInFlight = true;
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("load-in-flight", r.Reason);
        }

        [Fact]
        public void Dispatch_MergeJournalInFlight_Rejects()
        {
            var st = At(TestCommandScene.SpaceCenter);
            st.MergeJournalInFlight = true;
            var r = TestCommandDispatcher.DecideDispatch(Cmd("pid=42"), st);
            Assert.Equal(DispatchDecision.Reject, r.Decision);
            Assert.Equal("merge-journal-in-flight", r.Reason);
        }

        [Fact]
        public void Budget_Is120Seconds()
        {
            Assert.Equal(120.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.Equal(TestCommandTrackingStationRecover.Verb, Verb);
        }

        // ----- arg -----

        [Theory]
        [InlineData("1", 1u)]
        [InlineData("4294967295", 4294967295u)]
        public void ParsePid_Accepts(string raw, uint expected)
        {
            Assert.Null(TestCommandTrackingStationRecover.ParsePid(raw, out uint pid));
            Assert.Equal(expected, pid);
        }

        [Theory]
        [InlineData(null, "tsrecover-pid-arg-missing")]
        [InlineData("", "tsrecover-pid-arg-missing")]
        [InlineData("0", "tsrecover-pid-arg-invalid")]
        [InlineData("-5", "tsrecover-pid-arg-invalid")]
        [InlineData("+5", "tsrecover-pid-arg-invalid")]
        [InlineData(" 5", "tsrecover-pid-arg-invalid")]
        [InlineData("1.0", "tsrecover-pid-arg-invalid")]
        [InlineData("4294967296", "tsrecover-pid-arg-invalid")]
        [InlineData("\u0665", "tsrecover-pid-arg-invalid")]
        [InlineData("${spawn.pid}", "tsrecover-pid-arg-invalid")]
        public void ParsePid_Refuses(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandTrackingStationRecover.ParsePid(raw, out uint pid));
            Assert.Equal(0u, pid);
        }

        // ----- gate -----

        [Theory]
        // scene, found, ghost, recoverable, building, open, canGoIn, damage -> reason
        [InlineData("Flight", true, false, true, true, true, true, 0f, "tsrecover-wrong-scene")]
        [InlineData("TrackingStation", true, false, true, true, true, true, 0f, "tsrecover-wrong-scene")]
        [InlineData("SpaceCenter", false, true, false, true, true, true, 0f, "tsrecover-target-is-ghost")]
        [InlineData("SpaceCenter", false, false, false, true, true, true, 0f, "tsrecover-vessel-not-found")]
        [InlineData("SpaceCenter", true, false, false, true, true, true, 0f, "tsrecover-not-recoverable")]
        [InlineData("SpaceCenter", true, false, true, false, false, true, 0f, "tsrecover-building-not-found")]
        [InlineData("SpaceCenter", true, false, true, true, false, true, 0f, "tsrecover-facility-closed")]
        [InlineData("SpaceCenter", true, false, true, true, true, false, 0f, "tsrecover-facility-closed")]
        [InlineData("SpaceCenter", true, false, true, true, true, true, 70f, "tsrecover-facility-closed")]
        [InlineData("SpaceCenter", true, false, true, true, true, true, 69.9f, null)]
        public void Gate(string sceneName, bool found, bool ghost, bool recoverable,
            bool building, bool open, bool canGoIn, float damage, string expected)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandTrackingStationRecover.DecideGate(
                scene, found, ghost, recoverable, building, open, canGoIn, damage));
        }

        [Fact]
        public void EveryGateReasonIsInTheTable()
        {
            string[] reasons =
            {
                TestCommandTrackingStationRecover.PidArgMissingReason,
                TestCommandTrackingStationRecover.PidArgInvalidReason,
                TestCommandTrackingStationRecover.WrongSceneReason,
                TestCommandTrackingStationRecover.VesselNotFoundReason,
                TestCommandTrackingStationRecover.TargetIsGhostReason,
                TestCommandTrackingStationRecover.NotRecoverableReason,
                TestCommandTrackingStationRecover.BuildingNotFoundReason,
                TestCommandTrackingStationRecover.FacilityClosedReason,
                TestCommandTrackingStationRecover.ButtonLockedReason,
            };
            Assert.Equal(reasons, TestCommandTrackingStationRecover.Reasons);
            string[] errors =
            {
                TestCommandTrackingStationRecover.SelectFailedReason,
                TestCommandTrackingStationRecover.ConfirmNotFoundReason,
                TestCommandTrackingStationRecover.NotRecoveredReason,
                TestCommandTrackingStationRecover.ReturnedToMenuReason,
                TestCommandTrackingStationRecover.TimeoutReason,
            };
            Assert.Equal(errors, TestCommandTrackingStationRecover.ErrorReasons);
        }

        [Fact]
        public void ReasonsArePrefixedAndDistinct()
        {
            var all = TestCommandTrackingStationRecover.Reasons
                .Concat(TestCommandTrackingStationRecover.ErrorReasons).ToList();
            Assert.All(all, r => Assert.StartsWith("tsrecover-", r));
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        // ----- completion walk -----

        [Fact]
        public void Entering_WaitsForASettledTrackingStation()
        {
            // The click is made from the Space Center, which is still the loaded scene.
            Assert.Equal(TsRecoverPollAction.NotYet,
                Poll(TsRecoverPhase.EnteringTrackingStation, TestCommandScene.SpaceCenter, tsUp: false));
            Assert.Equal(TsRecoverPollAction.NotYet,
                Poll(TsRecoverPhase.EnteringTrackingStation, TestCommandScene.Loading, tsUp: false));
            Assert.Equal(TsRecoverPollAction.NotYet,
                Poll(TsRecoverPhase.EnteringTrackingStation, tsUp: false));
            Assert.Equal(TsRecoverPollAction.NotYet,
                Poll(TsRecoverPhase.EnteringTrackingStation,
                    frames: TestCommandTrackingStationRecover.TrackingStationSettleFrames - 1));
            Assert.Equal(TsRecoverPollAction.SelectVessel,
                Poll(TsRecoverPhase.EnteringTrackingStation,
                    frames: TestCommandTrackingStationRecover.TrackingStationSettleFrames));
        }

        [Fact]
        public void Selecting_RetriesThenGivesUp()
        {
            Assert.Equal(TsRecoverPollAction.SelectVessel, Poll(TsRecoverPhase.Selecting, frames: 1));
            Assert.Equal(TsRecoverPollAction.SelectFailed,
                Poll(TsRecoverPhase.Selecting, frames: TestCommandTrackingStationRecover.SelectRetryFrames));
        }

        [Fact]
        public void Selecting_PressesOnlyALiveButton()
        {
            Assert.Equal(TsRecoverPollAction.PressRecover,
                Poll(TsRecoverPhase.Selecting, frames: 1, selected: true, recoverLive: true));
            // A selected vessel whose Recover stock locked is a refusal, never a click
            // (Button.onClick.Invoke ignores interactable).
            Assert.Equal(TsRecoverPollAction.ButtonLocked,
                Poll(TsRecoverPhase.Selecting, frames: 1, selected: true, recoverLive: false));
        }

        [Fact]
        public void Confirming_AnswersThePopupOrGivesUp()
        {
            Assert.Equal(TsRecoverPollAction.NotYet, Poll(TsRecoverPhase.Confirming, frames: 0));
            Assert.Equal(TsRecoverPollAction.AnswerConfirm, Poll(TsRecoverPhase.Confirming, frames: 0, popup: true));
            Assert.Equal(TsRecoverPollAction.ConfirmNotFound,
                Poll(TsRecoverPhase.Confirming, frames: TestCommandTrackingStationRecover.ConfirmWaitFrames));
        }

        [Fact]
        public void Recovered_ClosesTheSummaryBeforeLeaving()
        {
            // Stock keeps Leave locked while the MissionRecoveryDialog is up.
            Assert.Equal(TsRecoverPollAction.DismissSummary,
                Poll(TsRecoverPhase.Recovered, summary: true, leaveLive: true));
            Assert.Equal(TsRecoverPollAction.NotYet, Poll(TsRecoverPhase.Recovered, leaveLive: false));
            Assert.Equal(TsRecoverPollAction.PressLeave, Poll(TsRecoverPhase.Recovered, leaveLive: true));
        }

        [Fact]
        public void Leaving_OkOnlyAtASettledSpaceCenter()
        {
            Assert.Equal(TsRecoverPollAction.NotYet, Poll(TsRecoverPhase.Leaving));
            Assert.Equal(TsRecoverPollAction.NotYet, Poll(TsRecoverPhase.Leaving, TestCommandScene.Loading));
            Assert.Equal(TsRecoverPollAction.NotYet,
                Poll(TsRecoverPhase.Leaving, TestCommandScene.SpaceCenter, gameLoaded: false));
            Assert.Equal(TsRecoverPollAction.Ok, Poll(TsRecoverPhase.Leaving, TestCommandScene.SpaceCenter));
        }

        [Theory]
        [InlineData("EnteringTrackingStation")]
        [InlineData("Selecting")]
        [InlineData("Confirming")]
        [InlineData("Recovered")]
        [InlineData("Leaving")]
        public void MenuIsFastAndBudgetIsTheCatchAll(string phaseName)
        {
            var phase = (TsRecoverPhase)System.Enum.Parse(typeof(TsRecoverPhase), phaseName);
            Assert.Equal(TsRecoverPollAction.ReturnedToMenu,
                Poll(phase, TestCommandScene.MainMenu, expired: true));
            Assert.Equal(TsRecoverPollAction.Timeout, Poll(phase, expired: true, popup: true, selected: true,
                recoverLive: true, leaveLive: true));
        }

        // ----- payload and lines -----

        [Fact]
        public void OkPayloadAndLines_AreInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandTrackingStationRecover.BuildOkPayload(77u, "Pod", "SPACECENTER", false);
                Assert.Equal(new[] { "pid", "vessel", "scene", "recovered", "quick" },
                    p.Select(kv => kv.Key).ToArray());
                Assert.Equal("77", Value(p, "pid"));
                Assert.Equal("SPACECENTER", Value(p, "scene"));
                Assert.Equal("true", Value(p, "recovered"));
                Assert.Equal("false", Value(p, "quick"));
                Assert.Equal("tsrecover enter pid=77 vessel=Pod situation=LANDED ut=1234.5",
                    TestCommandTrackingStationRecover.FormatEnterLine(77u, "Pod", "LANDED", 1234.5));
                Assert.Equal("tsrecover pressed pid=77 vessel=Pod framesInTs=16",
                    TestCommandTrackingStationRecover.FormatPressedLine(77u, "Pod", 16));
                Assert.Equal("tsrecover complete pid=77 vessel=Pod scene=SPACECENTER quick=false elapsed=3.2s",
                    TestCommandTrackingStationRecover.FormatCompleteLine(77u, "Pod", "SPACECENTER", false, 3.2));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void StockNames_AreTheDecompiledOnes()
        {
            // SpaceTracking.BtnOnclick_RecoverSelectedVessel spawns
            // MultiOptionDialog("Recover Vessel", ...) whose confirm button's callback is
            // OnRecoverConfirm (KSP 1.12.5).
            Assert.Equal("Recover Vessel", TestCommandTrackingStationRecover.ConfirmDialogName);
            Assert.Equal("OnRecoverConfirm", TestCommandTrackingStationRecover.ConfirmCallbackMethodName);
            Assert.Equal(70f, TestCommandTrackingStationRecover.BuildingDamageThreshold);
        }
    }
}
