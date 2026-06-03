using System;
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

    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.StatusReason"/>, the pure mapping that
    /// QW4 renders IN the Status cell (the raw enum name moves to the hover
    /// tooltip). Every <see cref="RouteStatus"/> must map to a non-empty,
    /// player-readable string that is NOT just the enum token, so a player reads
    /// "why" rather than a one-word state. Unity-free, so exercised directly.
    /// </summary>
    public class LogisticsWindowUIStatusReasonTests
    {
        // Every defined RouteStatus maps to a non-empty reason that differs from
        // the raw enum name (the whole point of QW4: the cell shows the reason,
        // the tooltip carries the enum token).
        [Fact]
        public void EveryStatus_MapsToNonEnumReason()
        {
            foreach (RouteStatus status in (RouteStatus[])Enum.GetValues(typeof(RouteStatus)))
            {
                string reason = LogisticsWindowUI.StatusReason(status);
                Assert.False(string.IsNullOrEmpty(reason));
                Assert.NotEqual(status.ToString(), reason);
            }
        }

        // Spot-check the blocked-active "visual-only" reason names the symptom a
        // player sees (the ghost still flies but nothing transfers).
        [Fact]
        public void WaitingForResources_ExplainsVisualOnlyCycle()
        {
            string reason = LogisticsWindowUI.StatusReason(RouteStatus.WaitingForResources);
            Assert.Contains("ghost flies", reason);
        }

        // Each individual status maps to its specific reason text (ordinal inputs
        // because RouteStatus is internal and cannot appear in a public signature).
        [Theory]
        [InlineData((int)RouteStatus.Active, "Dispatching on schedule")]
        [InlineData((int)RouteStatus.InTransit, "Ghost in transit")]
        [InlineData((int)RouteStatus.Paused, "Paused - not auto-dispatching")]
        public void Status_MapsToExpectedReason(int statusOrdinal, string expected)
        {
            Assert.Equal(expected, LogisticsWindowUI.StatusReason((RouteStatus)statusOrdinal));
        }
    }

    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.FormatCycleCount"/>, the pure Cyc-column
    /// formatter (QW5): completed deliveries plus a "/ N skipped" suffix only when
    /// cycles were blocked. InvariantCulture, so large counts carry no thousands
    /// separator. Unity-free, so exercised directly.
    /// </summary>
    public class LogisticsWindowUICycleCountTests
    {
        [Theory]
        [InlineData(3, 1, "3 / 1 skipped")]
        [InlineData(3, 0, "3")]
        [InlineData(0, 0, "0")]
        [InlineData(0, 2, "0 / 2 skipped")]
        // Large count: InvariantCulture must not inject a thousands separator.
        [InlineData(1000, 0, "1000")]
        [InlineData(1000, 250, "1000 / 250 skipped")]
        public void FormatCycleCount_MatchesExpected(int completed, int skipped, string expected)
        {
            Assert.Equal(expected, LogisticsWindowUI.FormatCycleCount(completed, skipped));
        }
    }

    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.ResolveTooltipEcho"/>, the pure decision
    /// (QW6) behind the bottom tooltip echo box: render the box only when a control
    /// is hovered (GUI.tooltip non-empty), otherwise signal the zero-height
    /// placeholder branch so the IMGUI control count stays stable. Unity-free, so
    /// exercised directly.
    /// </summary>
    public class LogisticsWindowUITooltipEchoTests
    {
        [Fact]
        public void NullTooltip_DoesNotShow()
        {
            (bool show, string text) = LogisticsWindowUI.ResolveTooltipEcho(null);
            Assert.False(show);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void EmptyTooltip_DoesNotShow()
        {
            (bool show, string text) = LogisticsWindowUI.ResolveTooltipEcho("");
            Assert.False(show);
            Assert.Equal(string.Empty, text);
        }

        [Fact]
        public void NonEmptyTooltip_ShowsSameText()
        {
            const string tip = "Delete this route";
            (bool show, string text) = LogisticsWindowUI.ResolveTooltipEcho(tip);
            Assert.True(show);
            Assert.Equal(tip, text);
        }
    }
}
