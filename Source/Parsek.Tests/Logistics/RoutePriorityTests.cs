using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the pure priority-stepper helper <see cref="RoutePriority"/> (M1,
    /// design D8). The stepper logic mirrors <see cref="RouteCadence"/> so the
    /// clamp + no-op + Info-log shape is testable without IMGUI. Runs Sequential
    /// because <see cref="RoutePriority.Apply"/> logs through the global static
    /// sink.
    /// </summary>
    [Collection("Sequential")]
    public class RoutePriorityTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RoutePriorityTests()
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

        // ==================================================================
        // ClampPriority (the floor invariant)
        // ==================================================================

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(-1, 0)]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(7, 7)]
        public void ClampPriority_FloorsAtZero(int input, int expected)
        {
            Assert.Equal(expected, Route.ClampPriority(input));
        }

        // ==================================================================
        // Step
        // ==================================================================

        // catches: the "-" button driving the priority below the 0 floor.
        [Fact]
        public void Step_MinusAtFloor_StaysZero()
        {
            Assert.Equal(0, RoutePriority.Step(0, -1));
        }

        [Fact]
        public void Step_PlusAndMinus_AdjustByOne()
        {
            Assert.Equal(3, RoutePriority.Step(2, +1));
            Assert.Equal(1, RoutePriority.Step(2, -1));
        }

        // ==================================================================
        // Apply
        // ==================================================================

        // catches: an edit not landing on the route, or the change not being
        // Info-logged (priority edits must be greppable).
        [Fact]
        public void Apply_RaisesPriority_SetsField_AndLogs()
        {
            var route = new Route { Id = "priority-test-route", DispatchPriority = 0 };

            bool changed = RoutePriority.Apply(route, 2);

            Assert.True(changed);
            Assert.Equal(2, route.DispatchPriority);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RoutePriority") && l.Contains("priority 0->2"));
        }

        // catches: the floor clamp being skipped on apply (a negative input
        // landing a sub-floor priority).
        [Fact]
        public void Apply_Negative_ClampsToZero()
        {
            var route = new Route { Id = "priority-test-route", DispatchPriority = 4 };

            bool changed = RoutePriority.Apply(route, -3);

            Assert.True(changed);
            Assert.Equal(0, route.DispatchPriority);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RoutePriority") && l.Contains("priority 4->0"));
        }

        // catches: a no-op edit (same value) being treated as a change and
        // re-logging at Info needlessly.
        [Fact]
        public void Apply_SameValue_NoOp_ReturnsFalse()
        {
            var route = new Route { Id = "priority-test-route", DispatchPriority = 2 };

            bool changed = RoutePriority.Apply(route, 2);

            Assert.False(changed);
            Assert.Equal(2, route.DispatchPriority);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RoutePriority.Apply") && l.Contains("no-op"));
        }

        // catches: a negative input that clamps to the CURRENT value being
        // treated as a change (clamp-then-compare must no-op).
        [Fact]
        public void Apply_NegativeOntoZero_NoOp_ReturnsFalse()
        {
            var route = new Route { Id = "priority-test-route", DispatchPriority = 0 };

            bool changed = RoutePriority.Apply(route, -1);

            Assert.False(changed);
            Assert.Equal(0, route.DispatchPriority);
        }

        [Fact]
        public void Apply_NullRoute_ReturnsFalse()
        {
            Assert.False(RoutePriority.Apply(null, 3));
        }
    }
}
