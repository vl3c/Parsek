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
    /// (M5 P2) Pins the inter-body firing-path gate: the residual cadence
    /// modulo on the loop path (single-stop + multi-stop cMin), the D3 anchor
    /// adoption, the D4 skip semantics (marker-only, credit flush, no partner
    /// advance), the D5 warp rule, the D6 engage/decline re-baselines wired
    /// into <c>ProcessLoopRoute</c>, the C4 windowed cadence rebase, and the
    /// zero-drift / flat behavior-identity guards.
    ///
    /// <para><b>Real path, not the false-green fake (the M4a lesson).</b>
    /// <see cref="RouteOrchestrator.DeliveryApplierForTesting"/> stays null;
    /// these tests assign <see cref="RouteOrchestrator.DeliveryRowEmitterForTesting"/>,
    /// which the REAL <c>ApplyDelivery</c> consults AFTER its per-window ELS
    /// guard, so replay / suppression regressions go RED. The dispatch-debit
    /// half and both replay guards run for real.</para>
    ///
    /// <para><b>Required-RED regressions</b> (verified RED against the pre-fix
    /// shapes before green, per the plan):
    /// <c>DeclineThenReEngage_NextWindowStillFires</c> (Engage without the
    /// cursor re-baseline = permanent silent skip),
    /// <c>CadenceRebase_WindowedBasis_NoDuplicateDeliveryOfDeliveredWindow</c>
    /// (unconditional -1 rebase = duplicate delivery),
    /// <c>ModuloSkip_DoesNotAdvancePartnerAlternation</c> (skip advancing the
    /// alternation cursor), and
    /// <c>DeclineToFaithful_RebaselinesWithoutFire_ThenFlatFiresNormally</c>
    /// (no D6 evaluator = decline mis-fire).</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteInterBodyFireTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        // Reaim window geometry shared by most tests: span [1000, 1300] (300s),
        // synodic cadence 3000, phase anchor 1000, recorded dock 1150. Window k
        // spans [1000+3000k, 1300+3000k]; its dock phase passes at 1150+3000k;
        // the clock parks at spanEnd (1300-equivalent) in the inter-window gap.
        private const double SpanStart = 1000.0;
        private const double SpanEnd = 1300.0;
        private const double Synodic = 3000.0;
        private const double DockUT = 1150.0;

        public RouteInterBodyFireTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.ResetWindowBasisTransitionCountsForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers
        // ==================================================================

        private static GhostPlaybackLogic.LoopUnit BuildReaimUnit()
        {
            return RouteWindowBasisTests.BuildReaimUnit(
                spanStartUT: SpanStart, spanEndUT: SpanEnd,
                synodicCadence: Synodic, phaseAnchorUT: SpanStart);
        }

        private static GhostPlaybackLogic.LoopUnit BuildFlatUnit(double cadenceSeconds = 300.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: SpanStart, spanEndUT: SpanEnd,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: SpanStart);
        }

        // Zero-drift unit: real MissionRelaunchSchedule, launches at 1000, 2000,
        // 3000, ... (ut0=0, anchorPeriod=500, floor=1000, minSpacing=600 - the
        // throttle skips every other faithful window, mirroring how the
        // builder's minSpacing = cadence = DispatchInterval consumes N).
        private static GhostPlaybackLogic.LoopUnit BuildZeroDriftUnit()
        {
            var sched = new MissionRelaunchSchedule(
                ut0: 0.0, anchorPeriod: 500.0,
                otherPeriods: null, otherTolerances: null,
                floorUT: 1000.0, lookaheadMultiples: 100000,
                minSpacingSeconds: 600.0);
            Assert.Equal(1000.0, sched.FirstLaunchUT); // fixture sanity
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: SpanStart, spanEndUT: SpanEnd,
                cadenceSeconds: 1000.0, phaseAnchorUT: sched.FirstLaunchUT,
                overlapCadenceSeconds: 1000.0, memberWindows: null,
                relaunchSchedule: sched);
        }

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        // Real-path row emitter (the M4a lesson): ApplyDelivery's per-window ELS
        // guard runs for real; only the live-Vessel row emission is faked.
        private void InstallRealPathRowEmitter()
        {
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (route, currentUT, env, cycleId, stopIndex, bumpCompletedCycle) =>
                {
                    Ledger.AddAction(new GameAction
                    {
                        Type = GameActionType.RouteCargoDelivered,
                        UT = currentUT,
                        RouteId = route.Id,
                        RouteCycleId = cycleId,
                        RouteStopIndex = stopIndex,
                        Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                    });
                };
        }

        private static Route BuildReaimRoute(
            string id = "route-interbody",
            int cadenceMultiplier = 1,
            long lastObserved = -1,
            long windowAnchor = -1,
            bool markerEngaged = false,
            int completedCycles = 0,
            bool isKscOrigin = true)
        {
            return new Route
            {
                Id = id,
                Status = RouteStatus.Active,
                IsKscOrigin = isKscOrigin,
                BackingMissionTreeId = "tree-1", // IsLoopRoute true
                RecordedDockUT = DockUT,
                DockMemberRecordingId = "rec-dock",
                LoopAnchorUT = SpanStart,
                LastObservedLoopCycleIndex = lastObserved,
                WindowAnchorCycleIndex = windowAnchor,
                ReaimWindowBasisEngaged = markerEngaged,
                CadenceMultiplier = cadenceMultiplier,
                DispatchInterval = 300.0 * cadenceMultiplier,
                TransitDuration = 300.0,
                CompletedCycles = completedCycles,
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

        private static List<GameAction> Delivered() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoDelivered).ToList();

        private static List<GameAction> Dispatched() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched).ToList();

        // ==================================================================
        // N=1: every synodic window delivers on its dock phase
        // ==================================================================

        // catches: the M5 goal itself - delivery firing on the SAME rendered
        // synodic windows, once per window, unique cycleIds, plus the fresh
        // engage transition on the first windowed tick.
        [Fact]
        public void ReaimN1_FiresOncePerSynodicWindow_OnDockPhase()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1100.0, env); // window 0, PRE-dock -> nothing
            Assert.Empty(Delivered());

            RouteOrchestrator.Tick(DockUT, env); // window 0 dock phase -> fire
            Assert.Single(Delivered());
            Assert.True(route.ReaimWindowBasisEngaged); // D6 engage ran

            RouteOrchestrator.Tick(2000.0, env); // parked inter-window tail -> nothing
            Assert.Single(Delivered());

            RouteOrchestrator.Tick(DockUT + Synodic, env); // window 1 dock -> fire
            var delivered = Delivered();
            Assert.Equal(2, delivered.Count);
            Assert.Equal(2, Dispatched().Count);
            Assert.Equal(new[] { "cycle-0", "cycle-1" },
                delivered.Select(a => a.RouteCycleId).ToArray()); // unique, ordered
            Assert.Equal(1L, route.LastObservedLoopCycleIndex);
        }

        // ==================================================================
        // N=2: alternate windows deliver, anchored at the first fire
        // ==================================================================

        [Fact]
        public void ReaimN2_FiresAlternateWindows_AnchoredAtFirstFire()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0: adopt anchor + fire
            Assert.Single(Delivered());
            Assert.Equal(0L, route.WindowAnchorCycleIndex);
            Assert.Contains(logLines, l => l.Contains("window-anchor ADOPTED"));

            RouteOrchestrator.Tick(DockUT + Synodic, env); // window 1: modulo skip
            Assert.Single(Delivered()); // nothing new
            Assert.Equal(1L, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("SKIPPED by cadence modulo"));

            RouteOrchestrator.Tick(DockUT + 2 * Synodic, env); // window 2: fire
            Assert.Equal(2, Delivered().Count);
            Assert.Equal(2L, route.LastObservedLoopCycleIndex);
            Assert.Equal(0L, route.WindowAnchorCycleIndex); // anchor unchanged
        }

        // ==================================================================
        // D4: the skip advances the marker and NOTHING else
        // ==================================================================

        // catches: a modulo skip bumping SkippedCycles (would advance the
        // cycle-{C+S} id sequence and read as a failure), recording a hold,
        // emitting rows, or reserving escrow.
        [Fact]
        public void ModuloSkip_AdvancesMarkerOnly_NoSkippedCyclesNoHoldNoRows_NoEscrow()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0 fires (anchor 0)
            int rowsAfterFire = Ledger.Actions.Count;
            int completedAfterFire = route.CompletedCycles;

            RouteOrchestrator.Tick(DockUT + Synodic, env); // window 1: skip

            Assert.Equal(1L, route.LastObservedLoopCycleIndex);      // marker advanced
            Assert.Equal(0, route.SkippedCycles);                    // NO blocked-counter bump
            Assert.Equal(completedAfterFire, route.CompletedCycles); // no completion
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.None,
                route.LastHoldKind);                                 // NO hold
            Assert.Equal(rowsAfterFire, Ledger.Actions.Count);       // NO rows
            Assert.False(RouteStore.HasEscrow(route.Id));            // NO escrow
        }

        // catches (OQ3): the skip IS the "next crossing" for the previously
        // dispatched cycle - its pending recovery credit must flush on the skip
        // tick, not strand until the next delivered window a synodic later.
        [Fact]
        public void ModuloSkip_FlushesPendingRecoveryCredit()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2, isKscOrigin: true);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv { IsCareer = true };

            RouteOrchestrator.Tick(DockUT, env); // window 0 fires, arms the credit
            Assert.Equal("cycle-0", route.PendingRecoveryCreditCycleId);

            logLines.Clear();
            RouteOrchestrator.Tick(DockUT + Synodic, env); // window 1: skip -> flush

            // The flush ran on the skip crossing (zero-recovery branch here: no
            // recovery rows in the ledger, amount ELS-recomputed at flush time)
            // and cleared the pending marker either way.
            Assert.Null(route.PendingRecoveryCreditCycleId);
            Assert.Contains(logLines, l => l.Contains("EmitPendingRecoveryCredit")
                && l.Contains("cycle-0"));
            Assert.Contains(logLines, l => l.Contains("SKIPPED by cadence modulo"));
        }

        // REQUIRED-RED (review Missed #3): a modulo skip must advance neither
        // the round-trip partner alternation cursor nor the partner gate (which
        // reads partner.CompletedCycles - never bumped by a skip). RED shape:
        // calling AdvancePartnerAlternationOnDispatch from the skip block.
        [Fact]
        public void ModuloSkip_DoesNotAdvancePartnerAlternation()
        {
            var partner = BuildReaimRoute(id: "route-partner");
            partner.Status = RouteStatus.Paused; // partner does not tick-fire
            partner.CompletedCycles = 5;
            partner.LinkedRouteId = "route-interbody";
            RouteStore.AddRoute(partner);

            var route = BuildReaimRoute(
                cadenceMultiplier: 2, lastObserved: 0, windowAnchor: 0,
                markerEngaged: true, completedCycles: 1);
            route.LinkedRouteId = partner.Id;
            route.LastConsumedPartnerCycle = 1;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT + Synodic, env); // window 1: modulo skip

            Assert.Contains(logLines, l => l.Contains("SKIPPED by cadence modulo"));
            // Alternation cursor untouched by the skip.
            Assert.Equal(1, route.LastConsumedPartnerCycle);
            // And the partner's gate cannot have been satisfied by it: this
            // route's own CompletedCycles (what the partner's gate reads) did
            // not move either.
            Assert.Equal(1, route.CompletedCycles);
        }

        // ==================================================================
        // D5: warp over several windows
        // ==================================================================

        // catches: a warp tick spanning deliverable + skipped windows either
        // replaying each one, firing for a NON-deliverable window, or failing
        // to snap the marker fully forward.
        [Fact]
        public void WarpOverThreeWindows_N2_FiresOnceHighestDeliverable_SnapsForward()
        {
            var route = BuildReaimRoute(
                cadenceMultiplier: 2, lastObserved: 0, windowAnchor: 0,
                markerEngaged: true, completedCycles: 1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Warp from window 0 straight past windows 1, 2, 3 (dock 3 passed).
            RouteOrchestrator.Tick(DockUT + 3 * Synodic + 100.0, env);

            // Exactly ONE fire (for window 2, the highest deliverable under
            // anchor 0 / N=2 in (0, 3]); marker snapped to 3 (never replay).
            Assert.Single(Delivered());
            Assert.Equal(3L, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("highest deliverable window 2"));

            // The next deliverable window (4) still fires normally after the warp.
            RouteOrchestrator.Tick(DockUT + 4 * Synodic, env);
            Assert.Equal(2, Delivered().Count);
        }

        // ==================================================================
        // D6: decline-to-faithful + re-engage (the two required-RED guards)
        // ==================================================================

        // REQUIRED-RED (the decline mis-fire regression): without the D6
        // evaluator, a stale window-space cursor (small) against a flat
        // cycleIndex (huge) reads as an owed jump and fires a delivery the
        // player never scheduled on the decline tick. With D6 the decline
        // re-baselines with NO fire, then the flat clock fires normally.
        [Fact]
        public void DeclineToFaithful_RebaselinesWithoutFire_ThenFlatFiresNormally()
        {
            GhostPlaybackLogic.LoopUnit currentUnit = BuildReaimUnit();
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => currentUnit;
            InstallRealPathRowEmitter();
            var route = BuildReaimRoute(cadenceMultiplier: 1);
            RouteStore.AddRoute(route);
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0 fires (engaged)
            Assert.Single(Delivered());
            Assert.True(route.ReaimWindowBasisEngaged);

            // The build declines (e.g. degraded bodyInfo): unit is now FLAT.
            currentUnit = BuildFlatUnit(cadenceSeconds: 300.0);
            // Flat tick deep in flat index space: cycle 4000, loopUT 1000 (<dock)
            // -> dockCycleIndex 3999.
            RouteOrchestrator.Tick(1201000.0, env);

            Assert.Single(Delivered()); // NO fire on the decline tick
            Assert.False(route.ReaimWindowBasisEngaged);
            Assert.Equal(3999L, route.LastObservedLoopCycleIndex);
            Assert.Contains(logLines, l => l.Contains("DECLINED") && l.Contains("flat space"));

            // The honest faithful fallback then fires on the flat clock.
            RouteOrchestrator.Tick(1201150.0, env); // flat cycle 4000 dock phase
            Assert.Equal(2, Delivered().Count);
        }

        // REQUIRED-RED (review C6, the blocker): a transient decline leaves the
        // cursor at a huge flat-space number; a re-engage that resets only the
        // anchor (the pre-review D6 shape) leaves every future synodic
        // dockCycleIndex at or below it - TryGetOwedDockCrossing never emits
        // again and the route silently never delivers. Engage MUST re-baseline
        // the cursor into window space; the next window then fires.
        [Fact]
        public void DeclineThenReEngage_NextWindowStillFires()
        {
            GhostPlaybackLogic.LoopUnit currentUnit = BuildReaimUnit();
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => currentUnit;
            InstallRealPathRowEmitter();
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0 fires (anchor 0 adopted)
            Assert.Single(Delivered());

            // Transient decline (one degraded build): flat tick pollutes the
            // cursor into flat space (3999).
            currentUnit = BuildFlatUnit(cadenceSeconds: 300.0);
            RouteOrchestrator.Tick(1201000.0, env);
            Assert.Equal(3999L, route.LastObservedLoopCycleIndex);

            // The verdict recovers: re-engage, then the next synodic window
            // (401 in window space at this UT) must still fire.
            currentUnit = BuildReaimUnit();
            RouteOrchestrator.Tick(DockUT + 401 * Synodic, env); // ut 1204150

            Assert.Equal(2, Delivered().Count); // the window DID fire
            Assert.True(route.ReaimWindowBasisEngaged);
            Assert.Contains(logLines, l => l.Contains("ENGAGED")
                && l.Contains("3999->400") && l.Contains("window space"));
            Assert.Equal(401L, route.LastObservedLoopCycleIndex);
            Assert.Equal(401L, route.WindowAnchorCycleIndex); // re-anchored at the fire
        }

        // catches: the engage transition suppressing the very first crossing of
        // a fresh windowed route (the activate-reset contract tolerates one
        // immediate fire; engage must not eat it).
        [Fact]
        public void EngageFromFresh_FirstCrossingDelivers()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env);

            Assert.Single(Delivered());
            Assert.True(route.ReaimWindowBasisEngaged);
            Assert.Equal(0L, route.WindowAnchorCycleIndex); // adopted at the fire
            Assert.Contains(logLines, l => l.Contains("ENGAGED"));
        }

        // ==================================================================
        // D3 reset sites: Activate + cadence rebase
        // ==================================================================

        [Fact]
        public void Activate_ResetsAnchor()
        {
            var route = BuildReaimRoute(
                lastObserved: 7, windowAnchor: 5, markerEngaged: true);
            route.Status = RouteStatus.Paused;
            RouteStore.AddRoute(route);

            Assert.True(RouteOrchestrator.TryActivate(route, 5000.0));

            Assert.Equal(-1L, route.LastObservedLoopCycleIndex);
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);
        }

        // catches: a cadence edit leaving a stale anchor (the modulo phase must
        // re-anchor at the next fire, not keep the pre-edit offset).
        [Fact]
        public void CadenceRebase_ResetsAnchor_NextFireReanchors()
        {
            var route = BuildReaimRoute(
                cadenceMultiplier: 2, lastObserved: 2, windowAnchor: 0,
                markerEngaged: true, completedCycles: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Mid-gap after window 2 (parked tail: dockCycleIndex 2).
            Assert.True(RouteCadence.ApplyMultiplier(route, 3, 7500.0));
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);
            Assert.Equal(2L, route.LastObservedLoopCycleIndex); // windowed rebase kept

            RouteOrchestrator.Tick(DockUT + 3 * Synodic, env); // window 3
            Assert.Single(Delivered());
            Assert.Equal(3L, route.WindowAnchorCycleIndex); // re-anchored + delivered
        }

        // REQUIRED-RED (review C4): an N edit mid-gap under the windowed basis
        // must NOT re-deliver the already-delivered window under a fresh
        // cycle-{C+S} id (the ELS backstop cannot suppress a fresh id; the
        // ghost does not re-fly the window; the next real window is a synodic
        // away). RED shape: the historical unconditional -1 reset.
        [Fact]
        public void CadenceRebase_WindowedBasis_NoDuplicateDeliveryOfDeliveredWindow()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0 delivered (cycle-0)
            Assert.Single(Delivered());

            // Cadence edit mid-gap (parked tail of window 0).
            Assert.True(RouteCadence.ApplyMultiplier(route, 2, 2000.0));
            Assert.Contains(logLines, l => l.Contains("WINDOWED rebase"));

            // Still in window 0's tail: the delivered window must stay consumed.
            RouteOrchestrator.Tick(2100.0, env);
            Assert.Single(Delivered()); // NO duplicate

            // The next real window still delivers (re-anchoring via adoption).
            RouteOrchestrator.Tick(DockUT + Synodic, env);
            var delivered = Delivered();
            Assert.Equal(2, delivered.Count);
            Assert.Equal(delivered.Select(a => a.RouteCycleId).Distinct().Count(),
                delivered.Count); // unique cycleIds
        }

        // ==================================================================
        // Save/reload mid-gap + the ELS backstop
        // ==================================================================

        // catches: the persisted windowed state (cursor + anchor + marker) not
        // surviving the codec round-trip (double-fire after reload), and the
        // dispatch-keyed ELS backstop failing on a full route-state loss that
        // re-presents an already-fired cycleId.
        [Fact]
        public void SaveReload_MidSynodicGap_NoDoubleFire()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(DockUT, env); // window 0 fires (anchor 0)
            Assert.Single(Delivered());

            // Serialize -> deserialize mid-gap; the windowed state round-trips.
            var node = new ConfigNode("ROUTE");
            route.SerializeInto(node);
            Route reloaded = Route.DeserializeFrom(node);
            Assert.NotNull(reloaded);
            Assert.Equal(0L, reloaded.WindowAnchorCycleIndex);
            Assert.True(reloaded.ReaimWindowBasisEngaged);
            Assert.Equal(0L, reloaded.LastObservedLoopCycleIndex);

            RouteStore.ResetForTesting();
            RouteStore.AddRoute(reloaded);

            RouteOrchestrator.Tick(2000.0, env); // parked tail -> nothing
            Assert.Single(Delivered());

            // Full route-state loss (stale reload): everything back to fresh
            // defaults, SAME cycleId re-presented -> the dispatch-keyed ELS
            // backstop suppresses the re-emit; the marker snaps forward.
            var stale = BuildReaimRoute(cadenceMultiplier: 2); // C=0, S=0, cursor -1
            RouteStore.ResetForTesting();
            RouteStore.AddRoute(stale);
            RouteOrchestrator.Tick(2150.0, env); // parked tail, dockCycle 0 owed again

            Assert.Single(Delivered()); // backstop held
            Assert.Single(Dispatched());
            Assert.Contains(logLines, l => l.Contains("already in ledger"));
        }

        // ==================================================================
        // D10: multi-stop cMin modulo gate
        // ==================================================================

        // catches: a non-deliverable cMin dispatching / reserving escrow /
        // bumping SkippedCycles, failing to snap ALL stops atomically, or
        // stalling the same-tick catch-up to a deliverable later cycle.
        // (A KSC delivery-only route reserves no escrow on ANY path; the
        // load-bearing escrow assert is that the skip pass adds none.)
        [Fact]
        public void MultiStop_CMinNotDeliverable_AtomicSnapNoDispatchNoEscrow_StillDueContinues()
        {
            var route = BuildReaimRoute(
                cadenceMultiplier: 2, lastObserved: 0, windowAnchor: 0,
                markerEngaged: true, completedCycles: 1);
            // Two stops: dock A 1150 (stop 0), dock B 1200 (stop 1).
            route.Stops = new List<RouteStop>
            {
                new RouteStop
                {
                    Endpoint = new RouteEndpoint { VesselPersistentId = 42u },
                    DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 100.0 } },
                    RecordedDockUT = 1150.0,
                    LastFiredCycleIndex = 0,
                },
                new RouteStop
                {
                    Endpoint = new RouteEndpoint { VesselPersistentId = 43u },
                    DeliveryManifest = new Dictionary<string, double> { { "Oxidizer", 120.0 } },
                    RecordedDockUT = 1200.0,
                    LastFiredCycleIndex = 0,
                },
            };
            route.RecordedDockUT = 1200.0; // route-level pair keys the LAST dock
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildReaimUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Tick between dock A and dock B of window 2 (loopUT 1180): stop 0
            // owes cycle 2, stop 1 owes cycle 1 -> cMin = 1, NOT deliverable
            // (anchor 0, N=2) -> atomic snap through 1, stillDue -> the SAME
            // tick's catch-up pass processes cycle 2 (deliverable): dispatch +
            // window A fire; window B (dock 1200) is not yet due.
            RouteOrchestrator.Tick(SpanStart + 2 * Synodic + 180.0, env); // ut 7180

            Assert.Contains(logLines, l => l.Contains("SKIPPED by cadence modulo")
                && l.Contains("window 1"));
            Assert.Equal(0, route.SkippedCycles);          // D4: no blocked-counter bump
            Assert.False(RouteStore.HasEscrow(route.Id));  // skip reserved nothing
            Assert.Single(Dispatched());                   // cycle 2's dispatch (catch-up ran)
            var deliveredMid = Delivered();
            Assert.Single(deliveredMid);                   // window A only
            Assert.Equal(0, deliveredMid[0].RouteStopIndex);
            Assert.Equal(1L, route.Stops[1].LastFiredCycleIndex); // atomic snap through cMin

            // Window B fires at its own dock phase on a later tick, completing
            // the cycle exactly once.
            RouteOrchestrator.Tick(SpanStart + 2 * Synodic + 210.0, env); // loopUT 1210
            var delivered = Delivered();
            Assert.Equal(2, delivered.Count);
            Assert.Equal(1, delivered[1].RouteStopIndex);
            Assert.Equal(delivered[0].RouteCycleId, delivered[1].RouteCycleId); // one cycleId
            Assert.Equal(2, route.CompletedCycles); // bumped once for the fired cycle
        }

        // ==================================================================
        // Behavior-identity guards: zero-drift + flat
        // ==================================================================

        // REQUIRED-RED (the D2 double-apply guard): on the zero-drift path N is
        // already consumed Missions-side (minSpacing throttles the schedule;
        // sIdx indexes the THROTTLED list), so at N=2 delivery must fire on
        // EVERY scheduled launch - a route-side modulo here would halve it.
        [Fact]
        public void ZeroDriftSchedule_NResidualOne_FiresEveryScheduledLaunch_NoModulo()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 2);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildZeroDriftUnit()); // launches 1000, 2000, 3000
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // launch 0 (sIdx 0) dock phase
            RouteOrchestrator.Tick(2150.0, env); // launch 1 (sIdx 1) dock phase
            RouteOrchestrator.Tick(3150.0, env); // launch 2 (sIdx 2) dock phase

            Assert.Equal(3, Delivered().Count); // EVERY scheduled launch delivered
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);  // modulo never engaged
            Assert.False(route.ReaimWindowBasisEngaged);       // no basis transition
            Assert.DoesNotContain(logLines, l => l.Contains("SKIPPED by cadence modulo"));
            Assert.DoesNotContain(logLines, l => l.Contains("window-anchor ADOPTED"));
        }

        // catches (the behavior-identical-off pin): a flat null-schedule route
        // never evaluates the modulo branch, adopts no anchor, flips no marker,
        // and logs none of the M5 lines - at ANY CadenceMultiplier.
        [Fact]
        public void FlatRoute_PathUnchanged()
        {
            var route = BuildReaimRoute(cadenceMultiplier: 3);
            route.DispatchInterval = 900.0;
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildFlatUnit(cadenceSeconds: 900.0));
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // flat cycle 0 dock phase -> fire
            RouteOrchestrator.Tick(2050.0, env); // flat cycle 1 dock phase -> fire

            Assert.Equal(2, Delivered().Count);
            Assert.Equal(-1L, route.WindowAnchorCycleIndex);
            Assert.False(route.ReaimWindowBasisEngaged);
            Assert.DoesNotContain(logLines, l => l.Contains("SKIPPED by cadence modulo"));
            Assert.DoesNotContain(logLines, l => l.Contains("window-anchor ADOPTED"));
            Assert.DoesNotContain(logLines, l => l.Contains("ENGAGED") || l.Contains("DECLINED"));
        }
    }
}
