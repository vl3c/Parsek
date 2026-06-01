using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.ShouldShowSendingButton"/>, the pure
    /// predicate that decides whether a route's action cell shows the disabled
    /// "Sending..." affordance (armed one-shot / in-flight cycle) instead of a
    /// live action button. The predicate is Unity-free, so it is exercised here
    /// without IMGUI. Theories take the status as an <c>int</c> ordinal because
    /// <see cref="RouteStatus"/> is <c>internal</c> and cannot appear in a public
    /// test-method signature (mirrors <see cref="RouteStatusPolicyTests"/>).
    /// </summary>
    public class LogisticsWindowUISendingButtonTests
    {
        private static Route RouteWith(RouteStatus status, bool pauseAfter)
        {
            return new RouteFixtureBuilder()
                .WithId("sending-button-test")
                .WithStatus(status)
                .WithCycleCounters(completed: 0, skipped: 0, pauseAfter: pauseAfter)
                .Build();
        }

        // A committed one-shot / in-flight cycle (PauseAfterCurrentCycle) that has
        // not yet landed back in Paused and is in a dispatchable state shows
        // "Sending...".
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void ArmedAndDispatchable_ShowsSending(int statusOrdinal)
        {
            var route = RouteWith((RouteStatus)statusOrdinal, pauseAfter: true);
            Assert.True(LogisticsWindowUI.ShouldShowSendingButton(route));
        }

        // Armed but already landed back in Paused (cycle complete / idle): the
        // normal Send Once / Activate buttons should show, not "Sending...".
        [Fact]
        public void ArmedButPaused_DoesNotShowSending()
        {
            var route = RouteWith(RouteStatus.Paused, pauseAfter: true);
            Assert.False(LogisticsWindowUI.ShouldShowSendingButton(route));
        }

        // Armed but in a hard-broken endpoint/source state that cannot send:
        // "Sending..." would be misleading, so show the normal actions.
        [Theory]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void ArmedButBroken_DoesNotShowSending(int statusOrdinal)
        {
            var route = RouteWith((RouteStatus)statusOrdinal, pauseAfter: true);
            Assert.False(LogisticsWindowUI.ShouldShowSendingButton(route));
        }

        // Not armed: a periodic Active route (or a mid-cycle periodic route, or an
        // idle Paused route) is not a one-shot send, so it never shows "Sending...".
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.InTransit)]
        [InlineData((int)RouteStatus.Paused)]
        public void NotArmed_DoesNotShowSending(int statusOrdinal)
        {
            var route = RouteWith((RouteStatus)statusOrdinal, pauseAfter: false);
            Assert.False(LogisticsWindowUI.ShouldShowSendingButton(route));
        }

        [Fact]
        public void NullRoute_DoesNotShowSending()
        {
            Assert.False(LogisticsWindowUI.ShouldShowSendingButton(null));
        }
    }
}
