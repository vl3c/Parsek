using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Parsek.Reaim;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// (M5 P1) Pins the pure inter-body window-basis layer: the D1 basis
    /// derivation fail-closed pairs, the D2 residual-cadence rule, the D3
    /// anchor-adoption deliverability predicate, the D5 warp
    /// highest-deliverable-window helper, and the D6 basis-flip transition
    /// evaluator + its orchestrator-side applier (field re-baselines + log
    /// assertions per the house pattern). No firing-path coverage here - that
    /// is P2's <c>RouteInterBodyFireTests</c>.
    /// </summary>
    [Collection("Sequential")]
    public class RouteWindowBasisTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteWindowBasisTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Unit fixtures
        // ==================================================================

        // Flat: no relaunch schedule, no re-aim pair. Span [1000, 1300], cadence
        // == span (one crossing == one cycle) unless widened by the caller.
        private static GhostPlaybackLogic.LoopUnit BuildFlatUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        // Zero-drift: a REAL MissionRelaunchSchedule attached (the D1 first
        // branch keys on RelaunchSchedule != null alone; the schedule's launch
        // list is irrelevant to the basis derivation).
        private static GhostPlaybackLogic.LoopUnit BuildZeroDriftUnit()
        {
            var sched = new MissionRelaunchSchedule(
                ut0: 0.0, anchorPeriod: 500.0,
                otherPeriods: null, otherTolerances: null,
                floorUT: 1000.0, lookaheadMultiples: 1000,
                minSpacingSeconds: 600.0);
            Assert.False(double.IsNaN(sched.FirstLaunchUT)); // fixture sanity
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1300.0,
                cadenceSeconds: 600.0, phaseAnchorUT: sched.FirstLaunchUT,
                overlapCadenceSeconds: 600.0, memberWindows: null,
                relaunchSchedule: sched);
        }

        // Re-aim: supported plan + valid synodic schedule (RelaunchSchedule null
        // BY CONSTRUCTION, mirroring the builder's re-aim branch). The synodic
        // cadence (3000) far exceeds the span (300), so the clock parks at
        // spanEnd between windows - the inter-body shape.
        internal static GhostPlaybackLogic.LoopUnit BuildReaimUnit(
            bool planSupported = true, bool scheduleValid = true,
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double synodicCadence = 3000.0, double phaseAnchorUT = 1000.0,
            string targetBody = "Duna")
        {
            var plan = new ReaimMissionPlan
            {
                Supported = planSupported,
                TargetBody = targetBody,
                Reason = planSupported ? null : "test-unsupported",
            };
            var sched = new ReaimWindowPlanner.ReaimWindowSchedule
            {
                Valid = scheduleValid,
                FirstDepartureUT = phaseAnchorUT + 50.0,
                SynodicPeriodSeconds = synodicCadence,
                PhaseAnchorUT = phaseAnchorUT,
                CadenceSeconds = synodicCadence,
            };
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: synodicCadence, phaseAnchorUT: phaseAnchorUT,
                overlapCadenceSeconds: synodicCadence, memberWindows: null,
                relaunchSchedule: null,
                reaimPlan: plan, reaimSchedule: sched);
        }

        private static Route BuildRoute(
            long lastObserved = -1, long windowAnchor = -1, bool markerEngaged = false,
            double recordedDockUT = 1150.0, int stops = 1)
        {
            var route = new Route
            {
                Id = "route-basis-test",
                Status = RouteStatus.Active,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = recordedDockUT,
                LastObservedLoopCycleIndex = lastObserved,
                WindowAnchorCycleIndex = windowAnchor,
                ReaimWindowBasisEngaged = markerEngaged,
                TransitDuration = 300.0,
                DispatchInterval = 300.0,
                Stops = new List<RouteStop>(),
            };
            for (int i = 0; i < stops; i++)
            {
                route.Stops.Add(new RouteStop
                {
                    Endpoint = new RouteEndpoint { VesselPersistentId = 42u + (uint)i },
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 10.0 } },
                    // Multi-stop shapes carry per-stop dock UTs; the single-stop
                    // shape leaves -1 (the codec sentinel) and falls back to the
                    // route-level dock in the transition applier.
                    RecordedDockUT = stops > 1 ? recordedDockUT + i * 50.0 : -1.0,
                });
            }
            return route;
        }

        // ==================================================================
        // D1 - DeriveWindowBasis fail-closed pairs
        // ==================================================================

        [Fact]
        public void DeriveWindowBasis_NullScheduleNoReaim_Flat()
        {
            Assert.Equal(RouteWindowBasis.FlatInterval,
                RouteLoopClock.DeriveWindowBasis(BuildFlatUnit()));
        }

        [Fact]
        public void DeriveWindowBasis_RelaunchSchedule_ZeroDrift()
        {
            Assert.Equal(RouteWindowBasis.ZeroDriftSchedule,
                RouteLoopClock.DeriveWindowBasis(BuildZeroDriftUnit()));
        }

        [Fact]
        public void DeriveWindowBasis_ValidSupportedReaim_ReaimWindows()
        {
            Assert.Equal(RouteWindowBasis.ReaimWindows,
                RouteLoopClock.DeriveWindowBasis(BuildReaimUnit()));
        }

        // catches: an INVALID schedule slipping through as windowed - the
        // fail-closed half of the D1 conjunction.
        [Fact]
        public void DeriveWindowBasis_InvalidReaimSchedule_Flat()
        {
            Assert.Equal(RouteWindowBasis.FlatInterval,
                RouteLoopClock.DeriveWindowBasis(BuildReaimUnit(scheduleValid: false)));
        }

        // catches: an UNSUPPORTED plan with a (stale) valid schedule classifying
        // windowed - the other fail-closed half.
        [Fact]
        public void DeriveWindowBasis_UnsupportedPlanValidSchedule_Flat()
        {
            Assert.Equal(RouteWindowBasis.FlatInterval,
                RouteLoopClock.DeriveWindowBasis(BuildReaimUnit(planSupported: false)));
        }

        // ==================================================================
        // D2 - ResolveResidualCadence
        // ==================================================================

        [Fact]
        public void ResolveResidualCadence_Flat_Off()
        {
            Assert.Equal(0, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.FlatInterval, 1));
            Assert.Equal(0, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.FlatInterval, 3));
        }

        // catches: the double-apply - N is already consumed Missions-side by the
        // zero-drift minSpacing throttle, so the residual MUST be 1 at every N.
        [Fact]
        public void ResolveResidualCadence_ZeroDrift_One()
        {
            Assert.Equal(1, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ZeroDriftSchedule, 1));
            Assert.Equal(1, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ZeroDriftSchedule, 2));
            Assert.Equal(1, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ZeroDriftSchedule, 7));
        }

        [Fact]
        public void ResolveResidualCadence_Reaim_N()
        {
            Assert.Equal(1, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ReaimWindows, 1));
            Assert.Equal(2, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ReaimWindows, 2));
            Assert.Equal(5, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ReaimWindows, 5));
            // Sub-floor input clamps to 1 (the ClampCadenceMultiplier invariant).
            Assert.Equal(1, RouteLoopClock.ResolveResidualCadence(RouteWindowBasis.ReaimWindows, 0));
        }

        // ==================================================================
        // D3 - IsDeliverableWindow
        // ==================================================================

        // catches: an unset anchor suppressing the first crossing - the first
        // owed crossing after creation / activation / rebase ALWAYS delivers
        // (the caller then adopts anchor = dockCycleIndex).
        [Fact]
        public void IsDeliverableWindow_AnchorAdoption_FirstWindowDeliverable()
        {
            Assert.True(RouteLoopClock.IsDeliverableWindow(0, -1, 2));
            Assert.True(RouteLoopClock.IsDeliverableWindow(7, -1, 3));
            // And the adopted window itself is deliverable ((A - A) % N == 0).
            Assert.True(RouteLoopClock.IsDeliverableWindow(7, 7, 3));
        }

        [Fact]
        public void IsDeliverableWindow_EveryNth()
        {
            // Anchor 4, N=2: windows 4, 6, 8 deliver; 5, 7 skip.
            Assert.True(RouteLoopClock.IsDeliverableWindow(4, 4, 2));
            Assert.False(RouteLoopClock.IsDeliverableWindow(5, 4, 2));
            Assert.True(RouteLoopClock.IsDeliverableWindow(6, 4, 2));
            Assert.False(RouteLoopClock.IsDeliverableWindow(7, 4, 2));
            Assert.True(RouteLoopClock.IsDeliverableWindow(8, 4, 2));
            // N=1 / 0 (off): every window deliverable.
            Assert.True(RouteLoopClock.IsDeliverableWindow(5, 4, 1));
            Assert.True(RouteLoopClock.IsDeliverableWindow(5, 4, 0));
        }

        // catches: C# remainder going negative on a window index below the
        // anchor (a re-baselined cursor can present one) and misreading a
        // deliverable window as skipped or vice versa. Euclidean modulo required.
        [Fact]
        public void IsDeliverableWindow_NegativeAndLargeIndices()
        {
            // Anchor 4, N=3: ... -2, 1, 4, 7 ... deliver.
            Assert.True(RouteLoopClock.IsDeliverableWindow(1, 4, 3));
            Assert.True(RouteLoopClock.IsDeliverableWindow(-2, 4, 3));
            Assert.False(RouteLoopClock.IsDeliverableWindow(0, 4, 3));
            Assert.False(RouteLoopClock.IsDeliverableWindow(-1, 4, 3));
            // Large indices stay exact (long arithmetic, no float drift).
            Assert.True(RouteLoopClock.IsDeliverableWindow(4L + 3L * 1_000_000_000L, 4, 3));
            Assert.False(RouteLoopClock.IsDeliverableWindow(5L + 3L * 1_000_000_000L, 4, 3));
        }

        // ==================================================================
        // D5 - ComputeHighestDeliverableWindow (warp rule)
        // ==================================================================

        // catches: a warp jump over K windows picking a non-deliverable window
        // or missing the highest deliverable one in (lastObserved, dock].
        [Fact]
        public void ComputeHighestDeliverableWindow_WarpJump_PicksHighestDeliverable()
        {
            // Anchor 0, N=2 (deliverable: 0, 2, 4, ...). lastObserved 0, warp to
            // dock 5: highest deliverable in (0, 5] is 4.
            Assert.Equal(4L, RouteLoopClock.ComputeHighestDeliverableWindow(0, 5, 0, 2));
            // Dock lands ON a deliverable window: picked directly.
            Assert.Equal(6L, RouteLoopClock.ComputeHighestDeliverableWindow(0, 6, 0, 2));
            // Modulo off / unset anchor: the newest window wins (existing warp
            // fire-once semantics / D3 adoption).
            Assert.Equal(5L, RouteLoopClock.ComputeHighestDeliverableWindow(0, 5, 0, 1));
            Assert.Equal(5L, RouteLoopClock.ComputeHighestDeliverableWindow(0, 5, -1, 4));
        }

        [Fact]
        public void ComputeHighestDeliverableWindow_NoneInRange_ReturnsNone()
        {
            // Anchor 0, N=2: only window 1 is owed -> none deliverable.
            Assert.Equal(RouteLoopClock.NoDeliverableWindow,
                RouteLoopClock.ComputeHighestDeliverableWindow(0, 1, 0, 2));
            // Nothing owed at all (dock <= lastObserved).
            Assert.Equal(RouteLoopClock.NoDeliverableWindow,
                RouteLoopClock.ComputeHighestDeliverableWindow(5, 5, 0, 2));
            Assert.Equal(RouteLoopClock.NoDeliverableWindow,
                RouteLoopClock.ComputeHighestDeliverableWindow(6, 5, 0, 1));
        }

        // ==================================================================
        // D6 - transition evaluator (pure) + applier (re-baselines + logs)
        // ==================================================================

        // catches (review C6, the blocker): after a transient decline leaves the
        // cursor in flat space (huge), a re-engage that resets ONLY the anchor
        // leaves every future synodic dockCycleIndex at or below the stale
        // cursor - TryGetOwedDockCrossing never emits again (permanent silent
        // skip). Engage MUST re-baseline the cursor into window space, and the
        // NEXT crossing must still be owed (no suppression).
        [Fact]
        public void EvaluateWindowBasisTransition_Engage_RebaselinesIntoWindowSpace_NoSuppress()
        {
            Assert.Equal(WindowBasisTransitionKind.Engage,
                RouteLoopClock.EvaluateWindowBasisTransition(false, RouteWindowBasis.ReaimWindows));

            // Stale FLAT-space cursor (large) from the decline interlude.
            Route route = BuildRoute(lastObserved: 5000, windowAnchor: 3, markerEngaged: false);
            var unit = BuildReaimUnit(); // window space: cadence 3000, anchor 1000

            // Tick inside window 2, dock phase passed (loopUT 1200 >= dock 1150).
            WindowBasisTransitionKind kind = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.ReaimWindows, loopUT: 1200.0, cycleIndex: 2, currentUT: 7300.0);

            Assert.Equal(WindowBasisTransitionKind.Engage, kind);
            Assert.True(route.ReaimWindowBasisEngaged);
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);
            // dockCycleIndex = 2 (dock passed) -> cursor re-baselined to 1:
            // exactly the CURRENT window (2) left owed.
            Assert.Equal(1L, route.LastObservedLoopCycleIndex);
            Assert.Equal(1L, route.Stops[0].LastFiredCycleIndex);

            // NoSuppress: the current window IS owed after the re-baseline.
            bool owedNow = RouteLoopClock.TryGetOwedDockCrossing(
                unit, 1200.0, 2, route.RecordedDockUT, route.LastObservedLoopCycleIndex,
                out RouteLoopClock.OwedDockCrossing owed);
            Assert.True(owedNow);
            Assert.Equal(2L, owed.DockCycleIndex);

            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("ENGAGED")
                && l.Contains("5000->1") && l.Contains("window space"));
        }

        // catches (the mis-fire regression, D6 decline): a stale window-space
        // cursor (small) compared against flat indices reads as a huge owed jump
        // and fires a delivery the player never scheduled. Decline must
        // re-baseline to the CURRENT flat index with NO crossing owed this tick.
        [Fact]
        public void EvaluateWindowBasisTransition_Decline_RebaselineNoFire()
        {
            Assert.Equal(WindowBasisTransitionKind.Decline,
                RouteLoopClock.EvaluateWindowBasisTransition(true, RouteWindowBasis.FlatInterval));

            // Window-space cursor (small) + engaged marker; the build declined,
            // so this tick's unit is flat: cycleIndex is a big flat index.
            Route route = BuildRoute(lastObserved: 2, windowAnchor: 2, markerEngaged: true);
            var unit = BuildFlatUnit();

            WindowBasisTransitionKind kind = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.FlatInterval, loopUT: 1200.0, cycleIndex: 4000, currentUT: 1201000.0);

            Assert.Equal(WindowBasisTransitionKind.Decline, kind);
            Assert.False(route.ReaimWindowBasisEngaged);
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);
            // dockCycleIndex = 4000 (dock passed at loopUT 1200) - consumed, NOT owed.
            Assert.Equal(4000L, route.LastObservedLoopCycleIndex);
            Assert.Equal(4000L, route.Stops[0].LastFiredCycleIndex);

            // NoFire: nothing is owed after the re-baseline.
            bool owedNow = RouteLoopClock.TryGetOwedDockCrossing(
                unit, 1200.0, 4000, route.RecordedDockUT, route.LastObservedLoopCycleIndex,
                out RouteLoopClock.OwedDockCrossing _);
            Assert.False(owedNow);

            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("DECLINED")
                && l.Contains("2->4000") && l.Contains("flat space"));
        }

        [Fact]
        public void EvaluateWindowBasisTransition_SteadyState_NoOp()
        {
            Assert.Equal(WindowBasisTransitionKind.None,
                RouteLoopClock.EvaluateWindowBasisTransition(false, RouteWindowBasis.FlatInterval));
            Assert.Equal(WindowBasisTransitionKind.None,
                RouteLoopClock.EvaluateWindowBasisTransition(false, RouteWindowBasis.ZeroDriftSchedule));
            Assert.Equal(WindowBasisTransitionKind.None,
                RouteLoopClock.EvaluateWindowBasisTransition(true, RouteWindowBasis.ReaimWindows));

            // Applier no-op: settled engaged route, no mutation, no transition log.
            Route route = BuildRoute(lastObserved: 3, windowAnchor: 2, markerEngaged: true);
            WindowBasisTransitionKind kind = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.ReaimWindows, loopUT: 1200.0, cycleIndex: 4, currentUT: 13300.0);
            Assert.Equal(WindowBasisTransitionKind.None, kind);
            Assert.Equal(3L, route.LastObservedLoopCycleIndex);
            Assert.Equal(2L, route.WindowAnchorCycleIndex);
            Assert.True(route.ReaimWindowBasisEngaged);
            Assert.DoesNotContain(logLines, l => l.Contains("ENGAGED") || l.Contains("DECLINED"));
        }

        // catches: a repeated decline tick re-running the re-baseline (the
        // second evaluation must see marker false + flat basis = None).
        [Fact]
        public void EvaluateWindowBasisTransition_DeclineTwice_Idempotent()
        {
            Route route = BuildRoute(lastObserved: 2, windowAnchor: 2, markerEngaged: true);

            WindowBasisTransitionKind first = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.FlatInterval, loopUT: 1200.0, cycleIndex: 4000, currentUT: 1201000.0);
            Assert.Equal(WindowBasisTransitionKind.Decline, first);
            long cursorAfterFirst = route.LastObservedLoopCycleIndex;

            logLines.Clear();
            WindowBasisTransitionKind second = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.FlatInterval, loopUT: 1250.0, cycleIndex: 4001, currentUT: 1201300.0);
            Assert.Equal(WindowBasisTransitionKind.None, second);
            Assert.Equal(cursorAfterFirst, route.LastObservedLoopCycleIndex);
            Assert.DoesNotContain(logLines, l => l.Contains("DECLINED"));
        }

        // catches (BUG-F class belt): a transition re-baseline running on a
        // cold-load UT=0 tick and destroying the persisted cursors.
        [Fact]
        public void ApplyWindowBasisTransition_ColdLoadUtZero_NoOp()
        {
            Route route = BuildRoute(lastObserved: 5000, windowAnchor: 3, markerEngaged: false);
            WindowBasisTransitionKind kind = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.ReaimWindows, loopUT: 1200.0, cycleIndex: 2, currentUT: 0.0);
            Assert.Equal(WindowBasisTransitionKind.None, kind);
            Assert.Equal(5000L, route.LastObservedLoopCycleIndex);
            Assert.Equal(3L, route.WindowAnchorCycleIndex);
            Assert.False(route.ReaimWindowBasisEngaged);
        }

        // Multi-stop transition: per-stop cursors re-baseline against each
        // stop's OWN dock phase (a stop whose dock has not yet passed this
        // window lands one lower), so no stop is left owed after a Decline.
        [Fact]
        public void ApplyWindowBasisTransition_MultiStop_PerStopDockPhaseExact()
        {
            // Stops at dock 1150 and 1200; tick at loopUT 1180: stop 0's dock
            // passed (cycle 2), stop 1's has not (cycle 1).
            Route route = BuildRoute(lastObserved: 2, windowAnchor: 2, markerEngaged: true,
                recordedDockUT: 1150.0, stops: 2);
            WindowBasisTransitionKind kind = RouteOrchestrator.ApplyWindowBasisTransition(
                route, RouteWindowBasis.FlatInterval, loopUT: 1180.0, cycleIndex: 2, currentUT: 7200.0);
            Assert.Equal(WindowBasisTransitionKind.Decline, kind);
            Assert.Equal(2L, route.Stops[0].LastFiredCycleIndex);  // dock 1150 passed
            Assert.Equal(1L, route.Stops[1].LastFiredCycleIndex);  // dock 1200 not yet
        }
    }
}
