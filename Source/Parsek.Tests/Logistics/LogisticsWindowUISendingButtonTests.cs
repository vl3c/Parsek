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
    /// "Delivering..." affordance (armed one-shot / in-flight cycle) instead of a
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
        // "Delivering...".
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
        // normal Send Once / Activate buttons should show, not "Delivering...".
        [Fact]
        public void ArmedButPaused_DoesNotShowSending()
        {
            var route = RouteWith(RouteStatus.Paused, pauseAfter: true);
            Assert.False(LogisticsWindowUI.ShouldShowSendingButton(route));
        }

        // Armed but in a hard-broken endpoint/source state that cannot send:
        // "Delivering..." would be misleading, so show the normal actions.
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
        // idle Paused route) is not a one-shot send, so it never shows "Delivering...".
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
        // "Pausing...".
        [Fact]
        public void InTransit_ClassifiesPauseAfterCycle_AndLabels()
        {
            var kind = LogisticsWindowUI.ClassifyArmedSend(RouteStatus.InTransit);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.PauseAfterCycle, kind);
        }

        // catches: a Send-Once-armed dispatchable status being mislabeled as a pause.
        // Active / the blocked-active waits / DestinationFull all classify as SendOnce
        // and read "Delivering...".
        [Theory]
        [InlineData((int)RouteStatus.Active)]
        [InlineData((int)RouteStatus.WaitingForResources)]
        [InlineData((int)RouteStatus.WaitingForFunds)]
        [InlineData((int)RouteStatus.DestinationFull)]
        public void Dispatchable_ClassifiesSendOnce_AndLabels(int statusOrdinal)
        {
            var kind = LogisticsWindowUI.ClassifyArmedSend((RouteStatus)statusOrdinal);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.SendOnce, kind);
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
        // provenance set, so a Send-Once-in-transit stays "Delivering..." and is
        // NOT mislabeled "Pausing...".
        [Fact]
        public void ResolveArmedKind_SendOnceArmed_StaysSendOnce_EvenInTransit()
        {
            var kind = LogisticsWindowUI.ResolveArmedKind(sendOnceArmed: true, RouteStatus.InTransit);
            Assert.Equal(LogisticsWindowUI.ArmedSendKind.SendOnce, kind);
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
    /// Pins <see cref="LogisticsWindowUI.FormatIntervalFieldValue"/>, the pure
    /// formatter for the M1 inline interval text field's displayed value: a friendly
    /// duration WITH a unit (s / m / h / d via <see cref="LogisticsWindowUI.FormatDuration"/>)
    /// that round-trips through the unit-aware
    /// <see cref="RouteCadence.ParseAndSnapInterval"/>, with a "0" fallback for a
    /// non-positive / non-finite interval so the field is always editable. Unity-free,
    /// so exercised directly.
    /// </summary>
    [Collection("Sequential")]
    public class LogisticsWindowUIIntervalFieldTests : IDisposable
    {
        public LogisticsWindowUIIntervalFieldTests()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = null;
        }

        [Theory]
        [InlineData(45.0, "45s")]       // under a minute
        [InlineData(600.0, "10.0m")]    // 10 minutes
        [InlineData(1800.0, "30.0m")]   // 30 minutes
        [InlineData(7200.0, "2.0h")]    // 2 hours
        [InlineData(18000.0, "5.0h")]   // 5 hours, still under one Kerbin day
        [InlineData(21600.0, "1.0d")]   // exactly one Kerbin day
        [InlineData(86400.0, "4.0d")]   // 4 Kerbin days (21600 s each)
        // InvariantCulture: the decimal point is always "." regardless of locale.
        [InlineData(599.6, "10.0m")]
        public void FormatIntervalFieldValue_FormatsFriendlyDurationWithUnit(double seconds, string expected)
        {
            Assert.Equal(expected, LogisticsWindowUI.FormatIntervalFieldValue(seconds));
        }

        // catches: the old hard-coded formatter, which switched to days at 86400 s but
        // divided by 21600 s, so on the Earth calendar 24 h read "4.0d" and 12 h read "12.0h"
        // while a typed "1d" meant a different length. On the Earth calendar a day is 24 h.
        [Theory]
        [InlineData(7200.0, "2.0h")]
        [InlineData(21600.0, "6.0h")]   // one Kerbin day is only a quarter Earth day
        [InlineData(43200.0, "12.0h")]
        [InlineData(86400.0, "1.0d")]
        [InlineData(129600.0, "1.5d")]
        public void FormatIntervalFieldValue_EarthCalendar_UsesTwentyFourHourDay(double seconds, string expected)
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = false;
            Assert.Equal(expected, LogisticsWindowUI.FormatIntervalFieldValue(seconds));
        }

        // catches: a displayed "Nd" value that parses back to a different length on
        // either calendar (the field shows FormatDuration and re-parses what it shows).
        [Theory]
        [InlineData(true, 86400.0, 21600.0, 4)]
        [InlineData(false, 172800.0, 86400.0, 2)]
        public void FormatIntervalFieldValue_DayValue_RoundTripsThroughParse(
            bool kerbinTime, double seconds, double span, int expectedN)
        {
            ParsekTimeFormat.KerbinTimeOverrideForTesting = kerbinTime;
            string shown = LogisticsWindowUI.FormatIntervalFieldValue(seconds);
            Assert.EndsWith("d", shown);
            Assert.True(RouteCadence.ParseAndSnapInterval(shown, span, out int n));
            Assert.Equal(expectedN, n);
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

    // The Logistics bottom tooltip echo box moved to the shared TooltipEchoBox
    // helper; its pure decisions are pinned by Parsek.Tests.TooltipEchoBoxTests.
}
