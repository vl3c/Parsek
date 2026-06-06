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
    /// Pins the M6 armed-state classifier and labels
    /// (<see cref="LogisticsWindowUI.ClassifyArmedSend"/> /
    /// <see cref="LogisticsWindowUI.LabelForArmedState"/> /
    /// <see cref="LogisticsWindowUI.TooltipForArmedState"/>). Both arming paths set
    /// the same <see cref="Route.PauseAfterCurrentCycle"/> flag, so the armer is
    /// inferred from the route's status: InTransit means Pause-mid-cycle, any other
    /// dispatchable status means Send Once. <see cref="RouteStatus"/> is internal, so
    /// theories take int ordinals and cast inside (mirrors the sibling tests).
    /// </summary>
    public class LogisticsWindowUIArmedStateTests
    {
        // catches: an InTransit arm being labeled as a Send Once. A Pause requested
        // while InTransit must classify as PauseAfterCycle and read
        // "Pausing after this cycle...".
        [Fact]
        public void InTransit_ClassifiesPauseAfterCycle_AndLabels()
        {
            var kind = LogisticsWindowUI.ClassifyArmedSend(RouteStatus.InTransit);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.PauseAfterCycle, kind);
            Assert.Equal("Pausing after this cycle...", LogisticsWindowUI.LabelForArmedState(kind));
        }

        // catches: a Send-Once-armed dispatchable status being mislabeled as a pause.
        // Active / the blocked-active waits / DestinationFull all classify as SendOnce
        // and read "Sending one cycle...".
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void Dispatchable_ClassifiesSendOnce_AndLabels(int statusOrdinal)
        {
            var kind = LogisticsWindowUI.ClassifyArmedSend((RouteStatus)statusOrdinal);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.SendOnce, kind);
            Assert.Equal("Sending one cycle...", LogisticsWindowUI.LabelForArmedState(kind));
        }

        // catches: the two labels collapsing into one. The whole point of M6 is that
        // a pause-armed route reads differently from a send-once-armed route, and
        // neither is the raw enum token.
        [Fact]
        public void Labels_DifferAndAreNotEnumTokens()
        {
            string pause = LogisticsWindowUI.LabelForArmedState(
                LogisticsWindowUI.ArmedSendKind.PauseAfterCycle);
            string send = LogisticsWindowUI.LabelForArmedState(
                LogisticsWindowUI.ArmedSendKind.SendOnce);

            Assert.NotEqual(pause, send);
            Assert.NotEqual(LogisticsWindowUI.ArmedSendKind.PauseAfterCycle.ToString(), pause);
            Assert.NotEqual(LogisticsWindowUI.ArmedSendKind.SendOnce.ToString(), send);
        }

        // catches: empty or shared tooltips. Each armed state carries a distinct,
        // non-empty explanatory tooltip.
        [Fact]
        public void Tooltips_NonEmptyAndDistinct()
        {
            string pause = LogisticsWindowUI.TooltipForArmedState(
                LogisticsWindowUI.ArmedSendKind.PauseAfterCycle);
            string send = LogisticsWindowUI.TooltipForArmedState(
                LogisticsWindowUI.ArmedSendKind.SendOnce);

            Assert.False(string.IsNullOrEmpty(pause));
            Assert.False(string.IsNullOrEmpty(send));
            Assert.NotEqual(pause, send);
        }

        // THE M6 bug fix: a Send-Once arm un-pauses Paused -> Active -> InTransit while
        // still armed, so once the cycle is in flight status alone reads InTransit for
        // BOTH a Send-Once and a Pause arm. ResolveArmedKind honors the send-once
        // provenance set, so a Send-Once-in-transit stays "Sending one cycle..." and is
        // NOT mislabeled "Pausing after this cycle...".
        [Fact]
        public void ResolveArmedKind_SendOnceArmed_StaysSendOnce_EvenInTransit()
        {
            var kind = LogisticsWindowUI.ResolveArmedKind(sendOnceArmed: true, RouteStatus.InTransit);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.SendOnce, kind);
            Assert.Equal("Sending one cycle...", LogisticsWindowUI.LabelForArmedState(kind));
        }

        // A genuine Pause-mid-cycle arm (not in the send-once set) reads PauseAfterCycle.
        [Fact]
        public void ResolveArmedKind_NotSendOnce_InTransit_IsPauseAfterCycle()
        {
            var kind = LogisticsWindowUI.ResolveArmedKind(sendOnceArmed: false, RouteStatus.InTransit);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.PauseAfterCycle, kind);
        }

        // When provenance is unknown (post-reload), a non-InTransit armed route falls
        // back to the status heuristic, which reads SendOnce.
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.Paused)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        public void ResolveArmedKind_NotSendOnce_NonTransit_FallsBackToSendOnce(int statusOrdinal)
        {
            var kind = LogisticsWindowUI.ResolveArmedKind(sendOnceArmed: false, (RouteStatus)statusOrdinal);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.SendOnce, kind);
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
    /// Pins <see cref="LogisticsWindowUI.FormatIntervalFieldValue"/>, the pure
    /// formatter for the M1 inline interval text field's displayed value: a friendly
    /// duration WITH a unit (s / m / h / d via <see cref="LogisticsWindowUI.FormatDuration"/>)
    /// that round-trips through the unit-aware
    /// <see cref="RouteCadence.ParseAndSnapInterval"/>, with a "0" fallback for a
    /// non-positive / non-finite interval so the field is always editable. Unity-free,
    /// so exercised directly.
    /// </summary>
    public class LogisticsWindowUIIntervalFieldTests
    {
        [Theory]
        [InlineData(45.0, "45s")]       // under a minute
        [InlineData(600.0, "10.0m")]    // 10 minutes
        [InlineData(1800.0, "30.0m")]   // 30 minutes
        [InlineData(7200.0, "2.0h")]    // 2 hours
        [InlineData(86400.0, "4.0d")]   // 4 Kerbin days (21600 s each)
        // InvariantCulture: the decimal point is always "." regardless of locale.
        [InlineData(599.6, "10.0m")]
        public void FormatIntervalFieldValue_FormatsFriendlyDurationWithUnit(double seconds, string expected)
        {
            Assert.Equal(expected, LogisticsWindowUI.FormatIntervalFieldValue(seconds));
        }

        // catches: a zero / negative / NaN / Infinity interval rendering "-" or empty
        // (which a player cannot edit). Falls back to "0" so the field is editable.
        [Theory]
        [InlineData(0.0)]
        [InlineData(-300.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void FormatIntervalFieldValue_NonPositiveOrNonFinite_ReturnsZero(double seconds)
        {
            Assert.Equal("0", LogisticsWindowUI.FormatIntervalFieldValue(seconds));
        }
    }

    /// <summary>
    /// Pins <see cref="LogisticsWindowUI.ResolveTooltipEcho"/>, the pure decision
    /// (QW6) behind the bottom tooltip echo box: report whether a control is hovered
    /// (GUI.tooltip non-empty) so the box renders boxed help text, or collapses to
    /// zero height. The boolean drives the box's spacing / content / style only; the
    /// control count emitted by DrawTooltipEchoBox is invariant across the IMGUI
    /// Layout and Repaint passes. Unity-free, so exercised directly.
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
