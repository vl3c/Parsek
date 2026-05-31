using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure cadence-stepper helper <see cref="RouteCadence"/> (plan
    /// Phase 6 task 3 / 5). The stepper logic is extracted from
    /// <c>LogisticsWindowUI</c> so the clamp + <c>DispatchInterval = N * span</c>
    /// recompute is testable without IMGUI. Runs Sequential because
    /// <see cref="RouteCadence.ApplyMultiplier"/> logs through the global static
    /// sink.
    /// </summary>
    [Collection("Sequential")]
    public class RouteCadenceTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteCadenceTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Route RouteWithSpan(double span, int multiplier)
        {
            return new RouteFixtureBuilder()
                .WithId("cadence-test-route")
                .WithSchedule(transitDurationSeconds: span, dispatchIntervalSeconds: multiplier * span)
                .WithCadenceMultiplier(multiplier)
                .Build();
        }

        // ==================================================================
        // ClampCadenceMultiplier (the floor invariant)
        // ==================================================================

        [Theory]
        [InlineData(-5, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(7, 7)]
        public void ClampCadenceMultiplier_FloorsAtOne(int input, int expected)
        {
            Assert.Equal(expected, Route.ClampCadenceMultiplier(input));
        }

        // ==================================================================
        // StepMultiplier
        // ==================================================================

        // catches: the "-" button driving N below the 1x floor (the route cannot
        // dispatch faster than the run allows).
        [Fact]
        public void StepMultiplier_MinusAtFloor_StaysOne()
        {
            Assert.Equal(1, RouteCadence.StepMultiplier(1, -1));
        }

        [Fact]
        public void StepMultiplier_PlusAndMinus_AdjustByOne()
        {
            Assert.Equal(3, RouteCadence.StepMultiplier(2, +1));
            Assert.Equal(2, RouteCadence.StepMultiplier(3, -1));
        }

        // ==================================================================
        // DeriveDispatchInterval
        // ==================================================================

        // catches: the derived interval not being N x span (the v0 cadence model).
        [Fact]
        public void DeriveDispatchInterval_IsMultiplierTimesSpan()
        {
            Assert.Equal(840.0, RouteCadence.DeriveDispatchInterval(1, 840.0));
            Assert.Equal(1680.0, RouteCadence.DeriveDispatchInterval(2, 840.0));
            Assert.Equal(2520.0, RouteCadence.DeriveDispatchInterval(3, 840.0));
        }

        // catches: a sub-floor multiplier producing a sub-span interval instead of
        // being clamped up to the 1x floor first.
        [Fact]
        public void DeriveDispatchInterval_ClampsMultiplier()
        {
            Assert.Equal(840.0, RouteCadence.DeriveDispatchInterval(0, 840.0));
            Assert.Equal(840.0, RouteCadence.DeriveDispatchInterval(-3, 840.0));
        }

        // catches: a zero / NaN span producing a zero / NaN interval (the caller
        // would then never fire the loop clock). Falls back to the span value.
        [Fact]
        public void DeriveDispatchInterval_NonPositiveSpan_ReturnsSpan()
        {
            Assert.Equal(0.0, RouteCadence.DeriveDispatchInterval(3, 0.0));
            Assert.True(double.IsNaN(RouteCadence.DeriveDispatchInterval(3, double.NaN)));
        }

        // ==================================================================
        // ApplyMultiplier
        // ==================================================================

        // catches: an edit not recomputing DispatchInterval from the new N, so the
        // loop clock (which reads DispatchInterval) would stay on the old cadence.
        [Fact]
        public void ApplyMultiplier_RaisesN_RecomputesInterval_AndLogs()
        {
            Route route = RouteWithSpan(span: 600.0, multiplier: 1);
            Assert.Equal(600.0, route.DispatchInterval);

            bool changed = RouteCadence.ApplyMultiplier(route, 3);

            Assert.True(changed);
            Assert.Equal(3, route.CadenceMultiplier);
            Assert.Equal(1800.0, route.DispatchInterval);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RouteCadence") && l.Contains("1x->3x"));
        }

        // catches: the floor clamp being skipped on apply (a 0 lands a sub-floor N).
        [Fact]
        public void ApplyMultiplier_SubFloor_ClampsToOne()
        {
            Route route = RouteWithSpan(span: 600.0, multiplier: 4);

            bool changed = RouteCadence.ApplyMultiplier(route, 0);

            Assert.True(changed);
            Assert.Equal(1, route.CadenceMultiplier);
            Assert.Equal(600.0, route.DispatchInterval);
        }

        // catches: a no-op edit (same N) being treated as a change and re-logging /
        // re-deriving needlessly.
        [Fact]
        public void ApplyMultiplier_SameN_NoOp_ReturnsFalse()
        {
            Route route = RouteWithSpan(span: 600.0, multiplier: 2);

            bool changed = RouteCadence.ApplyMultiplier(route, 2);

            Assert.False(changed);
            Assert.Equal(2, route.CadenceMultiplier);
            Assert.Equal(1200.0, route.DispatchInterval);
        }

        [Fact]
        public void ApplyMultiplier_NullRoute_ReturnsFalse()
        {
            Assert.False(RouteCadence.ApplyMultiplier(null, 3));
        }
    }
}
