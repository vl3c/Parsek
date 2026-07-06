using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Unit tests for the Rec-3 discard-time observability reporter
    /// (<see cref="RouteDiscardObservability"/>). The pure window / summarize helpers
    /// touch no shared state; the reporter emits through <c>ParsekLog</c>, so the class
    /// is <c>[Collection("Sequential")]</c> and captures + resets the log sink.
    ///
    /// Plan: <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c> Phase 4
    /// (observability slice — the reverse-on-discard fix is deferred). The reporter is
    /// BEHAVIOR-NEUTRAL: it only logs; it never reverses, retires, or gates.
    /// </summary>
    [Collection("Sequential")]
    public class RouteDiscardObservabilityTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteDiscardObservabilityTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        private static GameAction Phys(
            GameActionType t, double ut, string routeId, string cycleId, string res, double amt)
            => new GameAction
            {
                Type = t,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteResourceManifest = new Dictionary<string, double> { { res, amt } },
            };

        // ---- TryComputeDiscardedWindow ----

        [Fact]
        public void Window_MinMaxOverRecordings()
        {
            var recs = new List<Recording>
            {
                new Recording { ExplicitStartUT = 300.0, ExplicitEndUT = 400.0 },
                new Recording { ExplicitStartUT = 100.0, ExplicitEndUT = 250.0 },
            };

            Assert.True(RouteDiscardObservability.TryComputeDiscardedWindow(
                recs, out double min, out double max));
            Assert.Equal(100.0, min);
            Assert.Equal(400.0, max);
        }

        [Fact]
        public void Window_SkipsNullAndBoundslessPlaceholders()
        {
            var recs = new List<Recording>
            {
                null,
                new Recording(), // no trajectory, no explicit bounds -> StartUT=EndUT=0 -> skipped
                new Recording { ExplicitStartUT = 500.0, ExplicitEndUT = 600.0 },
            };

            Assert.True(RouteDiscardObservability.TryComputeDiscardedWindow(
                recs, out double min, out double max));
            Assert.Equal(500.0, min);
            Assert.Equal(600.0, max);
        }

        [Fact]
        public void Window_EmptyOrAllPlaceholdersReturnsFalse()
        {
            Assert.False(RouteDiscardObservability.TryComputeDiscardedWindow(
                null, out _, out _));
            Assert.False(RouteDiscardObservability.TryComputeDiscardedWindow(
                new List<Recording> { new Recording(), null }, out _, out _));
        }

        [Fact]
        public void Window_SkipsStartOnlyInvertedRecording()
        {
            // Start-only recording: ExplicitStartUT set, ExplicitEndUT defaults to NaN, so
            // StartUT=100 but EndUT falls back to 0.0 (< start). It must be skipped, not
            // accepted as an inverted [100..0] window (which would silently select nothing).
            var recs = new List<Recording>
            {
                new Recording { ExplicitStartUT = 100.0 },
            };
            Assert.False(RouteDiscardObservability.TryComputeDiscardedWindow(recs, out _, out _));

            // A start-only recording alongside a real one must not drag the window: only the
            // real recording contributes.
            var mixed = new List<Recording>
            {
                new Recording { ExplicitStartUT = 100.0 },                       // inverted -> skipped
                new Recording { ExplicitStartUT = 300.0, ExplicitEndUT = 400.0 }, // real
            };
            Assert.True(RouteDiscardObservability.TryComputeDiscardedWindow(mixed, out double min, out double max));
            Assert.Equal(300.0, min);
            Assert.Equal(400.0, max);
        }

        // ---- SummarizePhysicalLeak (pure decision) ----

        [Fact]
        public void Summarize_RewindBacked_ReportsNothing()
        {
            var actions = new List<GameAction>
            {
                Phys(GameActionType.RouteCargoDelivered, 150.0, "R1", "c1", "LiquidFuel", 200.0),
            };

            int n = RouteDiscardObservability.SummarizePhysicalLeak(
                actions, 100.0, 200.0, rewindOrRpBacked: true, out string summary);

            Assert.Equal(0, n);
            Assert.Equal(string.Empty, summary);
        }

        [Fact]
        public void Summarize_NonRewind_CountsOnlyPhysicalRowsInWindow()
        {
            var actions = new List<GameAction>
            {
                Phys(GameActionType.RouteCargoDelivered, 150.0, "R1", "c1", "LiquidFuel", 200.0),
                Phys(GameActionType.RouteCargoDebited, 160.0, "R1", "c1", "Ore", 500.0),
                // marker row, no manifest -> not counted
                new GameAction { Type = GameActionType.RouteDispatched, UT = 155.0, RouteId = "R1", RouteCycleId = "c1" },
                // physical but OUT of window -> not counted
                Phys(GameActionType.RouteCargoDelivered, 999.0, "R1", "c9", "LiquidFuel", 50.0),
            };

            int n = RouteDiscardObservability.SummarizePhysicalLeak(
                actions, 100.0, 200.0, rewindOrRpBacked: false, out string summary);

            Assert.Equal(2, n);
            Assert.Contains("R1", summary);
            Assert.Contains("physical mutation", summary);
        }

        [Fact]
        public void Summarize_OnlyFundsOrMarkerRows_ReportsNothing()
        {
            var actions = new List<GameAction>
            {
                new GameAction { Type = GameActionType.RouteDispatched, UT = 150.0, RouteId = "R1" },
                // KSC-funds-only debit: no manifest
                new GameAction { Type = GameActionType.RouteCargoDebited, UT = 160.0, RouteId = "R1", RouteKscFundsCost = 1000f },
                new GameAction { Type = GameActionType.RouteRecoveryCredited, UT = 170.0, RouteId = "R1", RouteKscFundsCost = 900f },
            };

            int n = RouteDiscardObservability.SummarizePhysicalLeak(
                actions, 100.0, 200.0, rewindOrRpBacked: false, out string summary);

            Assert.Equal(0, n);
            Assert.Equal(string.Empty, summary);
        }

        // ---- ReportDiscardLeakForRecordings (log capture) ----

        [Fact]
        public void Report_EmitsWarnForLeak()
        {
            var recs = new List<Recording>
            {
                new Recording { ExplicitStartUT = 100.0, ExplicitEndUT = 200.0 },
            };
            var actions = new List<GameAction>
            {
                Phys(GameActionType.RouteCargoDelivered, 150.0, "FuelRun", "c1", "LiquidFuel", 200.0),
            };

            int n = RouteDiscardObservability.ReportDiscardLeakForRecordings(
                recs, actions, rewindOrRpBacked: false, "unit-test");

            Assert.Equal(1, n);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("Rec-3 residual") && l.Contains("FuelRun"));
        }

        [Fact]
        public void Report_RewindBacked_NoWarn()
        {
            var recs = new List<Recording>
            {
                new Recording { ExplicitStartUT = 100.0, ExplicitEndUT = 200.0 },
            };
            var actions = new List<GameAction>
            {
                Phys(GameActionType.RouteCargoDelivered, 150.0, "FuelRun", "c1", "LiquidFuel", 200.0),
            };

            int n = RouteDiscardObservability.ReportDiscardLeakForRecordings(
                recs, actions, rewindOrRpBacked: true, "unit-test");

            Assert.Equal(0, n);
            Assert.DoesNotContain(logLines, l => l.Contains("Rec-3 residual"));
        }

        [Fact]
        public void Report_NoWindow_NoWarn()
        {
            var actions = new List<GameAction>
            {
                Phys(GameActionType.RouteCargoDelivered, 150.0, "FuelRun", "c1", "LiquidFuel", 200.0),
            };

            int n = RouteDiscardObservability.ReportDiscardLeakForRecordings(
                new List<Recording>(), actions, rewindOrRpBacked: false, "unit-test");

            Assert.Equal(0, n);
            Assert.DoesNotContain(logLines, l => l.Contains("Rec-3 residual"));
        }
    }
}
