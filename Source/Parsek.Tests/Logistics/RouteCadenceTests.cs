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

        // ==================================================================
        // LST-3: loop-clock rebase on a cadence change
        // ==================================================================

        // catches (LST-3): a cadence change recomputing DispatchInterval but leaving
        // LastObservedLoopCycleIndex stale. CadenceSeconds derives from
        // DispatchInterval, so the same UT resolves to a DIFFERENT span-clock
        // cycleIndex after the change; a stale lastObserved then stalls (N raised) or
        // snaps (N lowered) the next crossing. The fix resets it to -1 so the clock
        // re-anchors cleanly (mirrors RouteOrchestrator.TryActivate).
        [Fact]
        public void ApplyMultiplier_NChanges_RebasesLastObservedLoopCycleIndex()
        {
            Route route = RouteWithSpan(span: 300.0, multiplier: 1);
            route.LastObservedLoopCycleIndex = 7;

            bool changed = RouteCadence.ApplyMultiplier(route, 2);

            Assert.True(changed);
            Assert.Equal(2, route.CadenceMultiplier);
            Assert.Equal(600.0, route.DispatchInterval);
            Assert.Equal(-1L, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("RouteCadence")
                && l.Contains("lastObservedLoopCycleIndex 7->-1")
                && l.Contains("rebase"));
        }

        // catches: the no-op same-N path gratuitously resetting the clock. A same-N
        // apply returns false and must NOT touch LastObservedLoopCycleIndex (no
        // change == no rebase, so no spurious re-fire from a clean reset).
        [Fact]
        public void ApplyMultiplier_SameN_DoesNotRebase()
        {
            Route route = RouteWithSpan(span: 300.0, multiplier: 2);
            route.LastObservedLoopCycleIndex = 5;

            bool changed = RouteCadence.ApplyMultiplier(route, 2);

            Assert.False(changed);
            Assert.Equal(5L, route.LastObservedLoopCycleIndex);
        }

        // ==================================================================
        // M1: ParseAndSnapInterval (typed target seconds -> snapped N)
        // ==================================================================

        // catches: the snap not ceiling the typed seconds to the next whole run
        // multiple. A target just over one span (301 over a 300 span) must snap UP
        // to N=2, never round down to N=1, so the route never dispatches faster than
        // the run allows. 599 (just under two spans) still ceils to 2; an exact span
        // boundary (300, 600) lands on the lower multiple (1, 2).
        [Theory]
        [InlineData("301", 300.0, 2)]
        [InlineData("599", 300.0, 2)]
        [InlineData("600", 300.0, 2)]
        [InlineData("300", 300.0, 1)]
        [InlineData("900", 300.0, 3)]
        [InlineData("700", 300.0, 3)]
        public void ParseAndSnapInterval_CeilsToNextMultiple(string text, double span, int expectedN)
        {
            bool ok = RouteCadence.ParseAndSnapInterval(text, span, out int n);
            Assert.True(ok);
            Assert.Equal(expectedN, n);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("ParseAndSnapInterval") && l.Contains("N=" + expectedN));
        }

        // catches: a typed time below a single span snapping below the 1x floor. Any
        // positive target under one span (50, 0.5 over a 300 span) ceils to 1.
        [Theory]
        [InlineData("50", 300.0)]
        [InlineData("0.5", 300.0)]
        [InlineData("1", 300.0)]
        public void ParseAndSnapInterval_BelowOneSpan_FloorsAtOne(string text, double span)
        {
            bool ok = RouteCadence.ParseAndSnapInterval(text, span, out int n);
            Assert.True(ok);
            Assert.Equal(1, n);
        }

        // catches: garbage / empty / non-positive input being accepted and then fed
        // to ApplyMultiplier (which would silently clamp 0 up to 1 and wrongly reset
        // an already-raised cadence). All reject: return false, N=0.
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("abc")]
        [InlineData("NaN")]
        [InlineData("Infinity")]
        [InlineData("-100")]
        [InlineData("0")]
        public void ParseAndSnapInterval_Garbage_Rejects(string text)
        {
            bool ok = RouteCadence.ParseAndSnapInterval(text, 300.0, out int n);
            Assert.False(ok);
            Assert.Equal(0, n);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("ParseAndSnapInterval") && l.Contains("reject"));
        }

        // catches: a comma being silently reinterpreted as a decimal point on a
        // comma-locale system. The parse uses NumberStyles.Float (no AllowThousands)
        // with InvariantCulture, so "1,5" is NOT parsed as 1.5 (which a comma-locale
        // Parse would do): it is rejected outright. This pins that a comma can never
        // sneak through to produce a wrong cadence; the player re-types with a dot.
        [Fact]
        public void ParseAndSnapInterval_CommaNotTreatedAsDecimal_Rejects()
        {
            bool ok = RouteCadence.ParseAndSnapInterval("1,5", 300.0, out int n);
            Assert.False(ok); // NOT parsed as 1.5; comma rejected under InvariantCulture Float
            Assert.Equal(0, n);
        }

        // catches: a non-positive / non-finite span being snapped against (a divide
        // that would yield 0 / NaN / Infinity). Reject regardless of the text.
        [Theory]
        [InlineData("600", 0.0)]
        [InlineData("600", -300.0)]
        [InlineData("600", double.NaN)]
        [InlineData("600", double.PositiveInfinity)]
        public void ParseAndSnapInterval_BadSpan_Rejects(string text, double span)
        {
            bool ok = RouteCadence.ParseAndSnapInterval(text, span, out int n);
            Assert.False(ok);
            Assert.Equal(0, n);
        }

        // catches: the snap not feeding ApplyMultiplier correctly end-to-end. A
        // typed 700 over a 300 span snaps to N=3; ApplyMultiplier then sets
        // DispatchInterval = 3 * 300 = 900 and logs the cadence change.
        [Fact]
        public void ParseAndSnapInterval_FeedsApplyMultiplier_RoundTrip()
        {
            Route route = RouteWithSpan(span: 300.0, multiplier: 1);

            bool ok = RouteCadence.ParseAndSnapInterval("700", route.TransitDuration, out int n);
            Assert.True(ok);
            Assert.Equal(3, n);

            bool changed = RouteCadence.ApplyMultiplier(route, n);
            Assert.True(changed);
            Assert.Equal(3, route.CadenceMultiplier);
            Assert.Equal(900.0, route.DispatchInterval);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("RouteCadence") && l.Contains("1x->3x"));
        }

        // catches (LST-3 end-to-end): after a cadence change the next in-span dock
        // crossing fires EXACTLY once. Without the -1 rebase a stale lastObserved
        // (here 3) would never be exceeded by the post-change cycleIndex (smaller
        // because the cadence grew), stalling delivery indefinitely. With the rebase,
        // the next crossing fires once and a same-cycle re-tick does not re-fire.
        [Fact]
        public void CadenceChangeThenNextCrossing_FiresExactlyOnce()
        {
            // span [1000,1300] (300s), dock at the span midpoint (1150).
            const double spanStart = 1000.0;
            const double spanEnd = 1300.0;
            const double dockUT = 1150.0;

            Route route = RouteWithSpan(span: 300.0, multiplier: 1);
            // Simulate a clock that has already delivered cycle 3 under the OLD
            // (N=1, cadence=300) cadence.
            route.LastObservedLoopCycleIndex = 3;

            // Raise cadence to 2x: DispatchInterval 300 -> 600, lastObserved -> -1.
            Assert.True(RouteCadence.ApplyMultiplier(route, 2));
            Assert.Equal(600.0, route.DispatchInterval);
            Assert.Equal(-1L, route.LastObservedLoopCycleIndex);

            // Build the post-change loop unit: cadence == DispatchInterval (600s),
            // anchored at the new cadence epoch. Pick a UT in the FIRST post-rebase
            // cycle, past the dock phase so the dock crossing is reachable.
            double cadence = route.DispatchInterval;
            double anchor = spanStart;
            GhostPlaybackLogic.LoopUnit unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: spanStart,
                spanEndUT: spanEnd,
                cadenceSeconds: cadence,
                phaseAnchorUT: anchor);

            // 200s into cycle 0 -> loopUT = 1200 (>= dock 1150), cycleIndex 0.
            double sampleUT = anchor + 200.0;
            Assert.True(RouteLoopClock.TryGetRouteLoopState(
                unit, sampleUT, out double loopUT, out long cycleIndex, out bool tail));
            Assert.False(tail);
            Assert.Equal(0L, cycleIndex);
            Assert.True(loopUT >= dockUT, "sample must be past the dock phase");

            // First tick: with lastObserved == -1 the fresh cycle 0 dock fires once.
            bool firstFire = RouteLoopClock.IsDockCrossing(
                unit, loopUT, cycleIndex, dockUT, route.LastObservedLoopCycleIndex,
                out long dockCycleIndex);
            Assert.True(firstFire);
            Assert.Equal(0L, dockCycleIndex);

            // Orchestrator snaps lastObserved forward to the dock cycle.
            route.LastObservedLoopCycleIndex = dockCycleIndex;

            // Second tick in the SAME cycle does NOT re-fire (no double-fire).
            bool reFire = RouteLoopClock.IsDockCrossing(
                unit, loopUT, cycleIndex, dockUT, route.LastObservedLoopCycleIndex,
                out _);
            Assert.False(reFire);
        }
    }
}
