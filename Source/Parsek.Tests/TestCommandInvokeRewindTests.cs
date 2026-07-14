using System.Collections.Generic;
using System.Linq;
using Parsek.TestCommands;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// M-C1 coverage for the pure InvokeRewind decision core
    /// (<see cref="TestCommandInvokeRewind"/>): the two-phase completion decider, the
    /// gate-refusal message shape, and the terminal OK payload. Fails if a mid-reload
    /// poll prematurely terminates, a genuine failure is read as success, or the gate
    /// reason is not surfaced verbatim.
    /// </summary>
    public class TestCommandInvokeRewindTests
    {
        private const double Budget = 300.0;

        private static string Val(List<KeyValuePair<string, string>> p, string key)
            => p.First(kv => kv.Key == key).Value;

        [Fact]
        public void ContextPending_StillWaiting()
        {
            // Pre-load / mid-load straddle: keep holding the head regardless of marker/budget.
            Assert.Equal(RewindCompletionDecision.StillWaiting,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    5.0, contextPending: true, markerPresent: false, Budget));
            Assert.Equal(RewindCompletionDecision.StillWaiting,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    Budget + 10.0, contextPending: true, markerPresent: true, Budget));
        }

        [Fact]
        public void ContextCleared_MarkerPresent_CompleteOk()
        {
            Assert.Equal(RewindCompletionDecision.CompleteOk,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    5.0, contextPending: false, markerPresent: true, Budget));
        }

        [Fact]
        public void ContextCleared_NoMarker_WithinBudget_RewindFailed()
        {
            // The context cleared but no marker landed (StartInvoke's LoadGame returned null
            // or ConsumePostLoad aborted) -> fast failure BEFORE the budget.
            Assert.Equal(RewindCompletionDecision.RewindFailed,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    5.0, contextPending: false, markerPresent: false, Budget));
        }

        [Fact]
        public void ContextCleared_NoMarker_BudgetExpired_RewindTimeout()
        {
            Assert.Equal(RewindCompletionDecision.RewindTimeout,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    Budget, contextPending: false, markerPresent: false, Budget));
        }

        [Fact]
        public void MarkerPresent_WinsOverBudgetExpiry()
        {
            // A marker is the success even past budget (it is unambiguously this command's).
            Assert.Equal(RewindCompletionDecision.CompleteOk,
                TestCommandInvokeRewind.DecideRewindCompletion(
                    Budget + 10.0, contextPending: false, markerPresent: true, Budget));
        }

        [Fact]
        public void GateRefusalMsg_SurfacesReasonVerbatimBehindPrefix()
        {
            Assert.Equal("refly-gate Quicksave file missing on disk",
                TestCommandInvokeRewind.GateRefusalMsg("Quicksave file missing on disk"));
            Assert.Equal("refly-gate ", TestCommandInvokeRewind.GateRefusalMsg(null));
        }

        [Fact]
        public void CompletePayload_CarriesRewoundSessionRpSlotActivePid()
        {
            var p = TestCommandInvokeRewind.BuildCompletePayload("sess_ab", "rp_b9_root", "1", 123456u);
            Assert.Equal("true", Val(p, "rewound"));
            Assert.Equal("sess_ab", Val(p, "session"));
            Assert.Equal("rp_b9_root", Val(p, "rp"));
            Assert.Equal("1", Val(p, "slot"));
            Assert.Equal("123456", Val(p, "activePid"));
            Assert.Equal(new[] { "rewound", "session", "rp", "slot", "activePid" },
                p.Select(kv => kv.Key).ToArray());
        }

        [Fact]
        public void CompletePayload_NullSession_EmptyString()
        {
            var p = TestCommandInvokeRewind.BuildCompletePayload(null, "rp", "0", 0u);
            Assert.Equal(string.Empty, Val(p, "session"));
            Assert.Equal("0", Val(p, "activePid"));
        }
    }
}
