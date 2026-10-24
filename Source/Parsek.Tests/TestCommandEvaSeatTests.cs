using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The external command seat seam pair (<c>EvaSeatBoard</c> / <c>EvaSeatLeave</c>): the
    /// pure gates, the held-poll completion that outlasts Parsek's dock-merge confirmation
    /// window, the payload and line shapes the SEAT-1 lane pins, and the dispatch defers.
    /// </summary>
    public class TestCommandEvaSeatTests
    {
        private static ParsedCommand Cmd(string line) => TestCommandParser.ParseLine(line, 1);

        private static DispatchState Flight(bool onEva, bool splitPending) => new DispatchState
        {
            Scene = TestCommandScene.Flight,
            GameLoaded = true,
            SettingsPresent = true,
            FlightEvaPresent = true,
            ActiveVesselIsEva = onEva,
            StructuralSplitPending = splitPending,
        };

        // ----- Board gate -----

        [Fact]
        public void BoardGate_NotEva_RefusesFirst()
        {
            Assert.Equal("evaseatboard-not-eva", TestCommandEvaSeat.DecideBoardGate(false, false, double.MaxValue));
        }

        [Fact]
        public void BoardGate_NoFreeSeat_Refuses()
        {
            Assert.Equal("evaseatboard-no-free-seat", TestCommandEvaSeat.DecideBoardGate(true, false, double.MaxValue));
        }

        [Theory]
        [InlineData(0.5, null)]
        [InlineData(10.0, null)]
        [InlineData(10.01, "evaseatboard-not-near-seat")]
        [InlineData(double.NaN, "evaseatboard-not-near-seat")]
        public void BoardGate_RangeIsInclusiveAndNaNRefuses(double metres, string expected)
        {
            Assert.Equal(expected, TestCommandEvaSeat.DecideBoardGate(true, true, metres));
        }

        // ----- Leave gate -----

        [Theory]
        [InlineData(false, 1, false, "evaseatleave-no-active-vessel")]
        [InlineData(true, 0, false, "evaseatleave-not-seated")]
        [InlineData(true, 2, false, "evaseatleave-seated-ambiguous")]
        [InlineData(true, 1, true, "evaseatleave-tourist")]
        [InlineData(true, 1, false, null)]
        public void LeaveGate_Order(bool hasVessel, int seated, bool tourist, string expected)
        {
            Assert.Equal(expected, TestCommandEvaSeat.DecideLeaveGate(hasVessel, seated, tourist));
        }

        // ----- Conjuncts -----

        [Fact]
        public void BoardConjuncts_AllRequired()
        {
            Assert.True(TestCommandEvaSeat.BoardConjunctsHold(true, true, true, true, true, true));
            for (int i = 0; i < 6; i++)
            {
                bool[] b = Enumerable.Repeat(true, 6).ToArray();
                b[i] = false;
                Assert.False(TestCommandEvaSeat.BoardConjunctsHold(b[0], b[1], b[2], b[3], b[4], b[5]));
            }
        }

        [Fact]
        public void LeaveConjuncts_AllRequired()
        {
            Assert.True(TestCommandEvaSeat.LeaveConjunctsHold(true, true, true, true, true));
            for (int i = 0; i < 5; i++)
            {
                bool[] b = Enumerable.Repeat(true, 5).ToArray();
                b[i] = false;
                Assert.False(TestCommandEvaSeat.LeaveConjunctsHold(b[0], b[1], b[2], b[3], b[4]));
            }
        }

        // ----- Held-poll completion -----

        [Fact]
        public void HeldPolls_ResetOnAnyMiss()
        {
            Assert.Equal(1, TestCommandEvaSeat.NextHeldPolls(true, 0));
            Assert.Equal(7, TestCommandEvaSeat.NextHeldPolls(true, 6));
            Assert.Equal(0, TestCommandEvaSeat.NextHeldPolls(false, 9));
        }

        [Fact]
        public void Completion_NeedsTheFullHold_ThenOk()
        {
            int held = 0;
            for (int i = 0; i < TestCommandEvaSeat.HeldPollsRequired - 1; i++)
            {
                held = TestCommandEvaSeat.NextHeldPolls(true, held);
                Assert.Equal(EvaSeatCompletionDecision.StillWaiting,
                    TestCommandEvaSeat.DecideCompletion(held, 1.0, 60.0));
            }
            held = TestCommandEvaSeat.NextHeldPolls(true, held);
            Assert.Equal(EvaSeatCompletionDecision.CompleteOk, TestCommandEvaSeat.DecideCompletion(held, 1.0, 60.0));
        }

        [Fact]
        public void Completion_HoldOutlastsTheDockConfirmationWindow()
        {
            // ParsekFlight clears a pending dock confirmation after 5 frames.
            Assert.True(TestCommandEvaSeat.HeldPollsRequired > 5);
        }

        [Fact]
        public void Completion_BudgetExpiry_TimesOut_PositiveFirst()
        {
            Assert.Equal(EvaSeatCompletionDecision.Timeout, TestCommandEvaSeat.DecideCompletion(3, 60.0, 60.0));
            Assert.Equal(EvaSeatCompletionDecision.CompleteOk,
                TestCommandEvaSeat.DecideCompletion(TestCommandEvaSeat.HeldPollsRequired, 61.0, 60.0));
        }

        // ----- Wire shapes -----

        [Fact]
        public void BoardPayloadAndLines_AreStable()
        {
            var payload = TestCommandEvaSeat.BuildBoardOkPayload("Jebediah Kerman", 42u, "seatExternalCmd", 7u);
            Assert.Equal(new[] { "kerbal", "vesselPid", "seatPart", "seatId" }, payload.Select(p => p.Key).ToArray());
            Assert.Equal(new[] { "Jebediah Kerman", "42", "seatExternalCmd", "7" }, payload.Select(p => p.Value).ToArray());
            Assert.Equal("evaseatboard start kerbal='Jebediah Kerman' targetPid=42 seatPart=seatExternalCmd seatId=7 dist=1.50",
                TestCommandEvaSeat.FormatBoardStartLine("Jebediah Kerman", 42u, "seatExternalCmd", 7u, 1.5));
            Assert.Equal("evaseatboard complete kerbal='Jebediah Kerman' vesselPid=42 seatPart=seatExternalCmd seatId=7",
                TestCommandEvaSeat.FormatBoardCompleteLine("Jebediah Kerman", 42u, "seatExternalCmd", 7u));
        }

        [Fact]
        public void LeavePayloadAndLines_AreStable()
        {
            var payload = TestCommandEvaSeat.BuildLeaveOkPayload("Jebediah Kerman", 9u, 42u);
            Assert.Equal(new[] { "kerbal", "kerbalPid", "vehiclePid" }, payload.Select(p => p.Key).ToArray());
            Assert.Equal(new[] { "Jebediah Kerman", "9", "42" }, payload.Select(p => p.Value).ToArray());
            Assert.Equal("evaseatleave start kerbal='Jebediah Kerman' vehiclePid=42 seatPart=seatExternalCmd",
                TestCommandEvaSeat.FormatLeaveStartLine("Jebediah Kerman", 42u, "seatExternalCmd"));
            Assert.Equal("evaseatleave complete kerbal='Jebediah Kerman' kerbalPid=9 vehiclePid=42",
                TestCommandEvaSeat.FormatLeaveCompleteLine("Jebediah Kerman", 9u, 42u));
        }

        [Fact]
        public void BoardStartLine_IsInvariantCulture()
        {
            var saved = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                Assert.EndsWith("dist=1.50",
                    TestCommandEvaSeat.FormatBoardStartLine("Jebediah Kerman", 1u, "seatExternalCmd", 2u, 1.5));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void ReasonTables_AreDisjointAndPrefixed()
        {
            Assert.Empty(TestCommandEvaSeat.Reasons.Intersect(TestCommandEvaSeat.ErrorReasons));
            foreach (string r in TestCommandEvaSeat.Reasons.Concat(TestCommandEvaSeat.ErrorReasons))
                Assert.True(r.StartsWith("evaseatboard-") || r.StartsWith("evaseatleave-"), r);
            Assert.Equal(TestCommandEvaSeat.Reasons.Length, TestCommandEvaSeat.Reasons.Distinct().Count());
        }

        // ----- Dispatch -----

        [Fact]
        public void Board_OutsideFlight_Defers_NotInFlight()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatBoard"),
                new DispatchState { Scene = TestCommandScene.MainMenu });
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-in-flight", r.Reason);
        }

        [Fact]
        public void Board_SplitPending_DefersBeforeNotEva()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatBoard"), Flight(false, true));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("split-pending", r.Reason);
        }

        [Fact]
        public void Board_NotOnEva_Defers_NotEva()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatBoard"), Flight(false, false));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("not-eva", r.Reason);
        }

        [Fact]
        public void Board_OnEva_Executes()
        {
            Assert.Equal(DispatchDecision.Execute,
                TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatBoard"), Flight(true, false)).Decision);
        }

        [Fact]
        public void Leave_SplitPending_Defers()
        {
            var r = TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatLeave"), Flight(false, true));
            Assert.Equal(DispatchDecision.Defer, r.Decision);
            Assert.Equal("split-pending", r.Reason);
        }

        [Fact]
        public void Leave_SeatedOnVehicle_Executes()
        {
            // A seated kerbal's vessel is the vehicle, never an EVA vessel.
            Assert.Equal(DispatchDecision.Execute,
                TestCommandDispatcher.DecideDispatch(Cmd("id=1 cmd=EvaSeatLeave"), Flight(false, false)).Decision);
        }

        [Theory]
        [InlineData("EvaSeatBoard")]
        [InlineData("EvaSeatLeave")]
        public void Both_RideTheDefaultBudget(string verb)
        {
            Assert.Equal(DeferralBudget.DefaultSeconds, DeferralBudget.BudgetSeconds(verb));
        }
    }
}
