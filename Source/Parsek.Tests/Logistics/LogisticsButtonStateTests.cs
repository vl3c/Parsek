using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="LogisticsButtonState.AnyRouteHardBroken"/>, the pure helper
    /// (QW8) behind the main-window Logistics button's red broken-state tint. The
    /// button label is a plain "Logistics" literal (no count), so there is nothing
    /// to test there. Unity-free, so exercised directly. Theories take the status as
    /// an <c>int</c> ordinal because <see cref="RouteStatus"/> is <c>internal</c> and
    /// cannot appear in a public test-method signature (mirrors
    /// <see cref="LogisticsWindowUISendingButtonTests"/>).
    /// </summary>
    public class LogisticsButtonStateTests
    {
        [Fact]
        public void NullSequence_IsNotBroken()
        {
            Assert.False(LogisticsButtonState.AnyRouteHardBroken(null));
        }

        [Fact]
        public void EmptySequence_IsNotBroken()
        {
            Assert.False(LogisticsButtonState.AnyRouteHardBroken(new List<RouteStatus>()));
        }

        // None of the healthy / blocked-active / paused states are hard-broken.
        [Fact]
        public void HealthyAndBlockedActiveAndPaused_AreNotBroken()
        {
            var statuses = new List<RouteStatus>
            {
                RouteStatus.Active,
                RouteStatus.InTransit,
                RouteStatus.WaitingForResources,
                RouteStatus.WaitingForFunds,
                RouteStatus.DestinationFull,
                RouteStatus.Paused
            };
            Assert.False(LogisticsButtonState.AnyRouteHardBroken(statuses));
        }

        // Each of the three hard-broken states, alone in the sequence, trips true.
        [Theory]
        [InlineData((int)RouteStatus.EndpointLost)]
        [InlineData((int)RouteStatus.MissingSourceRecording)]
        [InlineData((int)RouteStatus.SourceChanged)]
        public void SingleBrokenStatus_IsBroken(int statusOrdinal)
        {
            var statuses = new List<RouteStatus> { (RouteStatus)statusOrdinal };
            Assert.True(LogisticsButtonState.AnyRouteHardBroken(statuses));
        }

        // A mixed sequence with one broken status among healthy ones trips true.
        [Fact]
        public void MixedSequenceWithBroken_IsBroken()
        {
            var statuses = new List<RouteStatus>
            {
                RouteStatus.Active,
                RouteStatus.Paused,
                RouteStatus.EndpointLost
            };
            Assert.True(LogisticsButtonState.AnyRouteHardBroken(statuses));
        }
    }
}
