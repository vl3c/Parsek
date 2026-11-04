using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure coverage for the <c>PickUpFlag</c> seam verb's decision core: the target choice
    /// (nearest real loaded flag, ghosts and non-flags skipped, the inclusive reach bound),
    /// the gone test, the per-poll press / wait / conclude ladder, the dispatch row and the
    /// payload and log line shapes. No KSP types are touched.
    /// </summary>
    public class TestCommandPickUpFlagTests
    {
        private static ParsedCommand Cmd(string line) => TestCommandParser.ParseLine(line, 1);

        private static FlagPickUpCandidate Flag(uint pid, double metres, bool ghost = false,
            bool loaded = true, bool isFlag = true)
            => new FlagPickUpCandidate
            {
                Pid = pid, Name = "Flag " + pid, DistanceMeters = metres,
                IsFlag = isFlag, IsGhostMapVessel = ghost, Loaded = loaded,
            };

        // ----- ChooseFlag -----

        [Fact]
        public void ChooseFlag_PicksTheNearestRealFlag()
        {
            var list = new List<FlagPickUpCandidate> { Flag(1, 4.0), Flag(2, 1.5), Flag(3, 3.0) };
            int i = TestCommandPickUpFlag.ChooseFlag(list, out double nearest, out string reason);
            Assert.Null(reason);
            Assert.Equal(1, i);
            Assert.Equal(1.5, nearest);
        }

        [Fact]
        public void ChooseFlag_SkipsGhostsUnloadedAndNonFlags()
        {
            var list = new List<FlagPickUpCandidate>
            {
                Flag(1, 0.5, ghost: true),
                Flag(2, 0.6, loaded: false),
                Flag(3, 0.7, isFlag: false),
                Flag(0, 0.8),
                Flag(4, 2.0),
            };
            int i = TestCommandPickUpFlag.ChooseFlag(list, out double nearest, out string reason);
            Assert.Null(reason);
            Assert.Equal(4, i);
            Assert.Equal(2.0, nearest);
        }

        [Fact]
        public void ChooseFlag_NoCandidate_IsNoFlag()
        {
            Assert.Equal(-1, TestCommandPickUpFlag.ChooseFlag(
                new List<FlagPickUpCandidate>(), out _, out string reason));
            Assert.Equal(TestCommandPickUpFlag.NoFlagReason, reason);

            Assert.Equal(-1, TestCommandPickUpFlag.ChooseFlag(null, out _, out reason));
            Assert.Equal("pickupflag-no-flag", reason);

            var onlyGhost = new List<FlagPickUpCandidate> { Flag(9, 1.0, ghost: true) };
            Assert.Equal(-1, TestCommandPickUpFlag.ChooseFlag(onlyGhost, out _, out reason));
            Assert.Equal(TestCommandPickUpFlag.NoFlagReason, reason);
        }

        [Theory]
        [InlineData(5.0, true)]
        [InlineData(5.01, false)]
        [InlineData(0.0, true)]
        public void ChooseFlag_ReachIsInclusive(double metres, bool inReach)
        {
            var list = new List<FlagPickUpCandidate> { Flag(7, metres) };
            int i = TestCommandPickUpFlag.ChooseFlag(list, out _, out string reason);
            if (inReach)
            {
                Assert.Equal(0, i);
                Assert.Null(reason);
            }
            else
            {
                Assert.Equal(-1, i);
                Assert.Equal(TestCommandPickUpFlag.NotNearFlagReason, reason);
            }
        }

        [Fact]
        public void ChooseFlag_NaNDistanceIsNeverChosen()
        {
            var list = new List<FlagPickUpCandidate> { Flag(1, double.NaN) };
            Assert.Equal(-1, TestCommandPickUpFlag.ChooseFlag(list, out _, out string reason));
            Assert.Equal(TestCommandPickUpFlag.NoFlagReason, reason);
        }

        // ----- IsFlagGone -----

        [Theory]
        [InlineData(true, false, true, false)]
        [InlineData(false, false, false, true)]
        [InlineData(true, true, true, true)]
        [InlineData(true, false, false, true)]
        public void IsFlagGone_DestroyedDeadOrUnlisted(bool alive, bool dead, bool listed, bool expected)
        {
            Assert.Equal(expected, TestCommandPickUpFlag.IsFlagGone(alive, dead, listed));
        }

        // ----- Decide -----

        private static FlagPickUpDecision D(bool gone, int fires, int sinceFire, bool ragdoll,
            bool settled = true, double elapsed = 1.0, double budget = 60.0)
            => TestCommandPickUpFlag.Decide(gone, fires, sinceFire, ragdoll, settled, elapsed, budget);

        [Fact]
        public void Decide_FirstPollPresses()
        {
            Assert.Equal(FlagPickUpDecision.FirePickUp, D(false, 0, 0, false));
        }

        [Fact]
        public void Decide_RagdollWaitsBeforeAnyPress()
        {
            // Stock's PickUp returns silently for a ragdolled kerbal, so a press would be wasted.
            Assert.Equal(FlagPickUpDecision.KeepWaiting, D(false, 0, 0, true));
            Assert.Equal(FlagPickUpDecision.KeepWaiting, D(false, 1, 100, true));
        }

        [Fact]
        public void Decide_GoneAfterPress_IsOkOnceSettled()
        {
            Assert.Equal(FlagPickUpDecision.CompleteOk, D(true, 1, 1, false));
            Assert.Equal(FlagPickUpDecision.KeepWaiting, D(true, 1, 1, false, settled: false));
            Assert.Equal(FlagPickUpDecision.Timeout, D(true, 1, 1, false, settled: false, elapsed: 60.0));
        }

        [Fact]
        public void Decide_GoneBeforeAnyPress_IsFlagLost()
        {
            Assert.Equal(FlagPickUpDecision.FlagLost, D(true, 0, 0, false));
            Assert.Equal(FlagPickUpDecision.FlagLost, D(true, 0, 0, true, elapsed: 99.0));
        }

        [Fact]
        public void Decide_RepressesAfterTheRefireWindow_UpToTheCap()
        {
            int window = TestCommandPickUpFlag.RefirePolls;
            Assert.Equal(FlagPickUpDecision.KeepWaiting, D(false, 1, window - 1, false));
            Assert.Equal(FlagPickUpDecision.FirePickUp, D(false, 1, window, false));
            Assert.Equal(FlagPickUpDecision.FirePickUp, D(false, TestCommandPickUpFlag.MaxFires - 1, window, false));
            Assert.Equal(FlagPickUpDecision.KeepWaiting, D(false, TestCommandPickUpFlag.MaxFires, window * 10, false));
        }

        [Fact]
        public void Decide_StandingFlagAtBudget_TimesOut()
        {
            Assert.Equal(FlagPickUpDecision.Timeout, D(false, 0, 0, false, elapsed: 60.0));
            Assert.Equal(FlagPickUpDecision.Timeout, D(false, 3, 5, true, elapsed: 61.0));
        }

        // ----- Vocabulary, dispatch, payload -----

        [Fact]
        public void Vocabulary_IsTheNamedConstants()
        {
            Assert.Equal(new[] { TestCommandPickUpFlag.NotEvaReason, TestCommandPickUpFlag.NoFlagReason,
                TestCommandPickUpFlag.NotNearFlagReason }, TestCommandPickUpFlag.Reasons);
            Assert.Equal(new[] { TestCommandPickUpFlag.ThrewReason, TestCommandPickUpFlag.FlagLostReason,
                TestCommandPickUpFlag.TimeoutReason }, TestCommandPickUpFlag.ErrorReasons);
        }

        [Fact]
        public void PickUpFlag_IsImplementedFlightOnlyOnTheDefaultBudget()
        {
            Assert.Equal(TestCommandVerbClass.Implemented, TestCommandVerbs.Classify("PickUpFlag"));
            Assert.Equal(VerbSceneRequirement.RequiresFlight, TestCommandDispatcher.RequirementFor("PickUpFlag"));
            Assert.Equal(DeferralBudget.DefaultSeconds, DeferralBudget.BudgetSeconds("PickUpFlag"));
            Assert.True(TestCommandActiveVesselLoss.WatchesActiveVessel("PickUpFlag"));
        }

        [Fact]
        public void Dispatch_NotOnEva_DefersNotEva()
        {
            var state = new DispatchState
            {
                Scene = TestCommandScene.Flight,
                GameLoaded = true,
                SettingsPresent = true,
                FlightEvaPresent = true,
                ActiveVesselIsEva = false,
            };
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=PickUpFlag"), state);
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-eva", r.Reason);

            state.ActiveVesselIsEva = true;
            Assert.Equal(DispatchDecision.Execute,
                TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=PickUpFlag"), state).Decision);
        }

        [Fact]
        public void Payload_AndLines_AreInvariant()
        {
            var payload = TestCommandPickUpFlag.BuildOkPayload("Jeb's flag", 4242u, "Jebediah Kerman", 1.256, 2);
            Assert.Equal("flag", payload[0].Key);
            Assert.Equal("Jeb's flag", payload[0].Value);
            Assert.Equal("4242", payload[1].Value);
            Assert.Equal("Jebediah Kerman", payload[2].Value);
            Assert.Equal("1.26", payload[3].Value);
            Assert.Equal("2", payload[4].Value);

            Assert.Equal("pickupflag start kerbal='Jeb' flag='F' flagPid=7 dist=0.50",
                TestCommandPickUpFlag.FormatStartLine("Jeb", "F", 7u, 0.5));
            Assert.Equal("pickupflag complete kerbal='Jeb' flag='F' flagPid=7 fires=1",
                TestCommandPickUpFlag.FormatCompleteLine("Jeb", "F", 7u, 1));
        }
    }
}
