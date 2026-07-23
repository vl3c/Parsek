using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C2 coverage for the EvaBoard pure surfaces (<see cref="TestCommandEvaBoard"/>).
    /// IsWithinBoardRange: inclusive 10 m bound. DecideBoardCompletion (F2): crew unchanged
    /// never CompleteOk (the silent-stock-refusal guard); EVA vessel gone but crew absent
    /// never OK (a lost kerbal is never reported boarded); crew aboard but target NOT the
    /// active vessel -> StillWaiting; crew aboard + active but board-merge NOT quiescent ->
    /// StillWaiting. Fails if the head advances inside the board-merge window (the
    /// StopRecording / second-EvaExit mis-route regressions).
    /// </summary>
    public class TestCommandEvaBoardTests
    {
        private const double Budget = 120.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // ----- IsWithinBoardRange -----

        [Fact]
        public void Range_WithinBound_True()
        {
            Assert.True(TestCommandEvaBoard.IsWithinBoardRange(5.0, 10.0));
        }

        [Fact]
        public void Range_AtBound_InclusiveTrue()
        {
            Assert.True(TestCommandEvaBoard.IsWithinBoardRange(10.0, 10.0));
        }

        [Fact]
        public void Range_PastBound_False()
        {
            Assert.False(TestCommandEvaBoard.IsWithinBoardRange(10.0001, 10.0));
            Assert.False(TestCommandEvaBoard.IsWithinBoardRange(25.0, 10.0));
        }

        [Fact]
        public void Range_DefaultOverload_Is10m()
        {
            Assert.Equal(10.0, TestCommandEvaBoard.DefaultBoardRangeMeters);
            Assert.True(TestCommandEvaBoard.IsWithinBoardRange(10.0));
            Assert.False(TestCommandEvaBoard.IsWithinBoardRange(10.5));
        }

        // ----- DecideBoardCompletion (five conjuncts) -----

        private static BoardCompletionDecision Decide(
            double elapsed, bool gone, bool aboard, bool targetActive, bool quiescent, bool settled)
            => TestCommandEvaBoard.DecideBoardCompletion(elapsed, gone, aboard, targetActive, quiescent, settled, Budget);

        [Fact]
        public void Board_AllFive_Ok()
        {
            Assert.Equal(BoardCompletionDecision.CompleteOk,
                Decide(5.0, gone: true, aboard: true, targetActive: true, quiescent: true, settled: true));
        }

        [Fact]
        public void Board_CrewUnchanged_NeverOk_StillWaiting()
        {
            // The silent-stock-refusal guard: BoardPart is void, so a refused board leaves the
            // crew unchanged; that is NEVER a false OK.
            Assert.Equal(BoardCompletionDecision.StillWaiting,
                Decide(5.0, gone: false, aboard: false, targetActive: false, quiescent: true, settled: true));
        }

        [Fact]
        public void Board_VesselGoneButCrewAbsent_NeverOk()
        {
            // A lost kerbal (EVA vessel destroyed but not in the target crew) is never reported
            // boarded.
            Assert.Equal(BoardCompletionDecision.StillWaiting,
                Decide(5.0, gone: true, aboard: false, targetActive: true, quiescent: true, settled: true));
        }

        [Fact]
        public void Board_CrewAboardButTargetNotActive_StillWaiting()
        {
            Assert.Equal(BoardCompletionDecision.StillWaiting,
                Decide(5.0, gone: true, aboard: true, targetActive: false, quiescent: true, settled: true));
        }

        [Fact]
        public void Board_CrewAboardActiveButNotQuiescent_StillWaiting()
        {
            // The F2 board-merge window: crew moved + vessel gone + target active, but
            // OnVesselSwitchComplete / HandleTreeBoardMerge have not run -> hold the head so a
            // next FIFO command can never corrupt the merge.
            Assert.Equal(BoardCompletionDecision.StillWaiting,
                Decide(5.0, gone: true, aboard: true, targetActive: true, quiescent: false, settled: true));
        }

        [Fact]
        public void Board_NotSettled_StillWaiting()
        {
            Assert.Equal(BoardCompletionDecision.StillWaiting,
                Decide(5.0, gone: true, aboard: true, targetActive: true, quiescent: true, settled: false));
        }

        [Fact]
        public void Board_BudgetExpiredCrewUnchanged_BoardTimeout()
        {
            // A silently-refused board (CanBoard off / capacity raced / science prompt) converts
            // to board-timeout, never a false OK.
            Assert.Equal(BoardCompletionDecision.BoardTimeout,
                Decide(Budget, gone: false, aboard: false, targetActive: false, quiescent: true, settled: true));
        }

        [Fact]
        public void Board_PositiveBeatsBudget()
        {
            Assert.Equal(BoardCompletionDecision.CompleteOk,
                Decide(Budget + 10.0, gone: true, aboard: true, targetActive: true, quiescent: true, settled: true));
        }

        // ----- BuildCompletePayload -----

        [Fact]
        public void Payload_CarriesKerbalBoardedPid()
        {
            var p = TestCommandEvaBoard.BuildCompletePayload("Valentina Kerman", 98765u);
            Assert.Equal("Valentina Kerman", Val(p, "kerbal"));
            Assert.Equal("98765", Val(p, "boardedPid"));
            Assert.Equal(new[] { "kerbal", "boardedPid" }, p.Select(kv => kv.Key).ToArray());
        }
    }
}
