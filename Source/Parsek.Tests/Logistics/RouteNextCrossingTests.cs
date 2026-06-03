using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Pins the H1 read-only next-dock-crossing countdown: the pure
    /// crossing-to-seconds math (<see cref="RouteLoopClock.TryComputeSecondsToNextDockCrossing"/>),
    /// the orchestrator-side accessor exercised through the
    /// <see cref="RouteOrchestrator.LoopUnitResolverForTesting"/> seam
    /// (<see cref="RouteOrchestrator.TryComputeSecondsToNextDockCrossing"/>), and the
    /// wait-state-fallback branch decision
    /// (<see cref="LogisticsCountdownPresentation.ResolveDetailCountdown"/>). No live
    /// KSP: the LoopUnit is built directly via its public ctor (mirrors
    /// <c>RouteLoopDeliveryFireTests.BuildUnit</c>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteNextCrossingTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteNextCrossingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers
        // ==================================================================

        // span [1000, 1300] (300s); cadence == span; dock UT 1150 inside the span.
        // Uniform v0 path (no schedule / loiter cuts), so the dock-UT spacing is
        // PhaseAnchorUT + k*300 + (1150 - 1000): cycle 0 -> 1150, 1 -> 1450, 2 -> 1750.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        // A unit carrying whole-period loiter cuts -> non-v0 (re-aim) path.
        private static GhostPlaybackLogic.LoopUnit BuildUnitWithLoiterCuts()
        {
            var cuts = new List<GhostPlaybackLogic.LoopCut>
            {
                new GhostPlaybackLogic.LoopCut { StartUT = 1050.0, LengthSeconds = 40.0 },
            };
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 300.0, memberWindows: null,
                relaunchSchedule: null, reaimPlan: null, reaimSchedule: null,
                loiterCuts: cuts);
        }

        // A unit carrying a non-null relaunch schedule -> non-v0 (inter-body) path.
        private static GhostPlaybackLogic.LoopUnit BuildUnitWithSchedule()
        {
            var schedule = new MissionRelaunchSchedule(
                ut0: 1000.0, anchorPeriod: 300.0,
                otherPeriods: null, otherTolerances: null,
                floorUT: 1000.0, lookaheadMultiples: 8);
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 300.0, phaseAnchorUT: 1000.0,
                overlapCadenceSeconds: 300.0, memberWindows: null,
                relaunchSchedule: schedule);
        }

        private static Route BuildLoopRoute(
            string id = "route-next",
            RouteStatus status = RouteStatus.Active,
            double recordedDockUT = 1150.0,
            long lastObservedLoopCycleIndex = -1)
        {
            return new Route
            {
                Id = id,
                Status = status,
                BackingMissionTreeId = "tree-1", // makes IsLoopRoute true
                RecordedDockUT = recordedDockUT,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
            };
        }

        // ==================================================================
        // (1) PURE crossing-to-seconds math (RouteLoopClock)
        // ==================================================================

        // (a) nowUT mid-cycle, BEFORE this cycle's dock -> seconds to THIS cycle's dock.
        [Fact]
        public void PureMath_BeforeDockThisCycle_ReturnsSecondsToThisDock()
        {
            var unit = BuildUnit(); // dock cycle 0 at UT 1150

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.True(ok);
            // dock cycle 0 at 1150; now 1100 -> 50s.
            Assert.Equal(50.0, seconds, 6);
        }

        // (b) nowUT AFTER this cycle's dock with that cycle already observed ->
        // countdown rolls to the NEXT cycle's dock.
        [Fact]
        public void PureMath_AfterDockCycleObserved_RollsToNextCycleDock()
        {
            var unit = BuildUnit(); // dock cycle 0 at 1150, cycle 1 at 1450

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1200.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 0, out double seconds);

            Assert.True(ok);
            // cycle 0 (1150) consumed; next dock is cycle 1 at 1450; now 1200 -> 250s.
            Assert.Equal(250.0, seconds, 6);
        }

        // (c) dock UT outside [SpanStart, SpanEnd] -> false (no crossing ever fires).
        [Fact]
        public void PureMath_DockOutOfSpan_ReturnsFalse()
        {
            var unit = BuildUnit(); // span [1000,1300]

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1400.0, // beyond spanEnd
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // (d) exactly AT this cycle's dock with nothing observed -> the cycle-0
        // crossing is happening NOW, so the NEXT crossing is cycle 1's dock.
        [Fact]
        public void PureMath_ExactlyAtDockBoundary_AdvancesToNextCycle()
        {
            var unit = BuildUnit(); // dock cycle 0 at 1150, cycle 1 at 1450

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1150.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.True(ok);
            // The returned countdown is always strictly positive; the next dock after
            // the now-instant is cycle 1 at 1450 -> 300s.
            Assert.True(seconds > 0.0);
            Assert.Equal(300.0, seconds, 6);
        }

        // (e) lastObservedLoopCycleIndex already advanced past the current cycle ->
        // the consumed cycles are skipped.
        [Fact]
        public void PureMath_LastObservedAdvanced_SkipsConsumedCycles()
        {
            var unit = BuildUnit(); // dock cycle 2 at 1750

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: 1, out double seconds);

            Assert.True(ok);
            // cycles 0 and 1 consumed; next dock is cycle 2 at 1750; now 1100 -> 650s.
            Assert.Equal(650.0, seconds, 6);
        }

        // (f) non-v0 unit (loiter cuts) -> false (do not approximate).
        [Fact]
        public void PureMath_LoiterCutsUnit_ReturnsFalse()
        {
            var unit = BuildUnitWithLoiterCuts();

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // (f') non-v0 unit (relaunch schedule) -> false (do not approximate).
        [Fact]
        public void PureMath_ScheduleUnit_ReturnsFalse()
        {
            var unit = BuildUnitWithSchedule();

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1150.0,
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // Degenerate span -> false.
        [Fact]
        public void PureMath_DegenerateSpan_ReturnsFalse()
        {
            var unit = BuildUnit(spanStartUT: 1000.0, spanEndUT: 1000.0); // zero span

            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 1100.0, recordedDockUT: 1000.0,
                lastObservedLoopCycleIndex: -1, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // Cadence below MinCycleDuration is clamped the same way the span clock
        // clamps it, so the dock-UT spacing uses MinCycleDuration.
        [Fact]
        public void PureMath_SubMinCadence_UsesMinCycleDurationSpacing()
        {
            // span 10s, cadence 1s (< MinCycleDuration 5s); dock at spanStart+3.
            var unit = BuildUnit(
                spanStartUT: 0.0, spanEndUT: 10.0, cadenceSeconds: 1.0, phaseAnchorUT: 0.0);

            // cadence clamps to 5; dock cycle 0 at 0 + (3 - 0) = 3, cycle 1 at 8.
            bool ok = RouteLoopClock.TryComputeSecondsToNextDockCrossing(
                unit, nowUT: 4.0, recordedDockUT: 3.0,
                lastObservedLoopCycleIndex: 0, out double seconds);

            Assert.True(ok);
            // cycle 0 (dock 3) observed; next dock is cycle 1 at 8; now 4 -> 4s.
            Assert.Equal(4.0, seconds, 6);
        }

        // ==================================================================
        // (2) ORCHESTRATOR accessor via the LoopUnitResolverForTesting seam
        // ==================================================================

        // (g) not ghost-driving (Paused) -> false, with the not-ghost-driving log.
        [Fact]
        public void Accessor_NotGhostDriving_ReturnsFalseAndLogs()
        {
            var route = BuildLoopRoute(status: RouteStatus.Paused);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, nowUT: 1100.0, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("NextDockCrossing")
                && l.Contains("not ghost-driving"));
        }

        // (g') EndpointLost is also not ghost-driving -> false.
        [Fact]
        public void Accessor_EndpointLost_ReturnsFalse()
        {
            var route = BuildLoopRoute(status: RouteStatus.EndpointLost);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, nowUT: 1100.0, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // (h) resolver returns null -> false, with the no-unit log.
        [Fact]
        public void Accessor_ResolverReturnsNull_ReturnsFalseAndLogs()
        {
            var route = BuildLoopRoute();
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => null;

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, nowUT: 1100.0, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("NextDockCrossing")
                && l.Contains("no resolvable loop unit"));
        }

        // (i) resolver returns a unit -> forwards the pure result + logs the seconds.
        [Fact]
        public void Accessor_ResolverReturnsUnit_ForwardsPureResultAndLogs()
        {
            var route = BuildLoopRoute(recordedDockUT: 1150.0, lastObservedLoopCycleIndex: -1);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, nowUT: 1100.0, out double seconds);

            Assert.True(ok);
            Assert.Equal(50.0, seconds, 6); // dock cycle 0 at 1150, now 1100.
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("NextDockCrossing")
                && l.Contains("secondsToNextDock"));
        }

        // Accessor with a dock out of span -> false, with the out-of-span log.
        [Fact]
        public void Accessor_DockOutOfSpan_ReturnsFalseAndLogs()
        {
            var route = BuildLoopRoute(recordedDockUT: 1400.0); // beyond spanEnd 1300
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();

            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                route, nowUT: 1100.0, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("NextDockCrossing")
                && l.Contains("dock-out-of-span"));
        }

        // The accessor is strictly read-only: it must NOT advance the loop clock.
        [Fact]
        public void Accessor_DoesNotMutateLoopClockState()
        {
            var route = BuildLoopRoute(recordedDockUT: 1150.0, lastObservedLoopCycleIndex: 3);
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => BuildUnit();

            RouteOrchestrator.TryComputeSecondsToNextDockCrossing(route, nowUT: 1100.0, out _);

            // LastObservedLoopCycleIndex untouched (only ProcessLoopRoute snaps it).
            Assert.Equal(3, route.LastObservedLoopCycleIndex);
        }

        // Null route -> false, no throw.
        [Fact]
        public void Accessor_NullRoute_ReturnsFalse()
        {
            bool ok = RouteOrchestrator.TryComputeSecondsToNextDockCrossing(
                null, nowUT: 1100.0, out double seconds);

            Assert.False(ok);
            Assert.Equal(0.0, seconds);
        }

        // ==================================================================
        // (3) WAIT-STATE FALLBACK branch decision (presentation helper)
        // ==================================================================

        // Blocked wait-state with a future recheck UT -> "Rechecks in" branch wins,
        // even when a next-crossing is also available.
        [Fact]
        public void Branch_WaitStateWithRecheck_PicksRechecksIn()
        {
            var decision = LogisticsCountdownPresentation.ResolveDetailCountdown(
                status: RouteStatus.WaitingForResources,
                nextEligibilityCheckUT: 1130.0,
                hasNextCrossing: true,
                secondsToNextDockCrossing: 50.0,
                nowUT: 1100.0);

            Assert.Equal(
                LogisticsCountdownPresentation.CountdownBranch.RechecksIn, decision.Branch);
            Assert.Equal(30.0, decision.Seconds, 6); // 1130 - 1100.
        }

        // Active route with a finite crossing -> "Next delivery" branch.
        [Fact]
        public void Branch_ActiveWithCrossing_PicksNextDelivery()
        {
            var decision = LogisticsCountdownPresentation.ResolveDetailCountdown(
                status: RouteStatus.Active,
                nextEligibilityCheckUT: null,
                hasNextCrossing: true,
                secondsToNextDockCrossing: 50.0,
                nowUT: 1100.0);

            Assert.Equal(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, decision.Branch);
            Assert.Equal(50.0, decision.Seconds, 6);
        }

        // No crossing and no wait state -> None.
        [Fact]
        public void Branch_NoCrossingNoWait_PicksNone()
        {
            var decision = LogisticsCountdownPresentation.ResolveDetailCountdown(
                status: RouteStatus.Active,
                nextEligibilityCheckUT: null,
                hasNextCrossing: false,
                secondsToNextDockCrossing: 0.0,
                nowUT: 1100.0);

            Assert.Equal(
                LogisticsCountdownPresentation.CountdownBranch.None, decision.Branch);
            Assert.Equal(0.0, decision.Seconds);
        }

        // Wait state whose recheck UT has already passed -> falls back to the
        // next-crossing branch when one is available.
        [Fact]
        public void Branch_WaitStateRecheckPassed_FallsBackToNextDelivery()
        {
            var decision = LogisticsCountdownPresentation.ResolveDetailCountdown(
                status: RouteStatus.DestinationFull,
                nextEligibilityCheckUT: 1090.0, // already in the past
                hasNextCrossing: true,
                secondsToNextDockCrossing: 50.0,
                nowUT: 1100.0);

            Assert.Equal(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, decision.Branch);
            Assert.Equal(50.0, decision.Seconds, 6);
        }

        // IsWaitState classifies exactly the three blocked-but-flying states.
        // RouteStatus is internal, so the inputs stay inside the method body (a
        // public [Theory] parameter of an internal enum trips CS0051).
        [Fact]
        public void IsWaitState_ClassifiesBlockedStates()
        {
            Assert.True(LogisticsCountdownPresentation.IsWaitState(RouteStatus.WaitingForResources));
            Assert.True(LogisticsCountdownPresentation.IsWaitState(RouteStatus.WaitingForFunds));
            Assert.True(LogisticsCountdownPresentation.IsWaitState(RouteStatus.DestinationFull));

            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.Active));
            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.InTransit));
            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.Paused));
            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.EndpointLost));
            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.MissingSourceRecording));
            Assert.False(LogisticsCountdownPresentation.IsWaitState(RouteStatus.SourceChanged));
        }

        // ==================================================================
        // H1 cell / detail-line formatters (pure; the window passes the already-
        // formatted countdown so these stay Unity-free).
        // ==================================================================

        // The "Next" column cell shows the bare formatted countdown for both the
        // next-delivery and the wait-state recheck branch (the branch wording lives
        // in the detail line, not the narrow cell).
        [Fact]
        public void NextDeliveryCell_NextDeliveryBranch_ShowsCountdown()
        {
            Assert.Equal("T-12m 5s", LogisticsCountdownPresentation.FormatNextDeliveryCell(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, "T-12m 5s"));
        }

        [Fact]
        public void NextDeliveryCell_RechecksInBranch_ShowsCountdown()
        {
            Assert.Equal("T-0m 23s", LogisticsCountdownPresentation.FormatNextDeliveryCell(
                LogisticsCountdownPresentation.CountdownBranch.RechecksIn, "T-0m 23s"));
        }

        // No-countdown branch (or an empty formatted string) shows a dash.
        [Fact]
        public void NextDeliveryCell_NoneBranch_ShowsDash()
        {
            Assert.Equal("-", LogisticsCountdownPresentation.FormatNextDeliveryCell(
                LogisticsCountdownPresentation.CountdownBranch.None, "anything"));
        }

        [Fact]
        public void NextDeliveryCell_EmptyCountdown_ShowsDash()
        {
            Assert.Equal("-", LogisticsCountdownPresentation.FormatNextDeliveryCell(
                LogisticsCountdownPresentation.CountdownBranch.NextDelivery, ""));
        }

        // The detail line prefixes the branch wording; None yields null (no line drawn).
        [Fact]
        public void DetailCountdownLine_NextDelivery_PrefixesNextDelivery()
        {
            Assert.Equal("Next delivery T-12m 5s",
                LogisticsCountdownPresentation.FormatDetailCountdownLine(
                    LogisticsCountdownPresentation.CountdownBranch.NextDelivery, "T-12m 5s"));
        }

        [Fact]
        public void DetailCountdownLine_RechecksIn_PrefixesRechecksIn()
        {
            Assert.Equal("Rechecks in T-0m 23s",
                LogisticsCountdownPresentation.FormatDetailCountdownLine(
                    LogisticsCountdownPresentation.CountdownBranch.RechecksIn, "T-0m 23s"));
        }

        [Fact]
        public void DetailCountdownLine_None_ReturnsNull()
        {
            Assert.Null(LogisticsCountdownPresentation.FormatDetailCountdownLine(
                LogisticsCountdownPresentation.CountdownBranch.None, "ignored"));
        }
    }
}
