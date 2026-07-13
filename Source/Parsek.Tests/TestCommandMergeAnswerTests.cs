using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 coverage for the pure AnswerMergeDialog decision core
    /// (<see cref="TestCommandMergeAnswer"/>): the choice-string -> button-role mapper, the
    /// result-label mapper, the two-phase completion decider (answer-applied AND
    /// scene-settled), and the terminal payload. Fails if a choice string drift invokes the
    /// wrong button, or the head advances before the answer is applied and the scene settles.
    /// </summary>
    public class TestCommandMergeAnswerTests
    {
        private const double Budget = 120.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        // Expected role passed as a name string because the internal enum cannot appear in
        // a public xUnit theory signature.
        [Theory]
        [InlineData("merge", "Merge")]
        [InlineData("commit", "Merge")]
        [InlineData("discard", "Discard")]
        [InlineData("seal", "Seal")]
        public void MapChoice_KnownChoices(string choice, string expected)
        {
            Assert.Equal(expected, TestCommandMergeAnswer.MapChoice(choice).ToString());
        }

        [Theory]
        [InlineData("Merge")]   // case-sensitive
        [InlineData("MERGE")]
        [InlineData("seal ")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("frobnicate")]
        public void MapChoice_UnknownChoices(string choice)
        {
            Assert.Equal(MergeAnswerChoice.Unknown, TestCommandMergeAnswer.MapChoice(choice));
        }

        // Driven off the wire choice string (mapped to the role) so the internal enum stays
        // out of the public theory signature.
        [Theory]
        [InlineData("merge", "committed")]
        [InlineData("discard", "discarded")]
        [InlineData("seal", "sealed")]
        public void ResultLabel_PerChoice(string choice, string expected)
        {
            Assert.Equal(expected, TestCommandMergeAnswer.ResultLabel(TestCommandMergeAnswer.MapChoice(choice)));
        }

        [Fact]
        public void DecideAnswerCompletion_AppliedAndSettled_CompleteOk()
        {
            Assert.Equal(AnswerCompletionDecision.CompleteOk,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    5.0, answerApplied: true, TestCommandScene.SpaceCenter, Budget));
        }

        [Fact]
        public void DecideAnswerCompletion_AppliedButInFlight_StillWaiting()
        {
            // FLIGHT is NOT settled even though the pre-transition dialog can be answered
            // while still in FLIGHT: completion waits for the post-answer scene change.
            Assert.Equal(AnswerCompletionDecision.StillWaiting,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    5.0, answerApplied: true, TestCommandScene.Flight, Budget));
        }

        [Fact]
        public void DecideAnswerCompletion_AppliedButLoading_StillWaiting()
        {
            // A mid-transition LOADING scene is not settled either.
            Assert.Equal(AnswerCompletionDecision.StillWaiting,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    5.0, answerApplied: true, TestCommandScene.Loading, Budget));
        }

        [Fact]
        public void DecideAnswerCompletion_SettledButNotApplied_StillWaiting()
        {
            // Scene-settle ALONE is never OK: an orphaned unanswered dialog must not
            // false-complete.
            Assert.Equal(AnswerCompletionDecision.StillWaiting,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    5.0, answerApplied: false, TestCommandScene.SpaceCenter, Budget));
        }

        [Fact]
        public void DecideAnswerCompletion_BudgetExpiredWithoutApply_AnswerTimeout()
        {
            Assert.Equal(AnswerCompletionDecision.AnswerTimeout,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    Budget, answerApplied: false, TestCommandScene.Flight, Budget));
        }

        [Fact]
        public void DecideAnswerCompletion_CompleteWinsOverBudget()
        {
            // Applied + settled is OK even past budget.
            Assert.Equal(AnswerCompletionDecision.CompleteOk,
                TestCommandMergeAnswer.DecideAnswerCompletion(
                    Budget + 10.0, answerApplied: true, TestCommandScene.SpaceCenter, Budget));
        }

        [Fact]
        public void CompletePayload_CarriesChoiceAndResult()
        {
            var p = TestCommandMergeAnswer.BuildCompletePayload("merge", "committed");
            Assert.Equal("merge", Val(p, "choice"));
            Assert.Equal("committed", Val(p, "result"));
            Assert.Equal(new[] { "choice", "result" }, p.Select(kv => kv.Key).ToArray());
        }
    }
}
