using System.Collections.Generic;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    public class RouteEntityTests
    {
        // catches: a default-value drift that makes the in-memory shape ambiguous
        // on construction. Pins every field that the rest of the codebase will
        // sentinel-check.
        [Fact]
        public void DefaultFields()
        {
            var route = new Route();

            Assert.Equal(RouteStatus.Active, route.Status);

            Assert.NotNull(route.Stops);
            Assert.Empty(route.Stops);

            Assert.NotNull(route.RecordingIds);
            Assert.Empty(route.RecordingIds);

            Assert.NotNull(route.SourceRefs);
            Assert.Empty(route.SourceRefs);

            Assert.Equal(-1, route.CurrentSegmentIndex);
            Assert.Equal(-1, route.PendingStopIndex);

            Assert.Null(route.CurrentCycleStartUT);
            Assert.Null(route.NextEligibilityCheckUT);
            Assert.Null(route.PendingDeliveryUT);

            // M6 hold reasons: the "no hold recorded" defaults.
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(-1.0, route.LastHoldUT);
        }
    }

    /// <summary>
    /// Pins the M6 hold-reason mutators (<see cref="Route.RecordHold"/> /
    /// <see cref="Route.ClearHold"/>): field writes plus the on-change-only
    /// Verbose logging contract (a route re-blocking on the same reason every
    /// crossing refreshes the UT silently; a clear on an already-clear route is
    /// silent). Canonical log-capture pattern (RewindLoggingTests).
    /// </summary>
    [Collection("Sequential")]
    public class RouteHoldMutatorTests : System.IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteHoldMutatorTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // catches: RecordHold dropping a field, or staying silent on a genuine
        // kind/detail change.
        [Fact]
        public void RecordHold_SetsFieldsAndLogsOnChange()
        {
            var route = new Route { Id = "route-hold-set" };

            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 1150.0);

            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                route.LastHoldKind);
            Assert.Equal("LiquidFuel", route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(1150.0, route.LastHoldUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("hold recorded")
                && l.Contains("kind=OriginLacksCargo")
                && l.Contains("detail=LiquidFuel")
                && l.Contains("ut=1150"));
        }

        // catches: a route re-blocking on the SAME reason every crossing spamming
        // a Verbose line per crossing. Only the UT refreshes; the log fires once.
        [Fact]
        public void RecordHold_SameKindAndDetail_LogsOnce()
        {
            var route = new Route { Id = "route-hold-same" };

            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 1150.0);
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "LiquidFuel", 0.0, 1450.0);

            // The UT still refreshed on the silent second call.
            Assert.Equal(1450.0, route.LastHoldUT);
            Assert.Equal(1, logLines.FindAll(l =>
                l.Contains("[Route]") && l.Contains("hold recorded")).Count);

            // A detail change logs again.
            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo,
                "Oxidizer", 0.0, 1750.0);
            Assert.Equal(2, logLines.FindAll(l =>
                l.Contains("[Route]") && l.Contains("hold recorded")).Count);
        }

        // catches: ClearHold leaving a field behind, or logging on an
        // already-clear route (the per-crossing clear on a healthy route must
        // stay silent).
        [Fact]
        public void ClearHold_ResetsAndLogsOnlyWhenSet()
        {
            var route = new Route { Id = "route-hold-clear" };

            // Already clear: no-op, no log.
            route.ClearHold("crossing-eligible");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Route]") && l.Contains("hold cleared"));

            route.RecordHold(
                RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                "funds-short", 750.0, 2000.0);
            route.ClearHold("dispatched");

            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None, route.LastHoldKind);
            Assert.Null(route.LastHoldDetail);
            Assert.Equal(0.0, route.LastHoldShortfall);
            Assert.Equal(-1.0, route.LastHoldUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("hold cleared")
                && l.Contains("reason=dispatched"));

            // Second clear: silent.
            logLines.Clear();
            route.ClearHold("dispatched");
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Route]") && l.Contains("hold cleared"));
        }
    }
}
