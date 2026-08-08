using System.Collections.Generic;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decision cells for the EnterWatchMode seam verb (the player-workflow
    /// lane's watch-the-replay promotion): the optional index arg, the product-style
    /// auto-select conjunction (a headless mirror of
    /// MissionsWindowUI.ResolveMissionWatchTarget), the read-back completion decision,
    /// and the terminal payload.
    ///
    /// The completion cells are the load-bearing ones: ParsekFlight.EnterWatchMode is
    /// void, a TOGGLE, and silent-failure-heavy, so a verdict that did not wait for the
    /// observed read-back would report OK on an entry that never happened.
    /// </summary>
    public class TestCommandEnterWatchModeTests
    {
        private static TestCommandEnterWatchMode.WatchCandidate C(
            int index, bool inScope, bool ghost, bool body, bool range)
            => new TestCommandEnterWatchMode.WatchCandidate
            {
                Index = index,
                InScope = inScope,
                HasActiveGhost = ghost,
                OnSameBody = body,
                WithinVisualRange = range,
            };

        [Theory]
        [InlineData(null, true, -1)]     // absent = AUTO-SELECT sentinel
        [InlineData("", true, -1)]
        [InlineData("0", true, 0)]
        [InlineData("7", true, 7)]
        [InlineData("-1", false, -1)]    // an explicit negative is not the sentinel
        [InlineData("3.0", false, -1)]   // an index is an integer token, never coerced
        [InlineData("3,0", false, -1)]   // no locale comma
        [InlineData(" 3", false, -1)]    // NumberStyles.None: no whitespace tolerance
        [InlineData("bogus", false, -1)]
        public void Index_arg_is_optional_and_a_nonnegative_integer(
            string raw, bool ok, int want)
        {
            bool parsed = TestCommandEnterWatchMode.TryParseIndexArg(raw, out int index);
            Assert.Equal(ok, parsed);
            if (ok)
                Assert.Equal(want, index);
        }

        [Fact]
        public void Auto_select_takes_the_first_fully_watchable_candidate()
        {
            var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>
            {
                C(0, true, false, true, true),   // no ghost
                C(1, true, true, false, true),   // wrong body
                C(2, true, true, true, false),   // out of visual range
                C(3, true, true, true, true),    // <- first fully watchable
                C(4, true, true, true, true),
            };
            Assert.Equal(3, TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates));
        }

        [Fact]
        public void Auto_select_honours_the_tree_scope_filter()
        {
            // Index 1 is watchable but out of scope: the scoped selection must skip it
            // rather than watch a vessel from a different flight.
            var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>
            {
                C(0, false, true, true, true),
                C(1, false, true, true, true),
                C(2, true, true, true, true),
            };
            Assert.Equal(2, TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates));
        }

        [Fact]
        public void Auto_select_returns_minus_one_when_nothing_is_watchable()
        {
            var candidates = new List<TestCommandEnterWatchMode.WatchCandidate>
            {
                C(0, true, true, true, false),
                C(1, false, true, true, true),
            };
            Assert.Equal(-1, TestCommandEnterWatchMode.ResolveAutoWatchIndex(candidates));
            Assert.Equal(-1, TestCommandEnterWatchMode.ResolveAutoWatchIndex(
                new List<TestCommandEnterWatchMode.WatchCandidate>()));
            Assert.Equal(-1, TestCommandEnterWatchMode.ResolveAutoWatchIndex(null));
        }

        [Fact]
        public void Completion_waits_for_the_read_back_and_the_settle_window()
        {
            // Read-back true but the settle window has not drained: keep waiting.
            Assert.Equal(WatchEntryCompletionDecision.StillWaiting,
                TestCommandEnterWatchMode.DecideWatchCompletion(1.0, true, 2, 60.0));
            // Both satisfied: OK.
            Assert.Equal(WatchEntryCompletionDecision.CompleteOk,
                TestCommandEnterWatchMode.DecideWatchCompletion(1.0, true, 0, 60.0));
        }

        [Fact]
        public void Completion_times_out_on_a_silent_refusal()
        {
            // The real failure mode: EnterWatchMode is void and refuses silently
            // (distance / body / resolve guards), so the read-back never goes true.
            Assert.Equal(WatchEntryCompletionDecision.StillWaiting,
                TestCommandEnterWatchMode.DecideWatchCompletion(59.9, false, 0, 60.0));
            Assert.Equal(WatchEntryCompletionDecision.WatchTimeout,
                TestCommandEnterWatchMode.DecideWatchCompletion(60.0, false, 0, 60.0));
            Assert.Equal(WatchEntryCompletionDecision.WatchTimeout,
                TestCommandEnterWatchMode.DecideWatchCompletion(120.0, false, 3, 60.0));
        }

        [Fact]
        public void A_confirmed_read_back_wins_over_an_expired_budget()
        {
            // Same precedence as the jump verbs': having observed the thing asked for,
            // report it rather than a timeout that arrived in the same frame.
            Assert.Equal(WatchEntryCompletionDecision.CompleteOk,
                TestCommandEnterWatchMode.DecideWatchCompletion(999.0, true, 0, 60.0));
        }

        [Fact]
        public void Complete_payload_carries_index_recid_and_the_watching_claim()
        {
            var payload = TestCommandEnterWatchMode.BuildCompletePayload(4, "rec-abc");
            Assert.Equal(3, payload.Count);
            Assert.Equal("index", payload[0].Key);
            Assert.Equal("4", payload[0].Value);
            Assert.Equal("recId", payload[1].Key);
            Assert.Equal("rec-abc", payload[1].Value);
            Assert.Equal("watching", payload[2].Key);
            Assert.Equal("true", payload[2].Value);

            Assert.Equal(string.Empty,
                TestCommandEnterWatchMode.BuildCompletePayload(0, null)[1].Value);
        }
    }
}
