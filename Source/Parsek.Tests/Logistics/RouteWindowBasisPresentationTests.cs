using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// (M5 P3) Pins the display-only windowed-basis surfaces: the basis label,
    /// the OQ2 windowed cadence-stepper wording, the D8 basis-aware
    /// next-dispatch-window countdown accessor (field-source pinned to
    /// <c>unit.PhaseAnchorUT</c> / <c>unit.CadenceSeconds</c>), and the
    /// countdown-branch precedence. All InvariantCulture-formatted.
    /// </summary>
    [Collection("Sequential")]
    public class RouteWindowBasisPresentationTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteWindowBasisPresentationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Route BuildRoute(
            int cadenceMultiplier = 1, long windowAnchor = -1,
            RouteStatus status = RouteStatus.Active)
        {
            return new Route
            {
                Id = "route-presentation",
                Status = status,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0,
                CadenceMultiplier = cadenceMultiplier,
                WindowAnchorCycleIndex = windowAnchor,
                TransitDuration = 300.0,
                DispatchInterval = 300.0 * cadenceMultiplier,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    },
                },
            };
        }

        // ==================================================================
        // D8 countdown accessor
        // ==================================================================

        // catches: the ReaimWindows countdown missing the residual modulo (a
        // skipped window's launch shown as "next dispatch") or reading raw
        // ReaimSchedule fields instead of the unit anchor/cadence.
        [Fact]
        public void CountdownPresentation_ReaimWindows_NextDeliverableWindowLaunch()
        {
            // Unit anchor 1000 / cadence 3000; the schedule's OWN PhaseAnchorUT
            // field is deliberately shifted inside BuildReaimUnit's fixture via
            // FirstDepartureUT (+50) - the accessor must key on the UNIT fields.
            var unit = RouteWindowBasisTests.BuildReaimUnit(
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                synodicCadence: 3000.0, phaseAnchorUT: 1000.0, targetBody: "Duna");
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;

            // N=1: next window after now(2000) is window 1, launch 4000.
            var routeN1 = BuildRoute(cadenceMultiplier: 1);
            RouteStore.AddRoute(routeN1);
            bool ok = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                routeN1, 2000.0, out double seconds, out RouteWindowBasis basis, out string target);
            Assert.True(ok);
            Assert.Equal(2000.0, seconds, 6);
            Assert.Equal(RouteWindowBasis.ReaimWindows, basis);
            Assert.Equal("Duna", target);

            // N=2 anchored at window 0: window 1 (launch 4000) is SKIPPED by the
            // modulo, so the countdown targets window 2's launch (7000).
            var routeN2 = BuildRoute(cadenceMultiplier: 2, windowAnchor: 0);
            ok = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                routeN2, 2000.0, out seconds, out basis, out target);
            Assert.True(ok);
            Assert.Equal(5000.0, seconds, 6);

            // N=2 with the anchor UNSET (-1): the next crossing adopts and
            // delivers (D3), so the very next window (1, launch 4000) counts.
            var routeUnanchored = BuildRoute(cadenceMultiplier: 2, windowAnchor: -1);
            ok = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                routeUnanchored, 2000.0, out seconds, out basis, out target);
            Assert.True(ok);
            Assert.Equal(2000.0, seconds, 6);

            Assert.Contains(logLines, l => l.Contains("NextDispatchWindow")
                && l.Contains("basis=ReaimWindows"));
        }

        // catches: the zero-drift countdown approximating with the uniform
        // formula instead of resolving through the schedule's non-uniform
        // launch list.
        [Fact]
        public void CountdownPresentation_ZeroDrift_NextLaunchAfter()
        {
            // Real schedule: launches 1000, 2000, 3000 (minSpacing 600 thins the
            // 500s faithful grid) - the uniform cadence-1000 formula from anchor
            // 1000 happens to coincide at these, so pin against a mid-gap UT.
            var sched = new MissionRelaunchSchedule(
                ut0: 0.0, anchorPeriod: 500.0,
                otherPeriods: null, otherTolerances: null,
                floorUT: 1000.0, lookaheadMultiples: 100000,
                minSpacingSeconds: 600.0);
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 1000.0, phaseAnchorUT: sched.FirstLaunchUT,
                overlapCadenceSeconds: 1000.0, memberWindows: null,
                relaunchSchedule: sched);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;

            var route = BuildRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            bool ok = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, 1500.0, out double seconds, out RouteWindowBasis basis, out string target);

            Assert.True(ok);
            Assert.Equal(500.0, seconds, 6); // next launch 2000
            Assert.Equal(RouteWindowBasis.ZeroDriftSchedule, basis);
            Assert.Null(target);
            Assert.Contains(logLines, l => l.Contains("NextDispatchWindow")
                && l.Contains("basis=ZeroDriftSchedule"));
        }

        // catches: a flat route being served by the window accessor (its
        // countdown must stay the existing flat dock formula, unchanged).
        [Fact]
        public void CountdownPresentation_Flat_UsesExistingDockCountdown()
        {
            var unit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;

            var route = BuildRoute(cadenceMultiplier: 1);
            RouteStore.AddRoute(route);

            // Window accessor refuses flat...
            bool hasWindow = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, 1100.0, out double windowSeconds, out RouteWindowBasis basis, out _);
            Assert.False(hasWindow);
            Assert.Equal(RouteWindowBasis.FlatInterval, basis);

            // ...and the existing dock countdown still owns the branch.
            bool hasCrossing = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, 1100.0, out double dockSeconds);
            Assert.True(hasCrossing);
            Assert.Equal(50.0, dockSeconds, 6); // dock 1150

            var decision = LogisticsCountdownPresentation.ResolveDetailCountdown(
                route.Status, null, hasCrossing, dockSeconds,
                hasWindow, windowSeconds, 1100.0);
            Assert.Equal(LogisticsCountdownPresentation.CountdownBranch.NextDelivery,
                decision.Branch);
        }

        // ==================================================================
        // Branch precedence + wording
        // ==================================================================

        [Fact]
        public void ResolveDetailCountdown_NextWindow_PrecedenceAndFallback()
        {
            // Window beats the flat crossing when both are offered.
            var d = LogisticsCountdownPresentation.ResolveDetailCountdown(
                RouteStatus.Active, null, true, 100.0, true, 5000.0, 0.0);
            Assert.Equal(LogisticsCountdownPresentation.CountdownBranch.NextWindow, d.Branch);
            Assert.Equal(5000.0, d.Seconds, 6);

            // Wait-state retry still wins over the window countdown.
            d = LogisticsCountdownPresentation.ResolveDetailCountdown(
                RouteStatus.WaitingForResources, 50.0, false, 0.0, true, 5000.0, 0.0);
            Assert.Equal(LogisticsCountdownPresentation.CountdownBranch.RechecksIn, d.Branch);

            // The 5-arg overload is byte-identical to hasNextWindow=false.
            d = LogisticsCountdownPresentation.ResolveDetailCountdown(
                RouteStatus.Active, null, true, 100.0, 0.0);
            Assert.Equal(LogisticsCountdownPresentation.CountdownBranch.NextDelivery, d.Branch);
        }

        [Fact]
        public void FormatCountdownSurfaces_NextWindow()
        {
            Assert.Equal("Next launch window T-1h 2m",
                LogisticsCountdownPresentation.FormatDetailCountdownLine(
                    LogisticsCountdownPresentation.CountdownBranch.NextWindow, "T-1h 2m"));
            Assert.Equal("T-1h 2m",
                LogisticsCountdownPresentation.FormatNextDeliveryCell(
                    LogisticsCountdownPresentation.CountdownBranch.NextWindow, "T-1h 2m"));
        }

        // ==================================================================
        // OQ2 stepper wording + basis label
        // ==================================================================

        [Fact]
        public void StepperWording_WindowedBasis_EveryNth()
        {
            Assert.Equal("1x (every window)", RouteWindowBasisPresentation.FormatWindowedCadence(1));
            Assert.Equal("2x (every 2nd window)", RouteWindowBasisPresentation.FormatWindowedCadence(2));
            Assert.Equal("3x (every 3rd window)", RouteWindowBasisPresentation.FormatWindowedCadence(3));
            Assert.Equal("4x (every 4th window)", RouteWindowBasisPresentation.FormatWindowedCadence(4));
            // Sub-floor clamps to the 1x floor.
            Assert.Equal("1x (every window)", RouteWindowBasisPresentation.FormatWindowedCadence(0));
            // Ordinal exceptions (11th-13th, 21st).
            Assert.Equal("11th", RouteWindowBasisPresentation.Ordinal(11));
            Assert.Equal("12th", RouteWindowBasisPresentation.Ordinal(12));
            Assert.Equal("13th", RouteWindowBasisPresentation.Ordinal(13));
            Assert.Equal("21st", RouteWindowBasisPresentation.Ordinal(21));
        }

        [Fact]
        public void BasisLabel_PerBasis()
        {
            Assert.Null(RouteWindowBasisPresentation.BasisLabel(
                RouteWindowBasis.FlatInterval, "Duna"));
            Assert.Equal("(launch window schedule)", RouteWindowBasisPresentation.BasisLabel(
                RouteWindowBasis.ZeroDriftSchedule, null));
            Assert.Equal("(Duna transfer)", RouteWindowBasisPresentation.BasisLabel(
                RouteWindowBasis.ReaimWindows, "Duna"));
            Assert.Equal("(transfer windows)", RouteWindowBasisPresentation.BasisLabel(
                RouteWindowBasis.ReaimWindows, null));

            Assert.False(RouteWindowBasisPresentation.IsWindowedBasis(RouteWindowBasis.FlatInterval));
            Assert.True(RouteWindowBasisPresentation.IsWindowedBasis(RouteWindowBasis.ZeroDriftSchedule));
            Assert.True(RouteWindowBasisPresentation.IsWindowedBasis(RouteWindowBasis.ReaimWindows));
        }

        // catches: a non-ghost-driving route reaching the LoopUnit resolve (the
        // accessor must gate on status like the H1 helper).
        [Fact]
        public void CountdownAccessor_NotGhostDriving_RefusesWithoutResolve()
        {
            int resolves = 0;
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => { resolves++; return null; };
            var route = BuildRoute(status: RouteStatus.Paused);
            RouteStore.AddRoute(route);

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDispatchWindow(
                route, 2000.0, out _, out RouteWindowBasis basis, out _);

            Assert.False(ok);
            Assert.Equal(RouteWindowBasis.FlatInterval, basis);
            Assert.Equal(0, resolves);
        }
    }
}
