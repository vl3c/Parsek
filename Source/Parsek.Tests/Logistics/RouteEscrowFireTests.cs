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
    /// M4b Phase B3 (plan D10/D11 / OQ7): pins the per-window SOURCE-DEBIT firing +
    /// the reserve / debit / release ESCROW lifecycle on the REAL
    /// <see cref="RouteOrchestrator.ProcessMultiStopCrossings"/> /
    /// <see cref="RouteOrchestrator.EmitPickupHalf"/> path (through the production
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/>), using the
    /// A3 <see cref="RouteOrchestrator.PickupDebitApplierForTesting"/> seam to verify
    /// the source debit + escrow without a live Vessel.
    ///
    /// <para><b>The leak-free invariant under test (the keystone):</b> every Reserve is
    /// matched by a Release (a physical debit OR a window-skip) within the cycle, or a
    /// Drop on tombstone / scene-change. RESERVE fires once at dispatch (the per-pid
    /// SUMMED amount); each pickup window RELEASEs its OWN portion at its debit phase,
    /// so the source reservation nets to zero by cycle end. A PARTIAL cycle keeps the
    /// un-fired window's reservation LIVE across the gap (a competing route's B1 gate
    /// sees it).</para>
    ///
    /// <para>The escrow is a static map (shared state), so this class is
    /// <c>[Collection("Sequential")]</c> and resets <see cref="RouteStore"/> +
    /// <see cref="Ledger"/> in the ctor + Dispose; a leaked reservation would corrupt a
    /// later test in the collection (which is itself one of the things B2/B3 guard).</para>
    /// </summary>
    [Collection("Sequential")]
    public class RouteEscrowFireTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteEscrowFireTests()
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
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            logLines.Clear();
        }

        public void Dispose()
        {
            RouteOrchestrator.LoopUnitResolverForTesting = null;
            RouteOrchestrator.DeliveryApplierForTesting = null;
            RouteOrchestrator.DeliveryRowEmitterForTesting = null;
            RouteOrchestrator.OriginDebitApplierForTesting = null;
            RouteOrchestrator.PickupDebitApplierForTesting = null;
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ==================================================================
        // Seam helpers
        // ==================================================================

        // span [1000, 1400] (400s), cadence == span -> one crossing window per cycle.
        private static GhostPlaybackLogic.LoopUnit BuildUnit(
            double spanStartUT = 1000.0, double spanEndUT = 1400.0,
            double cadenceSeconds = 400.0, double phaseAnchorUT = 1000.0)
        {
            return new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: spanStartUT, spanEndUT: spanEndUT,
                cadenceSeconds: cadenceSeconds, phaseAnchorUT: phaseAnchorUT);
        }

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

        // Records every (resolved endpoint pid, manifest) the production firing path
        // resolved. Returns an outcome whose OriginVesselPid == endpoint.VesselPersistentId
        // (the resolved pid the headless env falls back to), so reserve + release key
        // identically. Optionally simulate UNRESOLVED (endpoint lost mid-cycle).
        private sealed class RecordingPickupSeam
        {
            public readonly List<(uint pid, Dictionary<string, double> manifest)> Debits =
                new List<(uint, Dictionary<string, double>)>();
            public HashSet<uint> UnresolvedPids = new HashSet<uint>();

            // Optional hook run INSIDE Apply (i.e. after the window-debit fires but
            // BEFORE EmitPickupHalf's subsequent ReleaseWindowEscrow), so a test can
            // snapshot the reservation that is still live at the moment the window
            // physically debits. Receives the endpoint pid being debited.
            public Action<uint> OnDebit;

            // Optional endpoint-pid -> reported OriginVesselPid remap. When a window's
            // endpoint pid is present, the outcome reports a DIFFERENT OriginVesselPid
            // (simulating a mid-cycle source-identity change / surface-fallback / craft-
            // baked-pid regeneration), so EmitPickupHalf's release keys on the diverged
            // pid and MISSES the dispatch-time reserve (the C2 leak). Absent -> identity.
            public Dictionary<uint, uint> ReleasePidRemap;

            public RouteOrchestrator.OriginDebitOutcome Apply(
                RouteEndpoint endpoint, Dictionary<string, double> manifest, IRouteRuntimeEnvironment env)
            {
                Debits.Add((endpoint.VesselPersistentId,
                    manifest != null ? new Dictionary<string, double>(manifest) : null));
                OnDebit?.Invoke(endpoint.VesselPersistentId);

                if (UnresolvedPids.Contains(endpoint.VesselPersistentId))
                {
                    return new RouteOrchestrator.OriginDebitOutcome
                    {
                        ActualDebited = null,
                        RequestedOnShortfall = manifest != null
                            ? new Dictionary<string, double>(manifest)
                            : null,
                        OriginVesselPid = 0u, // unresolved
                        Short = true,
                        Unresolved = true,
                    };
                }

                uint reportedPid = endpoint.VesselPersistentId;
                if (ReleasePidRemap != null
                    && ReleasePidRemap.TryGetValue(endpoint.VesselPersistentId, out uint remapped))
                    reportedPid = remapped;

                return new RouteOrchestrator.OriginDebitOutcome
                {
                    ActualDebited = manifest != null
                        ? new Dictionary<string, double>(manifest)
                        : null,
                    RequestedOnShortfall = null,
                    OriginVesselPid = reportedPid,
                    Short = false,
                    Unresolved = false,
                };
            }
        }

        private RecordingPickupSeam InstallPickupSeam()
        {
            var seam = new RecordingPickupSeam();
            RouteOrchestrator.PickupDebitApplierForTesting = seam.Apply;
            // Install the A3 row-emitter so the REAL ApplyDelivery path runs its
            // per-window guard without resolving a live destination Vessel (otherwise
            // the headless env returns vessel=null and ApplyDelivery flips the route to
            // EndpointLost). Emits a genuine RouteCargoDelivered row after the guard.
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
            return seam;
        }

        // Same as InstallPickupSeam but the debit outcome reports a DIVERGED
        // OriginVesselPid for the remapped endpoints, so EmitPickupHalf's release keys
        // on the diverged pid (missing the dispatch-time reserve) - the C2 leak path.
        private RecordingPickupSeam InstallPickupSeamWithPidRemap(Dictionary<uint, uint> remap)
        {
            var seam = InstallPickupSeam();
            seam.ReleasePidRemap = remap;
            return seam;
        }

        // A 2-SOURCE multi-stop route: pickup at depot A (pid 100, 100 Ore) at dock
        // 1150, pickup at depot B (pid 200, 200 Ore) at dock 1300, deliver at the
        // station at dock 1380. NON-KSC origin. (Deliver-at-station so the route also
        // exercises the delivery half; escrow only tracks the two pickup sources.)
        private static Route Build2SourceRoute(
            string id = "route-2src",
            long lastObservedLoopCycleIndex = -1,
            long stop0LastFired = -1,
            long stop1LastFired = -1,
            long stop2LastFired = -1)
        {
            return new Route
            {
                Id = id,
                Status = RouteStatus.Active,
                IsKscOrigin = false, // non-KSC -> origin gate runs, but pickup sources gate via env fake
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1380.0,
                DockMemberRecordingId = "rec-dock-station",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = lastObservedLoopCycleIndex,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 100u },
                        PickupManifest = new Dictionary<string, double> { { "Ore", 100.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = stop0LastFired,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 200u },
                        PickupManifest = new Dictionary<string, double> { { "Ore", 200.0 } },
                        SegmentIndexBefore = 1,
                        RecordedDockUT = 1300.0,
                        LastFiredCycleIndex = stop1LastFired,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 300u },
                        DeliveryManifest = new Dictionary<string, double> { { "Ore", 300.0 } },
                        SegmentIndexBefore = 2,
                        RecordedDockUT = 1380.0,
                        LastFiredCycleIndex = stop2LastFired,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-station", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        // A 2-window SHUTTLE (one recording, one source): load at refinery (pid 500,
        // 400 LF) at dock 1150, deliver at station (pid 600) at dock 1300. Non-KSC.
        private static Route BuildShuttleRoute(string id = "route-shuttle")
        {
            return new Route
            {
                Id = id,
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-station",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 500u },
                        PickupManifest = new Dictionary<string, double> { { "LiquidFuel", 400.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = -1,
                    },
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 600u },
                        DeliveryManifest = new Dictionary<string, double> { { "LiquidFuel", 400.0 } },
                        SegmentIndexBefore = 1,
                        RecordedDockUT = 1300.0,
                        LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-station", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
        }

        // Eligible env that resolves nothing live (vessel=null) so escrow pid resolution
        // falls back to the endpoint's baked pid - matching the seam's OriginVesselPid.
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

        private static List<GameAction> PickedUp() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteCargoPickedUp).ToList();

        private static List<GameAction> Dispatched() =>
            Ledger.Actions.Where(a => a.Type == GameActionType.RouteDispatched).ToList();

        // ==================================================================
        // (1) Two-source run: BOTH source debits fire at their windows + escrow nets
        //     to zero after the cycle completes (no leak)
        // ==================================================================

        // catches: the M4a defer NOT lifted (a >1 source-debiting window suppressed),
        // OR an escrow leak (reservation never released after the cycle completes). The
        // headline B3 keystone: both windows debit their own source at their own dock
        // phase, idempotent per (RouteId, cycleId, stopIndex), and the route's escrow is
        // EMPTY when the cycle finishes.
        [Fact]
        public void TwoSourceRun_BothSourceDebitsFire_EscrowEmptyAfterCycle_NoLeak()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span [1000,1400], docks 1150 / 1300 / 1380
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Tick at dock A (1150): window 0 (source A pickup) due. Dispatch fires +
            // reserves BOTH sources (100 Ore @ pid100, 200 Ore @ pid200). Window 0
            // debits source A + releases its 100.
            RouteOrchestrator.Tick(1150.0, env);

            Assert.Single(seam.Debits);
            Assert.Equal(100u, seam.Debits[0].pid);
            // Reserve at dispatch held both; A released, B still live (the partial-cycle
            // hold - see test (3) for the competing-route view).
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 100u, "Ore"));
            Assert.Equal(200.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));
            Assert.Single(Dispatched());

            // Tick past dock B (1350): window 1 (source B pickup) due. Source B debits +
            // releases its 200. (Cycle not complete until station dock 1380 reached.)
            RouteOrchestrator.Tick(1350.0, env);

            Assert.Equal(2, seam.Debits.Count);
            Assert.Equal(200u, seam.Debits[1].pid);
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));

            // Tick past station dock (1380): window 2 delivery fires, cycle completes.
            RouteOrchestrator.Tick(1380.0, env);

            // No re-dispatch, both source debits fired exactly once, escrow EMPTY.
            Assert.Single(Dispatched());
            Assert.Equal(2, seam.Debits.Count);
            Assert.Equal(2, PickedUp().Count);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting); // leak-free
            Assert.Equal(1, route.CompletedCycles);

            // Both pickup rows carry their own stopIndex under one cycleId.
            Assert.Contains(PickedUp(), p => p.RouteStopIndex == 0 && p.RouteCycleId == "cycle-0");
            Assert.Contains(PickedUp(), p => p.RouteStopIndex == 1 && p.RouteCycleId == "cycle-0");
        }

        // ==================================================================
        // (2) 2-window shuttle: refinery debited at its window
        // ==================================================================

        // catches: the shuttle (load at refinery, deliver at station = 2 windows) NOT
        // debiting the refinery at its window, or leaking the refinery reservation.
        [Fact]
        public void Shuttle_DebitsRefineryAtItsWindow_EscrowEmptyAfterCycle()
        {
            var route = BuildShuttleRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // docks 1150 (refinery) / 1300 (station)
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Tick at the refinery dock (1150): dispatch reserves 400 LF @ refinery
            // (pid 500), the pickup window debits it + releases.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Single(seam.Debits);
            Assert.Equal(500u, seam.Debits[0].pid);
            Assert.True(seam.Debits[0].manifest.TryGetValue("LiquidFuel", out double amt) && amt == 400.0);
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 500u, "LiquidFuel"));

            // Tick past the station dock (1350): delivery fires, cycle completes.
            RouteOrchestrator.Tick(1350.0, env);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Single(seam.Debits); // only the one source debit (refinery)
        }

        // ==================================================================
        // (3) Reserve-at-dispatch then release-at-each-window-debit; a PARTIAL cycle
        //     keeps the un-fired window's reservation LIVE (competing route sees it)
        // ==================================================================

        // catches: the partial-cycle reservation NOT staying live across the gap (a
        // competing route could then drain the un-fired source). After window 0 fires
        // but window 1 has NOT (the cycle is mid-flight), source B's reservation must
        // still be live and a competing route's B1 gate must see it.
        [Fact]
        public void PartialCycle_UnfiredWindowReservationLive_CompetingRouteSeesIt()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            InstallPickupSeam();
            var env = new EligibleEnv();

            // Tick at dock A only (1150): window 0 fires, window 1 (source B) pending.
            RouteOrchestrator.Tick(1150.0, env);

            // Source B's reservation is LIVE (200 Ore held against pid 200).
            Assert.Equal(200.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));

            // A competing route gating depot B sees its available reduced by route's 200
            // reservation (the production B1 net wrap). Depot B holds 250 Ore live; the
            // competitor sees 250 - 200 = 50.
            double otherReserved = RouteStore.OtherRoutesReservedFor("route-competitor", 200u, "Ore");
            Assert.Equal(200.0, otherReserved);
            double competitorAvailable = 250.0 - otherReserved;
            Assert.Equal(50.0, competitorAvailable);
        }

        // ==================================================================
        // (4) A window endpoint LOST mid-cycle releases its reservation (no leak) and
        //     the cycle still completes (OQ4: warn + skip + continue, not abort)
        // ==================================================================

        // catches: an endpoint-lost window LEAKING its reservation (the cargo was not
        // taken, so the hold must be freed), or the lost window ABORTING the cycle
        // (OQ4: warn + skip + continue - the surviving windows still fire).
        [Fact]
        public void WindowEndpointLost_ReleasesReservation_CycleStillCompletes()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            var seam = InstallPickupSeam();
            seam.UnresolvedPids.Add(200u); // source B endpoint lost mid-cycle
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // window 0 (source A) fires + releases A
            RouteOrchestrator.Tick(1350.0, env); // window 1 (source B) UNRESOLVED

            // Source B's reservation released despite the unresolved debit (no leak).
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));

            RouteOrchestrator.Tick(1380.0, env); // station delivery, cycle completes

            // The cycle completed despite the lost source-B window (not aborted), and
            // the escrow is empty (both holds released - A debited, B freed-on-loss).
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(1, route.CompletedCycles);
            // Both pickup windows emitted a row (the lost one carries zero actuals +
            // requested-on-shortfall, the unresolved-degrade path).
            Assert.Equal(2, PickedUp().Count);
        }

        // ==================================================================
        // (5) Crashed disposition still debits the source (19.2.5)
        // ==================================================================

        // catches: the source debit being gated on the transport's final disposition.
        // The outflow was witnessed at the dock, so the debit fires regardless of how
        // the transport ends up. The firing path does not consult disposition at all -
        // this pins that the debit + release happen on the normal (resolved) window.
        [Fact]
        public void CrashedDisposition_StillDebitsSource()
        {
            // The route shape carries no disposition field on the firing path; a
            // "crashed" transport is just the recording's outcome. The debit fires at
            // the window phase unconditionally (it is not gated on any disposition).
            var route = BuildShuttleRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // refinery window

            // The refinery was debited (the witnessed outflow), and the reservation
            // released - independent of any transport disposition.
            Assert.Single(seam.Debits);
            Assert.Equal(500u, seam.Debits[0].pid);
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 500u, "LiquidFuel"));
        }

        // ==================================================================
        // (6) Cross-cycle gap-tick with multi-pickup: each cycle's source debits fire
        //     once, reservations don't leak across cycles
        // ==================================================================

        // catches: a gap-tick straddling cycles double-debiting a source or leaking a
        // reservation across the cross-cycle catch-up (the A3 Blocker1 class, extended
        // to a source-debiting route). After two complete cycles, each source is
        // debited exactly twice (once per cycle), and the escrow is empty.
        // NOTE (C4): this drives TWO FULL CYCLES window-by-window on SEPARATE ticks
        // (it is NOT a single straddling gap-tick); the genuine single-tick cMin
        // catch-up path is pinned by the A3 suite (without escrow). The escrow
        // assertion here is the per-cycle reserve/release net-zero across cycles.
        [Fact]
        public void CrossCycleGapTick_MultiPickup_EachSourceDebitsOncePerCycle_NoLeak()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // span 400, docks 1150 / 1300 / 1380
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Cycle 0: dock A (1150), dock B (1350), station (1380).
            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1350.0, env);
            RouteOrchestrator.Tick(1380.0, env);
            Assert.Equal(1, route.CompletedCycles);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);

            // Cycle 1: docks at 1000 + 400 + {150,300,380} = 1550 / 1700 / 1780.
            RouteOrchestrator.Tick(1550.0, env);
            RouteOrchestrator.Tick(1750.0, env);
            RouteOrchestrator.Tick(1780.0, env);
            Assert.Equal(2, route.CompletedCycles);

            // Each source debited exactly twice (once per cycle), no leak.
            int sourceADebits = seam.Debits.Count(d => d.pid == 100u);
            int sourceBDebits = seam.Debits.Count(d => d.pid == 200u);
            Assert.Equal(2, sourceADebits);
            Assert.Equal(2, sourceBDebits);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting); // no cross-cycle leak

            // Two dispatches (one per cycle), under unique cycleIds.
            Assert.Equal(2, Dispatched().Count);
            Assert.Contains(Dispatched(), d => d.RouteCycleId == "cycle-0");
            Assert.Contains(Dispatched(), d => d.RouteCycleId == "cycle-1");
        }

        // ==================================================================
        // (7) Idempotent across reload: a window re-presented under a frozen cycleId
        //     does NOT double-debit (C-2 backstop) and the escrow stays consistent
        // ==================================================================

        // catches: a reload re-presenting an already-fired pickup window double-debiting
        // the source. Pre-seed the cycle-0 pickup row for window 0 (as if it fired +
        // saved), then re-present window 0: the C-2 backstop suppresses the second
        // physical debit; no second escrow release against a (post-reload empty) escrow.
        [Fact]
        public void IdempotentAcrossReload_NoDoubleDebit()
        {
            var route = Build2SourceRoute(stop0LastFired: 0, lastObservedLoopCycleIndex: -1);
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Pre-seed the reload state: dispatch row + window-0 pickup row for cycle-0.
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteDispatched, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0, Sequence = 0,
            });
            Ledger.AddAction(new GameAction
            {
                Type = GameActionType.RouteCargoPickedUp, UT = 1150.0, RouteId = route.Id,
                RouteCycleId = "cycle-0", RouteStopIndex = 0,
                Sequence = 0 * RouteOrchestrator.SeqStride + 2,
            });

            // Re-present window 0 directly through the real EmitPickupHalf (the C-2 guard
            // lives there). PendingStopIndex = 0.
            route.PendingStopIndex = 0;
            RouteOrchestrator.EmitPickupHalf(route, 1150.0, env, "cycle-0");

            // No physical debit (C-2 backstop), no second pickup row.
            Assert.Empty(seam.Debits);
            Assert.Single(PickedUp()); // only the pre-seeded one
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("C-2 backstop"));
        }

        // ==================================================================
        // (8) Single-window M3a pickup is behaviour-identical (no regression)
        // ==================================================================

        // catches: B3 escrow wiring changing the single-window M3a pickup behaviour. A
        // 1-stop pickup route fires its source debit, reserves+releases within one
        // EmitLoopCycle, and ends with an empty escrow - just as a single-source M3a
        // route did before B3 (the reserve/release is a within-tick net-zero there).
        [Fact]
        public void SingleWindowM3aPickup_BehaviourIdentical_EscrowNetsZeroInTick()
        {
            var route = new Route
            {
                Id = "route-m3a",
                Status = RouteStatus.Active,
                IsKscOrigin = true, // KSC origin: origin gate passes, pickup source gates via env fake
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-dock-a",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 200.0,
                TransitDuration = 200.0,
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint { VesselPersistentId = 999u },
                        PickupManifest = new Dictionary<string, double> { { "Ore", 50.0 } },
                        SegmentIndexBefore = 0,
                        RecordedDockUT = 1150.0,
                        LastFiredCycleIndex = -1,
                    },
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-dock-a", TreeId = "tree-1", RouteProofHash = "deadbeef" },
                },
            };
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit(spanStartUT: 1000.0, spanEndUT: 1200.0,
                cadenceSeconds: 200.0, phaseAnchorUT: 1000.0));
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Single tick at the dock: EmitLoopCycle reserves 50 Ore @ pid 999, the
            // single pickup window debits it + releases - net-zero within the tick.
            RouteOrchestrator.Tick(1150.0, env);

            Assert.Single(seam.Debits);
            Assert.Equal(999u, seam.Debits[0].pid);
            // Escrow EMPTY after the single tick (reserve == release for one window).
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Single(PickedUp());
        }

        // ==================================================================
        // (9) Delivery-only routes reserve NOTHING (no escrow side effects)
        // ==================================================================

        // catches: a delivery-only route accidentally reserving escrow (it has no pickup
        // source - there is nothing to hold). A pure-delivery multi-stop cycle must
        // leave the escrow untouched.
        [Fact]
        public void DeliveryOnlyRoute_ReservesNothing()
        {
            var route = new Route
            {
                Id = "route-delivery-only",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-1",
                RecordedDockUT = 1300.0,
                DockMemberRecordingId = "rec-dock-b",
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
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
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            // Use the row-emitter seam so the real ApplyDelivery guard runs without a Vessel.
            RouteOrchestrator.DeliveryRowEmitterForTesting =
                (r, ut, e, cycleId, stopIndex, bump) =>
                {
                    Ledger.AddAction(new GameAction
                    {
                        Type = GameActionType.RouteCargoDelivered,
                        UT = ut, RouteId = r.Id, RouteCycleId = cycleId, RouteStopIndex = stopIndex,
                        Sequence = stopIndex * RouteOrchestrator.SeqStride + 3,
                    });
                };
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env);
            RouteOrchestrator.Tick(1350.0, env);

            // No escrow held at any point for a delivery-only route.
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(1, route.CompletedCycles);
        }

        // ==================================================================
        // (10) C1: a dispatchAlready resume after a mid-cycle escrow CLEAR (scene-
        //      switch / reload) RE-ESTABLISHES the un-fired window's reservation
        //      before that window fires (a competing route sees it again), and is
        //      IDEMPOTENT (a normal in-session resume does NOT double-reserve).
        // ==================================================================

        // catches (C1): the un-fired window's hold being SILENTLY LOST across a mid-
        // cycle escrow clear (a scene-switch ClearAllEscrow or a reload dropped the
        // RAM-only map) - leaving a gap where a competing route could drain the source
        // (violating 19.2.5 strand-protection). On the dispatchAlready resume the
        // reserve must be RE-ESTABLISHED from the still-un-fired pickup windows
        // (OQ7/D11 "recomputed from pending state on the next Tick").
        [Fact]
        public void DispatchAlreadyResumeAfterClear_ReestablishesUnfiredWindowReservation()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit()); // docks 1150 (A) / 1300 (B) / 1380 (station)
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Tick at dock A (1150): dispatch reserves BOTH (A=100, B=200), window 0
            // debits + releases A. Source B's 200 reservation is now live (partial).
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(200.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 100u, "Ore"));

            // Simulate a scene-switch / reload mid-gap: the RAM-only escrow is cleared.
            RouteStore.ClearAllEscrow("test-scene-switch");
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting); // hold lost (the bug)

            // On window B's debit, snapshot the live reservation BEFORE EmitPickupHalf
            // releases it: the resume must have RE-ESTABLISHED B's 200 hold, and a
            // competing route's B1 net must see it.
            double reservedAtBDebit = -1.0;
            double competitorSeesAtBDebit = -1.0;
            seam.OnDebit = pid =>
            {
                if (pid == 200u)
                {
                    reservedAtBDebit = RouteStore.GetReservedForTesting(route.Id, 200u, "Ore");
                    competitorSeesAtBDebit =
                        RouteStore.OtherRoutesReservedFor("route-competitor", 200u, "Ore");
                }
            };

            // Resume tick past dock B (1350): dispatchAlready==true (cycle-0 dispatch
            // is in the ledger), the escrow was cleared -> re-establish B's reservation,
            // THEN window B fires + releases it.
            RouteOrchestrator.Tick(1350.0, env);

            // At window B's debit the re-established hold was LIVE (200 Ore), and a
            // competing route saw it (the strand-protection restored across the gap).
            Assert.Equal(200.0, reservedAtBDebit);
            Assert.Equal(200.0, competitorSeesAtBDebit);

            // Window A (already fired this cycle) was NOT re-reserved (no double): only
            // window B's source debited on the resume tick.
            Assert.Equal(2, seam.Debits.Count); // A on tick 1, B on the resume tick
            Assert.Equal(200u, seam.Debits[1].pid);

            // The re-established reservation released at B's debit; cycle completes
            // at the station dock, escrow empty (no leak).
            RouteOrchestrator.Tick(1380.0, env);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting);
            Assert.Equal(1, route.CompletedCycles);

            // The re-establish was logged with its distinguishing context tag.
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("ReserveCycleEscrow") && l.Contains("context=resume-reestablish"));
        }

        // catches (C1 idempotency): a NORMAL in-session resume (no clear happened) re-
        // reserving the un-fired window on top of the live reservation -> DOUBLE hold.
        // The re-establish must run ONLY when the route holds NO escrow; an in-session
        // resume still holds its reservation, so it must short-circuit.
        [Fact]
        public void DispatchAlreadyResumeNoClear_DoesNotDoubleReserve()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            var seam = InstallPickupSeam();
            var env = new EligibleEnv();

            // Tick at dock A: B's 200 reservation is live (partial cycle). No clear.
            RouteOrchestrator.Tick(1150.0, env);
            Assert.Equal(200.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));

            // On window B's debit, snapshot the live reservation: it must still be
            // exactly 200 (the in-session resume did NOT re-reserve on top of it).
            double reservedAtBDebit = -1.0;
            seam.OnDebit = pid =>
            {
                if (pid == 200u)
                    reservedAtBDebit = RouteStore.GetReservedForTesting(route.Id, 200u, "Ore");
            };

            // Resume tick (no clear in between): dispatchAlready==true, escrow still
            // held -> the re-establish must NO-OP (idempotent), so B's hold stays 200,
            // not 400.
            RouteOrchestrator.Tick(1350.0, env);

            Assert.Equal(200.0, reservedAtBDebit); // not doubled to 400
            RouteOrchestrator.Tick(1380.0, env);
            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting); // nets to zero, no leak
            Assert.Equal(1, route.CompletedCycles);

            // No resume-reestablish reserve happened (the route already held escrow).
            Assert.DoesNotContain(logLines, l => l.Contains("context=resume-reestablish"));
        }

        // ==================================================================
        // (11) C2: a cycle-complete drop SWEEP clears any residual reservation, even
        //      when a release MISSED (reserve-pid != release-pid divergence).
        // ==================================================================

        // catches (C2): a positive-residual reservation LEAKING past cycle-complete
        // when the window's resolved release pid diverged from the dispatch-time
        // reserve pid (a mid-cycle source-identity change / surface-fallback / craft-
        // baked-pid regeneration), so the release missed the reserved key. The cycle-
        // complete DropRouteEscrow must sweep it -> EscrowRouteCountForTesting == 0
        // after the cycle regardless of pid divergence.
        [Fact]
        public void CycleComplete_DropsResidual_WhenReleasePidDiverges()
        {
            var route = Build2SourceRoute();
            RouteStore.AddRoute(route);
            InstallUnitResolver(BuildUnit());
            // Make source B's window RESOLVE TO A DIFFERENT PID at debit time than the
            // dispatch-time reserve keyed on. The reserve keys on the endpoint baked
            // pid (200, headless fallback); the seam reports OriginVesselPid = 999 for
            // pid 200, so ReleaseWindowEscrow releases against 999 - MISSING the 200
            // reservation. (Simulates a surface-fallback / pid-regeneration divergence.)
            var seam = InstallPickupSeamWithPidRemap(new Dictionary<uint, uint> { { 200u, 999u } });
            var env = new EligibleEnv();

            RouteOrchestrator.Tick(1150.0, env); // window A debits + releases (pid 100 matches)
            RouteOrchestrator.Tick(1350.0, env); // window B debits; release on 999 misses 200

            // Mid-cycle the divergence left a positive residual on pid 200 (the release
            // went to 999). This is the leak C2 closes.
            Assert.Equal(200.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));

            // Station dock (1380): cycle completes -> DropRouteEscrow sweeps the residual.
            RouteOrchestrator.Tick(1380.0, env);

            Assert.Equal(0, RouteStore.EscrowRouteCountForTesting); // residual swept, no leak
            Assert.Equal(0.0, RouteStore.GetReservedForTesting(route.Id, 200u, "Ore"));
            Assert.Equal(1, route.CompletedCycles);
            // The cycle-complete drop logged a sweep of the residual entry.
            Assert.Contains(logLines, l => l.Contains("[Route]")
                && l.Contains("DropRouteEscrow") && l.Contains("droppedResourceEntries=1"));
        }
    }
}
