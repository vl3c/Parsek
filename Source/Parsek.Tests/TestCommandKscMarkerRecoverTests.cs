using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure half and dispatch rows of <c>KscMarkerRecover pid=</c>: recover a landed
    /// vessel on the home world through its stock Space Center marker.
    /// </summary>
    public class TestCommandKscMarkerRecoverTests
    {
        private const string Verb = "KscMarkerRecover";

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

        // DecidePoll with every input named; defaults describe a settled Space Center whose
        // marker is found, wired, unlocked and live, nothing recovered yet.
        private static KscRecoverPollAction Poll(
            KscRecoverPhase phase,
            TestCommandScene scene = TestCommandScene.SpaceCenter,
            bool gameLoaded = true, int frames = 10,
            bool found = true, bool wired = true, bool locked = false, bool live = true,
            bool recovered = false, int sinceRecovered = 0, bool summary = false,
            bool expired = false)
            => TestCommandKscMarkerRecover.DecidePoll(
                phase, scene, gameLoaded, frames, found, wired, locked, live,
                recovered, sinceRecovered, summary, expired);

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
        public void Budget_Is60Seconds()
        {
            Assert.Equal(60.0, DeferralBudget.BudgetSeconds(Verb));
            Assert.Equal(TestCommandKscMarkerRecover.Verb, Verb);
            Assert.Equal(VerbSceneRequirement.RequiresGameLoaded, TestCommandDispatcher.RequirementFor(Verb));
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify(Verb));
        }

        // ----- arg -----

        [Theory]
        [InlineData("1", 1u)]
        [InlineData("1344998135", 1344998135u)]
        [InlineData("4294967295", 4294967295u)]
        public void ParsePid_Accepts(string raw, uint expected)
        {
            Assert.Null(TestCommandKscMarkerRecover.ParsePid(raw, out uint pid));
            Assert.Equal(expected, pid);
        }

        [Theory]
        [InlineData(null, "kscrecover-pid-arg-missing")]
        [InlineData("", "kscrecover-pid-arg-missing")]
        [InlineData("0", "kscrecover-pid-arg-invalid")]
        [InlineData("-5", "kscrecover-pid-arg-invalid")]
        [InlineData("+5", "kscrecover-pid-arg-invalid")]
        [InlineData(" 5", "kscrecover-pid-arg-invalid")]
        [InlineData("1.0", "kscrecover-pid-arg-invalid")]
        [InlineData("4294967296", "kscrecover-pid-arg-invalid")]
        [InlineData("\u0665", "kscrecover-pid-arg-invalid")]
        [InlineData("${spawn.pid}", "kscrecover-pid-arg-invalid")]
        public void ParsePid_Refuses(string raw, string reason)
        {
            Assert.Equal(reason, TestCommandKscMarkerRecover.ParsePid(raw, out uint pid));
            Assert.Equal(0u, pid);
        }

        // ----- gate -----

        [Theory]
        // landedOrSplashed, onHome, deployedScience, droppedPart -> stock gives it a marker
        [InlineData(true, true, false, false, true)]
        [InlineData(false, true, false, false, false)]
        [InlineData(true, false, false, false, false)]
        [InlineData(true, true, true, false, false)]
        [InlineData(true, true, false, true, false)]
        public void HasStockMarker_MirrorsSpawnVesselMarkers(
            bool landed, bool home, bool science, bool dropped, bool expected)
        {
            Assert.Equal(expected, TestCommandKscMarkerRecover.HasStockMarker(landed, home, science, dropped));
        }

        [Theory]
        // scene, found, ghost, hasMarker -> reason
        [InlineData("Flight", true, false, true, "kscrecover-wrong-scene")]
        [InlineData("TrackingStation", true, false, true, "kscrecover-wrong-scene")]
        [InlineData("Editor", true, false, true, "kscrecover-wrong-scene")]
        [InlineData("SpaceCenter", false, true, false, "kscrecover-target-is-ghost")]
        [InlineData("SpaceCenter", false, false, false, "kscrecover-vessel-not-found")]
        [InlineData("SpaceCenter", true, false, false, "kscrecover-not-recoverable")]
        [InlineData("SpaceCenter", true, false, true, null)]
        public void Gate(string sceneName, bool found, bool ghost, bool hasMarker, string expected)
        {
            var scene = (TestCommandScene)System.Enum.Parse(typeof(TestCommandScene), sceneName);
            Assert.Equal(expected, TestCommandKscMarkerRecover.DecideGate(scene, found, ghost, hasMarker));
        }

        [Fact]
        public void EveryReasonIsInTheTable()
        {
            string[] reasons =
            {
                TestCommandKscMarkerRecover.PidArgMissingReason,
                TestCommandKscMarkerRecover.PidArgInvalidReason,
                TestCommandKscMarkerRecover.WrongSceneReason,
                TestCommandKscMarkerRecover.VesselNotFoundReason,
                TestCommandKscMarkerRecover.TargetIsGhostReason,
                TestCommandKscMarkerRecover.NotRecoverableReason,
                TestCommandKscMarkerRecover.MarkerNotFoundReason,
                TestCommandKscMarkerRecover.ButtonLockedReason,
            };
            Assert.Equal(reasons, TestCommandKscMarkerRecover.Reasons);
            string[] errors =
            {
                TestCommandKscMarkerRecover.NotRecoveredReason,
                TestCommandKscMarkerRecover.ReturnedToMenuReason,
                TestCommandKscMarkerRecover.TimeoutReason,
            };
            Assert.Equal(errors, TestCommandKscMarkerRecover.ErrorReasons);
            var all = reasons.Concat(errors).ToList();
            Assert.All(all, r => Assert.StartsWith("kscrecover-", r));
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        // ----- completion walk -----

        [Fact]
        public void FindingMarker_WaitsForAWiredMarkerThenRefuses()
        {
            int bound = TestCommandKscMarkerRecover.MarkerWaitFrames;
            Assert.Equal(KscRecoverPollAction.NotYet,
                Poll(KscRecoverPhase.FindingMarker, frames: bound - 1, found: false));
            Assert.Equal(KscRecoverPollAction.NotYet,
                Poll(KscRecoverPhase.FindingMarker, frames: bound - 1, wired: false));
            Assert.Equal(KscRecoverPollAction.NotYet,
                Poll(KscRecoverPhase.FindingMarker, TestCommandScene.Loading, frames: bound - 1));
            Assert.Equal(KscRecoverPollAction.MarkerNotFound,
                Poll(KscRecoverPhase.FindingMarker, frames: bound, found: false));
            Assert.Equal(KscRecoverPollAction.MarkerNotFound,
                Poll(KscRecoverPhase.FindingMarker, frames: bound, wired: false));
            Assert.Equal(KscRecoverPollAction.MarkerNotFound,
                Poll(KscRecoverPhase.FindingMarker, gameLoaded: false, frames: bound));
        }

        [Fact]
        public void FindingMarker_PressesOnlyALiveUnlockedMarker()
        {
            Assert.Equal(KscRecoverPollAction.PressRecover, Poll(KscRecoverPhase.FindingMarker, frames: 0));
            // Button.onClick.Invoke ignores interactable and the marker's KSC_UI lock, so a
            // refused press is a typed refusal, never a click.
            Assert.Equal(KscRecoverPollAction.ButtonLocked, Poll(KscRecoverPhase.FindingMarker, live: false));
            Assert.Equal(KscRecoverPollAction.ButtonLocked, Poll(KscRecoverPhase.FindingMarker, locked: true));
            Assert.Equal(KscRecoverPollAction.Timeout, Poll(KscRecoverPhase.FindingMarker, expired: true));
        }

        [Fact]
        public void Recovering_WaitsForTheEventThenGivesUp()
        {
            int bound = TestCommandKscMarkerRecover.RecoverWaitFrames;
            Assert.Equal(KscRecoverPollAction.NotYet, Poll(KscRecoverPhase.Recovering, frames: 1));
            Assert.Equal(KscRecoverPollAction.NotYet, Poll(KscRecoverPhase.Recovering, frames: bound - 1));
            Assert.Equal(KscRecoverPollAction.NotRecovered, Poll(KscRecoverPhase.Recovering, frames: bound));
            // The budget ends the wait as not-recovered when the event never came.
            Assert.Equal(KscRecoverPollAction.NotRecovered,
                Poll(KscRecoverPhase.Recovering, frames: 1, expired: true));
        }

        [Fact]
        public void Recovering_ClosesTheSummaryBeforeOk()
        {
            int settle = TestCommandKscMarkerRecover.SettleFrames;
            Assert.Equal(KscRecoverPollAction.DismissSummary,
                Poll(KscRecoverPhase.Recovering, recovered: true, sinceRecovered: settle, summary: true));
            Assert.Equal(KscRecoverPollAction.NotYet,
                Poll(KscRecoverPhase.Recovering, recovered: true, sinceRecovered: settle - 1));
            Assert.Equal(KscRecoverPollAction.Ok,
                Poll(KscRecoverPhase.Recovering, recovered: true, sinceRecovered: settle));
            // Recovered but the scene left the Space Center: wait for the budget.
            Assert.Equal(KscRecoverPollAction.NotYet,
                Poll(KscRecoverPhase.Recovering, TestCommandScene.Loading, recovered: true, sinceRecovered: settle));
            Assert.Equal(KscRecoverPollAction.Timeout,
                Poll(KscRecoverPhase.Recovering, TestCommandScene.Loading, recovered: true,
                    sinceRecovered: settle, expired: true));
        }

        [Theory]
        [InlineData("FindingMarker")]
        [InlineData("Recovering")]
        public void MenuIsFastInEveryPhase(string phaseName)
        {
            var phase = (KscRecoverPhase)System.Enum.Parse(typeof(KscRecoverPhase), phaseName);
            Assert.Equal(KscRecoverPollAction.ReturnedToMenu,
                Poll(phase, TestCommandScene.MainMenu, recovered: true, sinceRecovered: 9, expired: true));
        }

        // ----- payload and lines -----

        [Fact]
        public void OkPayloadAndLines_AreInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                var p = TestCommandKscMarkerRecover.BuildOkPayload(77u, "CTR Lander", "SPACECENTER", false);
                Assert.Equal(new[] { "pid", "vessel", "scene", "recovered", "quick" },
                    p.Select(kv => kv.Key).ToArray());
                Assert.Equal("77", Value(p, "pid"));
                Assert.Equal("CTR Lander", Value(p, "vessel"));
                Assert.Equal("SPACECENTER", Value(p, "scene"));
                Assert.Equal("true", Value(p, "recovered"));
                Assert.Equal("false", Value(p, "quick"));
                Assert.Equal("true", Value(TestCommandKscMarkerRecover.BuildOkPayload(77u, "P", "S", true), "quick"));
                Assert.Equal(
                    "kscrecover pressed pid=77 vessel=CTR Lander situation=LANDED markers=3 framesWaited=16 ut=1234.5",
                    TestCommandKscMarkerRecover.FormatPressedLine(77u, "CTR Lander", "LANDED", 3, 16, 1234.5));
                Assert.Equal("kscrecover observed onVesselRecovered pid=77 vessel=CTR Lander quick=false",
                    TestCommandKscMarkerRecover.FormatObservedLine(77u, "CTR Lander", false));
                Assert.Equal("kscrecover complete pid=77 vessel=CTR Lander scene=SPACECENTER quick=false elapsed=3.2s",
                    TestCommandKscMarkerRecover.FormatCompleteLine(77u, "CTR Lander", "SPACECENTER", false, 3.2));
                Assert.Equal("kscrecover rejected reason=kscrecover-button-locked pid=77 locked=true elapsed=0.5s",
                    TestCommandKscMarkerRecover.FormatTerminalLine(
                        true, "kscrecover-button-locked", 77u, "locked=true", 0.5));
                Assert.Equal("kscrecover error reason=kscrecover-timeout pid=77 elapsed=60.0s",
                    TestCommandKscMarkerRecover.FormatTerminalLine(false, "kscrecover-timeout", 77u, null, 60.0));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }
    }
}
