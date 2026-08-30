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
    /// Pins the armed-pause honor on a BLOCKED loop cycle (the "Send Once turned
    /// into an endless cycle" defect).
    ///
    /// <para><b>The observed defect.</b> A single-stop KSC rover supply route was
    /// armed with Send Once; the cycle crossed, eligibility failed
    /// (<c>DestinationFull</c> - the previous delivery had filled the destination's
    /// inventory slots), and the blocked branch consumed the cycle (SkippedCycles+1,
    /// crossing index snapped) and returned. The route stayed <b>Active with
    /// PauseAfterCurrentCycle STILL armed</b>: the ghost looped forever, and the
    /// live arm would have silently delivered at an arbitrary future crossing the
    /// moment the destination freed up. The delivered path
    /// (<c>delivered-then-paused</c>) had always honored the arm; only the blocked
    /// branches did not.</para>
    ///
    /// <para>Harness mirrors <see cref="RouteLoopDeliveryFireTests"/> (loop-unit
    /// resolver + fake delivery applier seams) and
    /// <see cref="RouteMultiStopFireTests"/> for the multi-stop half. The screen
    /// message is asserted through the existing
    /// <see cref="ParsekLog.ScreenMessageSinkForTesting"/> seam, so the toast is
    /// verified headlessly rather than only its text builder.</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteSendOnceBlockedPauseTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> screenMessages = new List<string>();

        public RouteSendOnceBlockedPauseTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ScreenMessageSinkForTesting = (msg, dur) => screenMessages.Add(msg);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            logLines.Clear();
            screenMessages.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers (mirrors RouteLoopDeliveryFireTests / RouteMultiStopFireTests)
        // ==================================================================

        // span [1000, 1300] (300s); cadence == span -> one crossing == one cycle.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1300.0,
            double cadenceSeconds = 300.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        // span [1000, 1400]; docks 1150 (stop 0) / 1300 (stop 1).
        private static GhostPlaybackLogic.LoopUnit BuildMultiUnit()
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1400.0,
                cadenceSeconds: 400.0, phaseAnchorUT: 1000.0);
        }

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        private void InstallFakeDeliveryApplier()
        {
            RouteOrchestrator.DeliveryApplierForTesting = (route, currentUT, env) =>
            {
                string cycleId = "cycle-" + (route.CompletedCycles + route.SkippedCycles).ToString(IC);
                Ledger.AddAction(new GameAction
                {
                    Type = GameActionType.RouteCargoDelivered,
                    UT = currentUT,
                    RouteId = route.Id,
                    RouteCycleId = cycleId,
                    RouteStopIndex = 0,
                    Sequence = 0,
                });
                route.CompletedCycles += 1;
                route.PendingDeliveryUT = null;
                route.PendingStopIndex = -1;
                route.TransitionTo(RouteStatus.Active, "delivered-loop-fake");
            };
        }

        private static Route BuildLoopRoute(
            string id = "route-sendonce",
            string name = "Rover Supply",
            RouteStatus status = RouteStatus.Active,
            bool isKscOrigin = false,
            long lastObservedLoopCycleIndex = -1)
        {
            return new Route
            {
                Id = id,
                Name = name,
                Status = status,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = "tree-1", // makes IsLoopRoute true
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
                DispatchInterval = 300.0,
                TransitDuration = 300.0,
                CadenceMultiplier = 1,
                CostManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private static Route Build2StopRoute(string id = "route-sendonce-multi")
        {
            return new Route
            {
                Id = id,
                Name = "Two Stop Run",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-b",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                CadenceMultiplier = 1,
                CostManifest = new Dictionary<string, double>
                {
                    { "LiquidFuel", 100.0 },
                    { "Oxidizer", 120.0 },
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                        DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 120.0 } },
                        SegmentIndexBefore = 1,
                        RecordedDockUT = 1300.0,
                        LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-b", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        private sealed class EligibleEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // Reproduces the flight's hold verbatim: the destination's inventory slots
        // are full of the stored part the first delivery placed there.
        private sealed class DestinationFullEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public string FullToken { get; set; } = "stored-part:evaScienceKit";
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = string.Empty; return true; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = FullToken; return false; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // (a) Single-stop: send-once armed + blocked cycle -> Paused
        // ==================================================================

        // catches THE defect: the blocked branch consuming the armed cycle but
        // leaving the route Active with PauseAfterCurrentCycle still set (endless
        // ghost loop + a live arm that fires at some arbitrary later crossing).
        [Fact]
        public void SendOnceArmed_BlockedCycle_PausesRoute_ClearsFlags_KeepsHold()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            Assert.True(RouteOrchestrator.TrySendOneCycleNow(route, 1100.0));
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.True(route.SendOnceArmed);

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());

            // The cycle was consumed as blocked...
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.CompletedCycles);
            Assert.Equal(0, route.LastObservedLoopCycleIndex);
            // ...NOTHING was emitted beyond the pause marker (no dispatch/debit/delivery)...
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteDispatched);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDebited);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            // ...and the armed pause was honored.
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.False(route.SendOnceArmed);
            // The hold survives so the Logistics window still names WHY it blocked.
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, route.LastHoldKind);
            Assert.Equal("stored-part:evaScienceKit", route.LastHoldDetail);
            Assert.Equal(1150.0, route.LastHoldUT);
            Assert.Contains(logLines, l =>
                l.Contains("[Route]") && l.Contains("ArmedPause") && l.Contains("blocked-then-paused"));
        }

        // catches: the pause not landing on the timeline (a RoutePaused row is what
        // a later rewind retires; the delivered path emits one, so must this).
        [Fact]
        public void SendOnceArmed_BlockedCycle_EmitsRoutePausedMarker()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            RouteOrchestrator.TrySendOneCycleNow(route, 1100.0);

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());

            var paused = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(paused);
            Assert.Equal(route.Id, paused.RouteId);
            Assert.Equal(1150.0, paused.UT);
            Assert.Equal("blocked-then-paused", paused.RouteEndpointReason);
        }

        // catches the player-visible half of the report: a send-once run that
        // resolves without delivering saying nothing on screen.
        [Fact]
        public void SendOnceArmed_BlockedCycle_PostsScreenMessageNamingTheHold()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            RouteOrchestrator.TrySendOneCycleNow(route, 1100.0);

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());

            string toast = Assert.Single(screenMessages);
            Assert.Contains("Rover Supply", toast);
            Assert.Contains("evaScienceKit", toast);
            Assert.Contains("Paused", toast);
        }

        // catches the reported symptom directly: after the blocked cycle the route
        // must go QUIET - no further crossings processed, no late delivery when the
        // destination frees up, because the arm is gone and the route is Paused.
        [Fact]
        public void SendOnceArmed_BlockedCycle_StopsLooping_NoLateDelivery()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            RouteOrchestrator.TrySendOneCycleNow(route, 1100.0);

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());
            // The block clears; several later crossings go by.
            var eligible = new EligibleEnv();
            RouteOrchestrator.Tick(1450.0, eligible); // cycle 1 dock phase
            RouteOrchestrator.Tick(1750.0, eligible); // cycle 2 dock phase
            RouteOrchestrator.Tick(2050.0, eligible); // cycle 3 dock phase

            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.Equal(0, route.CompletedCycles);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
        }

        // NEGATIVE CONTROL: an UNARMED route must keep its pre-fix behavior on a
        // blocked cycle (stay Active, keep looping, emit no pause marker). Without
        // this the fix could silently pause every blocked loop route.
        [Fact]
        public void Unarmed_BlockedCycle_StaysActive_EmitsNoPauseMarker()
        {
            var route = BuildLoopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());

            Assert.Equal(RouteStatus.Active, route.Status);
            Assert.Equal(1, route.SkippedCycles);
            Assert.Empty(Ledger.Actions);
            Assert.Empty(screenMessages);
        }

        // ==================================================================
        // (b) Multi-stop: the same contract on the per-window path
        // ==================================================================

        // catches: the multi-stop blocked branch (which skips the WHOLE cMin cycle
        // atomically) leaving the arm live. Also pins that the catch-up loop stops:
        // a paused route must not have its later owed cycles processed this tick.
        [Fact]
        public void SendOnceArmed_BlockedCycle_MultiStop_PausesRoute_ClearsFlags_KeepsHold()
        {
            var route = Build2StopRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildMultiUnit());
            RouteOrchestrator.TrySendOneCycleNow(route, 1100.0);

            // ut 1300 == dock B phase of cycle 0: both windows are due, and the
            // cycle is NOT yet dispatched -> the eligibility gate runs and blocks.
            RouteOrchestrator.Tick(1300.0, new DestinationFullEnv());

            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(0, route.CompletedCycles);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteDispatched);
            Assert.DoesNotContain(Ledger.Actions, a => a.Type == GameActionType.RouteCargoDelivered);
            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.False(route.SendOnceArmed);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, route.LastHoldKind);
            Assert.Equal("stored-part:evaScienceKit", route.LastHoldDetail);
            var paused = Ledger.Actions.SingleOrDefault(a => a.Type == GameActionType.RoutePaused);
            Assert.NotNull(paused);
            Assert.Equal("blocked-then-paused", paused.RouteEndpointReason);
            Assert.Contains(logLines, l => l.Contains("LoopRoute(multi)") && l.Contains("BLOCKED"));
        }

        // ==================================================================
        // (c) TryPause-armed (InTransit) provenance
        // ==================================================================

        // catches: only the Send Once provenance being honored. A player who hit
        // Pause during an in-flight cycle asked to pause; a blocked cycle COMPLETES
        // that request (it is the cycle finishing, badly), so the route must land
        // in Paused - but with no send-once toast (nobody is watching for a run).
        [Fact]
        public void PauseArmedWhileInTransit_BlockedCycle_PausesRoute_WithoutToast()
        {
            var route = BuildLoopRoute(status: RouteStatus.InTransit);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();
            // Arm through the real TryPause InTransit branch (sets the flag, leaves
            // SendOnceArmed false) rather than by poking the field.
            Assert.True(RouteOrchestrator.TryPause(route, 1100.0, new EligibleEnv()));
            Assert.True(route.PauseAfterCurrentCycle);
            Assert.False(route.SendOnceArmed);

            RouteOrchestrator.Tick(1150.0, new DestinationFullEnv());

            Assert.Equal(RouteStatus.Paused, route.Status);
            Assert.False(route.PauseAfterCurrentCycle);
            Assert.Equal(1, route.SkippedCycles);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull, route.LastHoldKind);
            // No screen message: this arm was not a "Send Once" run.
            Assert.Empty(screenMessages);
            Assert.Contains(logLines, l =>
                l.Contains("ArmedPause") && l.Contains("armedBy=pause-after-cycle"));
        }

        // ==================================================================
        // (e) Cadence is deterministic and never mutated by the tick loop
        // ==================================================================

        // catches: any orchestrator tick path silently rewriting the cadence /
        // interval of a route it is processing. Cadence is a PLAYER-ONLY input
        // (RouteCadence.ApplyMultiplier from the Logistics window, or
        // RouteBuilder at creation); an observed 1x->2x->1x->2x sequence in a log
        // is three clicks, and nothing else may ever produce one.
        [Fact]
        public void TickLoop_NeverMutatesCadenceOrInterval_PausedOrActive()
        {
            var paused = BuildLoopRoute(id: "route-paused", status: RouteStatus.Paused);
            var active = BuildLoopRoute(id: "route-active");
            RouteStore.AddRoute(paused);
            RouteStore.AddRoute(active);
            InstallUnitResolver(BuildUnit());
            InstallFakeDeliveryApplier();

            double pausedInterval = paused.DispatchInterval;
            double activeInterval = active.DispatchInterval;
            var env = new DestinationFullEnv();
            for (int i = 0; i < 12; i++)
                RouteOrchestrator.Tick(1150.0 + i * 50.0, env);

            Assert.Equal(1, paused.CadenceMultiplier);
            Assert.Equal(pausedInterval, paused.DispatchInterval);
            Assert.Equal(1, active.CadenceMultiplier);
            Assert.Equal(activeInterval, active.DispatchInterval);
            Assert.DoesNotContain(logLines, l => l.Contains("RouteCadence:"));
        }

        // catches: a same-N apply (e.g. a re-entrant UI commit, or a stepper click
        // already at the floor) rebasing the loop clock / rewriting the interval.
        // The no-op guard is what makes repeated applies idempotent instead of a
        // source of clock churn.
        [Fact]
        public void ApplyMultiplier_SameN_IsNoOp_NoRebase_NoIntervalRewrite()
        {
            var route = BuildLoopRoute();
            route.LastObservedLoopCycleIndex = 7;
            route.WindowAnchorCycleIndex = 3;
            double before = route.DispatchInterval;

            Assert.False(RouteCadence.ApplyMultiplier(route, 1, 1150.0));

            Assert.Equal(1, route.CadenceMultiplier);
            Assert.Equal(before, route.DispatchInterval);
            Assert.Equal(7, route.LastObservedLoopCycleIndex); // no rebase
            Assert.Equal(3, route.WindowAnchorCycleIndex);
            Assert.DoesNotContain(logLines, l => l.Contains("RouteCadence:") && l.Contains("rebase"));
        }

        // catches: a non-deterministic interval derivation (the value the flat loop
        // clock keys on). Same inputs -> bit-identical interval, every time.
        [Fact]
        public void DeriveDispatchInterval_IsDeterministic()
        {
            const double span = 45.039999999959093; // the flight's real span
            double first = RouteCadence.DeriveDispatchInterval(2, span);
            for (int i = 0; i < 25; i++)
                Assert.Equal(first, RouteCadence.DeriveDispatchInterval(2, span));
            Assert.Equal(2.0 * span, first);
            Assert.Equal(span, RouteCadence.DeriveDispatchInterval(1, span));
        }

        // ==================================================================
        // Pure message builders
        // ==================================================================

        [Fact]
        public void BlockedMessage_NamesRouteAndHoldInPlayerLanguage()
        {
            string msg = RouteSendOncePresentation.BuildBlockedMessage(
                "Rover Supply",
                RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull,
                "stored-part:evaScienceKit",
                0.0);

            Assert.Contains("Rover Supply", msg);
            Assert.Contains("evaScienceKit", msg);
            Assert.Contains("Paused", msg);
            // Same wording the Logistics detail panel shows for this hold.
            Assert.Contains(
                LogisticsHoldPresentation.DescribeHold(
                    RouteDispatchEvaluator.EligibilityFailureKind.DestinationFull,
                    "stored-part:evaScienceKit", 0.0),
                msg);
        }

        [Fact]
        public void BlockedMessage_FundsShortfall_UsesInvariantCulture()
        {
            string msg = RouteSendOncePresentation.BuildBlockedMessage(
                "Ore Run", RouteDispatchEvaluator.EligibilityFailureKind.FundsShort,
                "funds-short", 1234.0);

            Assert.Contains("1234", msg);
            Assert.DoesNotContain("1.234", msg);
            Assert.DoesNotContain("1,234", msg);
        }

        [Fact]
        public void DeliveredMessage_UsesCountFormAndSingularPlural()
        {
            string one = RouteSendOncePresentation.BuildDeliveredMessage("Rover Supply", 1, 1, false);
            Assert.Contains("1 resource line", one);
            Assert.DoesNotContain("1 resource lines", one);
            Assert.Contains("1 item", one);
            Assert.DoesNotContain("1 items", one);

            string many = RouteSendOncePresentation.BuildDeliveredMessage("Rover Supply", 2, 0, false);
            Assert.Contains("2 resource lines", many);
            Assert.Contains("0 items", many);
            Assert.Contains("Rover Supply", many);
            Assert.Contains("Paused", many);
            Assert.DoesNotContain("PARTIAL", many);
        }

        [Fact]
        public void DeliveredMessage_PartialIsFlagged()
        {
            string msg = RouteSendOncePresentation.BuildDeliveredMessage("Rover Supply", 1, 0, true);
            Assert.Contains("PARTIAL", msg);
        }

        [Fact]
        public void Messages_BlankRouteName_RenderUnnamedNeverEmptyQuotes()
        {
            Assert.Contains("'unnamed'",
                RouteSendOncePresentation.BuildDeliveredMessage("   ", 1, 0, false));
            Assert.Contains("'unnamed'",
                RouteSendOncePresentation.BuildBlockedMessage(
                    null, RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, "LiquidFuel", 0.0));
        }
    }
}
