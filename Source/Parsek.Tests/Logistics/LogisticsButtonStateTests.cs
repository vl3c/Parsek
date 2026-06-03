using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins <see cref="LogisticsButtonState.FormatLogisticsButtonLabel"/> and
    /// <see cref="LogisticsButtonState.AnyRouteHardBroken"/>, the two pure helpers
    /// (QW8) behind the main-window Logistics button's live count and red
    /// broken-state tint. Both are Unity-free, so exercised directly. Theories take
    /// the status as an <c>int</c> ordinal because <see cref="RouteStatus"/> is
    /// <c>internal</c> and cannot appear in a public test-method signature
    /// (mirrors <see cref="LogisticsWindowUISendingButtonTests"/>).
    /// </summary>
    public class LogisticsButtonStateTests
    {
        [Theory]
        [InlineData(0, "Logistics (0)")]
        [InlineData(3, "Logistics (3)")]
        // InvariantCulture must not inject a thousands separator on a large count.
        [InlineData(1000, "Logistics (1000)")]
        public void FormatLogisticsButtonLabel_MatchesExpected(int count, string expected)
        {
            Assert.Equal(expected, LogisticsButtonState.FormatLogisticsButtonLabel(count));
        }

        // Even under a comma-decimal culture the label stays InvariantCulture: the
        // count is a plain int so the (N) form must not pick up locale grouping.
        [Fact]
        public void FormatLogisticsButtonLabel_IsInvariantUnderCommaLocale()
        {
            CultureInfo prev = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("Logistics (1000)", LogisticsButtonState.FormatLogisticsButtonLabel(1000));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = prev;
            }
        }

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
