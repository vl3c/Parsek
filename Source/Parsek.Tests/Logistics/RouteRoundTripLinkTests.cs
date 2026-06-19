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
    /// M4c Phase C1 (plan D12 / OQ8): the round-trip linking chain-constraint
    /// scheduler. A linked route B holds <c>WaitingForPartner</c> until its
    /// partner A completes a NEW cycle, alternating A-&gt;B-&gt;A. These tests drive
    /// the REAL <see cref="RouteOrchestrator.ProcessLoopRoute"/> path through
    /// <see cref="RouteOrchestrator.Tick(double, IRouteRuntimeEnvironment)"/> with
    /// the loop-unit + delivery-row-emitter seams (the same fixture pattern as
    /// <see cref="RouteMultiStopFireTests"/>), so the partner gate
    /// (<see cref="RouteDispatchEvaluator.CheckEligibility"/>) and the dispatch-time
    /// alternation advance both run for real, including the
    /// <see cref="RouteStore.TryGetRoute"/> partner resolution.
    /// </summary>
    [Collection("Sequential")]
    public class RouteRoundTripLinkTests : IDisposable
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        private readonly List<string> logLines = new List<string>();

        public RouteRoundTripLinkTests()
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
            logLines.Clear();
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
        // Seam helpers
        // ==================================================================

        // A single-stop DELIVERY loop route. span [1000, 1400] (400s), dock at 1150.
        // Each tick at loopUT >= 1150 inside the cycle's span fires one cycle.
        private static GhostPlaybackLogic.LoopUnit BuildUnit() =>
            new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0, memberIndices: new[] { 0 },
                spanStartUT: 1000.0, spanEndUT: 1400.0,
                cadenceSeconds: 400.0, phaseAnchorUT: 1000.0);

        private void InstallUnitResolver(GhostPlaybackLogic.LoopUnit unit)
        {
            RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) => unit;
        }

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

        // A single-stop delivery loop route; dock at 1150 inside the [1000,1400]
        // span. KSC origin (no physical origin debit needed for the headless path).
        private static Route BuildLinkedRoute(
            string id,
            string linkedRouteId,
            int dispatchPriority,
            RouteStatus status = RouteStatus.Active,
            int completedCycles = 0,
            int lastConsumedPartnerCycle = 0)
        {
            return new Route
            {
                Id = id,
                Status = status,
                IsKscOrigin = true,
                BackingMissionTreeId = "tree-" + id,
                RecordedDockUT = 1150.0,
                DockMemberRecordingId = "rec-" + id,
                LoopAnchorUT = 1000.0,
                LastObservedLoopCycleIndex = -1,
                DispatchInterval = 400.0,
                TransitDuration = 400.0,
                DispatchPriority = dispatchPriority,
                LinkedRouteId = linkedRouteId,
                CompletedCycles = completedCycles,
                LastConsumedPartnerCycle = lastConsumedPartnerCycle,
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
                },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "rec-" + id, TreeId = "tree-" + id, RouteProofHash = "deadbeef" },
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

        // Drives the route's single-stop cycle for the given cycle index (0-based):
        // a tick at the cycle's dock phase. dock = anchor + cycleIndex*cadence + 150.
        private void TickAtDock(Route route, int cycleIndex, IRouteRuntimeEnvironment env)
        {
            // span anchor 1000, cadence 400, dock offset 150 within the span.
            double ut = 1000.0 + cycleIndex * 400.0 + 150.0;
            // Only this route resolves a unit; install per-route by id check is not
            // needed because each test ticks the store and the resolver returns the
            // SAME canonical unit (both routes share span shape).
            RouteOrchestrator.Tick(ut, env);
        }

        private static int Delivered(string routeId) =>
            Ledger.Actions.Count(a => a.Type == GameActionType.RouteCargoDelivered && a.RouteId == routeId);

        private static int Dispatched(string routeId) =>
            Ledger.Actions.Count(a => a.Type == GameActionType.RouteDispatched && a.RouteId == routeId);

        // ==================================================================
        // (1) Strict A->B->A alternation
        // ==================================================================

        // catches: B (linked to A) dispatching before A completes, or A and B both
        // running freely without alternation. The headline invariant: A completes a
        // cycle -> B becomes eligible and dispatches + consumes A's cycle -> B holds
        // again until A completes ANOTHER cycle.
        [Fact]
        public void Alternation_BHoldsUntilAcompletes_ThenConsumesAndHoldsAgain()
        {
            // A is the seed (priority 0 < 1). Both linked, fresh (0 completed, cursor 0).
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b", dispatchPriority: 0);
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a", dispatchPriority: 1);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            // Cycle 0 dock: both cross. A (seed) dispatches via the deadlock seed
            // bypass and bumps to CompletedCycles 1. B then sees A.CompletedCycles 1
            // > B.LastConsumed 0 -> B ALSO eligible and dispatches this same tick
            // (the gate is "A has completed a cycle", satisfied within the tick once
            // A processed first). Both consumed correctly.
            TickAtDock(a, 0, env);
            Assert.Equal(1, a.CompletedCycles);
            Assert.Equal(1, b.CompletedCycles);
            // A consumed B's cycle 0; B consumed A's cycle 1 (A processed first).
            Assert.Equal(0, a.LastConsumedPartnerCycle);
            Assert.Equal(1, b.LastConsumedPartnerCycle);
            Assert.Equal(1, Dispatched("route-a"));
            Assert.Equal(1, Dispatched("route-b"));

            // Cycle 1 dock: A's gate is partner(B).CompletedCycles(1) <=
            // A.LastConsumed(0) -> 1 <= 0 false -> A eligible, dispatches, consumes
            // B's cycle 1. B's gate is partner(A).CompletedCycles(2 after A) <=
            // B.LastConsumed(1) -> 2 <= 1 false -> B eligible too.
            TickAtDock(a, 1, env);
            Assert.Equal(2, a.CompletedCycles);
            Assert.Equal(2, b.CompletedCycles);
            Assert.Equal(1, a.LastConsumedPartnerCycle); // consumed B cycle 1
            Assert.Equal(2, b.LastConsumedPartnerCycle); // consumed A cycle 2
        }

        // catches the alternation gate in isolation: when A has NOT completed a new
        // cycle since B last consumed one, B must HOLD WaitingForPartner. Verdict
        // asserted at the evaluator level (CheckEligibility) so it is independent of
        // same-tick processing order. Then advancing A's completion clears B's gate.
        [Fact]
        public void Alternation_BHoldsUntilPartnerCompletesNewCycle()
        {
            // A has completed 1 cycle; B already consumed that cycle (cursor 1).
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b",
                dispatchPriority: 0, completedCycles: 1);
            a.Name = "Outbound";
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a",
                dispatchPriority: 1, completedCycles: 1, lastConsumedPartnerCycle: 1);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            var env = new EligibleEnv();

            // B's gate: partner(A).CompletedCycles(1) <= B.LastConsumed(1) -> 1<=1
            // true -> HOLD WaitingForPartner, naming A.
            var heldVerdict = RouteDispatchEvaluator.CheckEligibility(b, 1150.0, env);
            Assert.False(heldVerdict.Eligible);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, heldVerdict.Kind);
            Assert.Equal("partner:Outbound", heldVerdict.Reason);
            string describe = LogisticsHoldPresentation.DescribeHold(
                heldVerdict.Kind, heldVerdict.Reason, heldVerdict.Shortfall);
            Assert.Contains("Outbound", describe);

            // A completes ANOTHER cycle -> B alternates in: partner(A).CompletedCycles
            // (2) > B.LastConsumed(1) -> eligible.
            a.CompletedCycles = 2;
            Assert.True(RouteDispatchEvaluator.CheckEligibility(b, 1150.0, env).Eligible);
        }

        // ==================================================================
        // (2) Short/blocked partner -> B holds WaitingForPartner (named)
        // ==================================================================

        // catches: a partner that never completes (A blocked) failing to hold B, or
        // holding B with a reason that does not name the partner. A is short of cargo
        // every cycle, so it never completes; B (linked to A, already consumed A's
        // baseline) must hold WaitingForPartner with A named.
        [Fact]
        public void BlockedPartner_BHoldsWaitingForPartner_PartnerNamed()
        {
            // A blocked: never completes. B consumed A's baseline (both at completed 0,
            // B cursor 0, A is the partner). B is NOT the seed (priority 1 > A's 0), so
            // B does not get the deadlock bypass; A (seed) would, but A is blocked.
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b", dispatchPriority: 0);
            a.Name = "Outbound Supply";
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a", dispatchPriority: 1);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();

            // A blocked (origin lacks cargo), B's other gates pass. The partner gate
            // is ordered LAST, so B surfaces WaitingForPartner (not a cargo/funds
            // blocker of its own).
            var blockedAEligibleB = new BlockedForRouteEnv(blockedRouteId: "route-a");
            TickAtDock(b, 0, blockedAEligibleB);

            Assert.Equal(0, b.CompletedCycles);
            Assert.Equal(0, Dispatched("route-b"));
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, b.LastHoldKind);
            string describe = LogisticsHoldPresentation.DescribeHold(
                b.LastHoldKind, b.LastHoldDetail, b.LastHoldShortfall);
            Assert.Contains("Outbound Supply", describe);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("HOLD WaitingForPartner"));
        }

        // Env that blocks origin cargo for ONE named route (the partner A) but keeps
        // every other route eligible. Lets the partner stay incomplete while the
        // linked route's own gates pass, isolating the WaitingForPartner hold.
        private sealed class BlockedForRouteEnv : IRouteRuntimeEnvironment
        {
            private readonly string blockedRouteId;
            public BlockedForRouteEnv(string blockedRouteId) { this.blockedRouteId = blockedRouteId; }
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource)
            {
                if (string.Equals(route.Id, blockedRouteId, StringComparison.Ordinal))
                { lackingResource = "LiquidFuel"; return false; }
                lackingResource = string.Empty; return true;
            }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // (3) Paused partner -> B bypasses the constraint (design 10.14)
        // ==================================================================

        // catches: a Paused partner stalling B forever (the partner never completes a
        // cycle, so without the bypass B would wait indefinitely). With the partner
        // Paused, B dispatches on its OWN schedule.
        [Fact]
        public void PausedPartner_BBypassesConstraint_DispatchesOnOwnSchedule()
        {
            // A is Paused (not ghost-driving). B linked to A, B already consumed A's
            // baseline so the alternation gate WOULD hold it - but the Paused bypass
            // overrides.
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b",
                dispatchPriority: 0, status: RouteStatus.Paused, completedCycles: 0);
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a",
                dispatchPriority: 1, completedCycles: 0, lastConsumedPartnerCycle: 0);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            TickAtDock(b, 0, env);

            // B dispatched despite the alternation gate (A Paused -> bypass).
            Assert.Equal(1, b.CompletedCycles);
            Assert.Equal(1, Dispatched("route-b"));
            // A (Paused) did NOT dispatch.
            Assert.Equal(0, Dispatched("route-a"));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("not ghost-driving - bypassing"));
        }

        // catches: a missing partner (deleted / never created) stalling B. The
        // partner id resolves to nothing -> bypass.
        [Fact]
        public void MissingPartner_BBypassesConstraint()
        {
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-gone",
                dispatchPriority: 1, completedCycles: 0, lastConsumedPartnerCycle: 0);
            RouteStore.AddRoute(b); // partner "route-gone" NOT in the store
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            TickAtDock(b, 0, env);

            Assert.Equal(1, b.CompletedCycles);
            Assert.Equal(1, Dispatched("route-b"));
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("unresolved - bypassing"));
        }

        // ==================================================================
        // (4) Deadlock guard: mutual A<->B link, neither completed
        // ==================================================================

        // catches: a mutual A<->B link with both at the default cursor deadlocking
        // (both compute partner.CompletedCycles(0) <= LastConsumed(0) = held, forever).
        // The SEED (lower DispatchPriority, then ordinal id) is exempted on its FIRST
        // cycle and dispatches; the NON-seed waits until the seed completes, then
        // alternates. Asserted at the evaluator level so each route's gate verdict is
        // isolated from same-tick processing order.
        [Fact]
        public void Deadlock_MutualLink_SeedDispatchesFirst_NonSeedWaits()
        {
            // A priority 0 (seed), B priority 1 (non-seed). Both fresh: CompletedCycles
            // 0, LastConsumedPartnerCycle 0, both Active/ghost-driving.
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b", dispatchPriority: 0);
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a", dispatchPriority: 1);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            var env = new EligibleEnv();

            // Seed predicate is deterministic + structural (priority then ordinal id).
            Assert.True(RouteDispatchEvaluator.IsChainSeed(a, b));
            Assert.False(RouteDispatchEvaluator.IsChainSeed(b, a));

            // Fresh-chain verdict: the SEED (A) is eligible (deadlock break); the
            // NON-seed (B) HOLDS WaitingForPartner. Without the seed rule both would
            // hold (0 <= 0) and deadlock.
            Assert.True(RouteDispatchEvaluator.CheckEligibility(a, 1150.0, env).Eligible);
            var bElig = RouteDispatchEvaluator.CheckEligibility(b, 1150.0, env);
            Assert.False(bElig.Eligible);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, bElig.Kind);

            // Drive the seed's first cycle (advances A.CompletedCycles to 1, consumes
            // B's cycle 0). Now the NON-seed B alternates in: partner(A).CompletedCycles
            // (1) > B.LastConsumed(0) -> eligible.
            a.CompletedCycles = 1;
            a.LastConsumedPartnerCycle = 0;
            Assert.True(RouteDispatchEvaluator.CheckEligibility(b, 1150.0, env).Eligible);
            Assert.Contains(logLines, l => l.Contains("[Route]") && l.Contains("chain SEED")
                && l.Contains("deadlock break"));
        }

        // catches: the seed re-dispatching every cycle while the partner stays at 0
        // (the seed bypass not being scoped to the seed's FIRST cycle - route AND
        // partner both at CompletedCycles 0). After the seed completes its first cycle
        // (CompletedCycles 1) the bypass must STOP firing and the normal alternation
        // gate must HOLD it until the partner completes a cycle.
        [Fact]
        public void SeedBypass_IsOneShot_SeedHoldsAfterItsFirstCycle()
        {
            var a = BuildLinkedRoute("route-a", linkedRouteId: "route-b", dispatchPriority: 0);
            var b = BuildLinkedRoute("route-b", linkedRouteId: "route-a", dispatchPriority: 1);
            RouteStore.AddRoute(a);
            RouteStore.AddRoute(b);
            var env = new EligibleEnv();

            // First cycle: both at 0 -> seed bypass -> A eligible.
            Assert.True(RouteDispatchEvaluator.CheckEligibility(a, 1150.0, env).Eligible);

            // The seed completed its first cycle (bypass fired) and consumed B's 0.
            a.CompletedCycles = 1;
            a.LastConsumedPartnerCycle = 0;

            // Second cycle: A.CompletedCycles 1 != 0 -> seed bypass NO LONGER fires;
            // normal gate partner(B).CompletedCycles(0) <= A.LastConsumed(0) -> HOLD.
            var elig = RouteDispatchEvaluator.CheckEligibility(a, 1550.0, env);
            Assert.False(elig.Eligible);
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.WaitingForPartner, elig.Kind);
        }

        // ==================================================================
        // (5) Unlinked routes are byte-behaviour-identical (gate skipped)
        // ==================================================================

        // catches: the partner gate firing for an UNLINKED route (LinkedRouteId null).
        // An unlinked route must dispatch exactly as before, never touching the gate
        // or RouteStore partner lookup.
        [Fact]
        public void UnlinkedRoute_GateSkipped_DispatchesNormally()
        {
            var solo = BuildLinkedRoute("route-solo", linkedRouteId: null, dispatchPriority: 0);
            RouteStore.AddRoute(solo);
            InstallUnitResolver(BuildUnit());
            InstallRealPathRowEmitter();
            var env = new EligibleEnv();

            TickAtDock(solo, 0, env);

            Assert.Equal(1, solo.CompletedCycles);
            Assert.Equal(1, Dispatched("route-solo"));
            // The cursor never moves for an unlinked route.
            Assert.Equal(0, solo.LastConsumedPartnerCycle);
            // No partner-gate log line for an unlinked route.
            Assert.DoesNotContain(logLines, l => l.Contains("PartnerGate:"));
        }

        // catches: PartnerConstraintSatisfied doing a RouteStore lookup for a null /
        // empty LinkedRouteId (the byte-behaviour-identical fast path).
        [Fact]
        public void PartnerConstraintSatisfied_NullLink_ReturnsTrue_NoLookup()
        {
            var solo = BuildLinkedRoute("route-solo", linkedRouteId: null, dispatchPriority: 0);
            // No routes in the store at all.
            Assert.True(RouteDispatchEvaluator.PartnerConstraintSatisfied(solo, out string reason));
            Assert.Null(reason);
        }

        // ==================================================================
        // (6) Gate ORDERED LAST: a genuinely-blocked route surfaces its real
        //     blocker first, not WaitingForPartner.
        // ==================================================================

        // catches: the partner gate running BEFORE the cargo / funds / endpoint gates
        // and masking a route's real blocker. A linked route that is BOTH short of
        // cargo AND would wait on its partner must report OriginLacksCargo (the real,
        // higher-priority blocker), because the partner gate is ordered last.
        [Fact]
        public void PartnerGate_OrderedLast_RealBlockerSurfacesFirst()
        {
            // The partner exists, live, and has NOT completed a new cycle, so the
            // partner gate WOULD hold this route. But the route is also short of
            // cargo. The earlier origin-cargo gate must win.
            var partner = BuildLinkedRoute("route-p", linkedRouteId: "route-q",
                dispatchPriority: 0, completedCycles: 1);
            var q = BuildLinkedRoute("route-q", linkedRouteId: "route-p",
                dispatchPriority: 1, completedCycles: 1, lastConsumedPartnerCycle: 1);
            RouteStore.AddRoute(partner);
            RouteStore.AddRoute(q);

            // Env: q is short of origin cargo (a real blocker BEFORE the partner gate).
            var shortCargoEnv = new ShortCargoEnv();
            var elig = RouteDispatchEvaluator.CheckEligibility(q, 1150.0, shortCargoEnv);

            Assert.False(elig.Eligible);
            // The REAL blocker (OriginLacksCargo), NOT WaitingForPartner.
            Assert.Equal(RouteDispatchEvaluator.EligibilityFailureKind.OriginLacksCargo, elig.Kind);
        }

        private sealed class ShortCargoEnv : IRouteRuntimeEnvironment
        {
            public bool IsCareer { get; set; }
            public bool TryResolveEndpoint(RouteEndpoint endpoint, out string reason) { reason = string.Empty; return true; }
            public bool TryResolveEndpointVessel(RouteEndpoint endpoint, out Vessel vessel, out string reason) { vessel = null; reason = string.Empty; return true; }
            public bool OriginHasCargo(Route route, out string lackingResource) { lackingResource = "LiquidFuel"; return false; }
            public bool KscFundsAvailable(Route route, out double shortfall) { shortfall = 0.0; return true; }
            public bool DestinationHasCapacity(Route route, out string fullResource) { fullResource = string.Empty; return true; }
            public bool RouteHasValidSourcesInErs(Route route) => true;
        }

        // ==================================================================
        // (7) Bypass set: every non-ghost-driving partner status bypasses
        // ==================================================================

        // catches: the bypass set being narrower than !GhostDriving - a partner in
        // ANY of Paused / EndpointLost / MissingSourceRecording / SourceChanged will
        // never advance CompletedCycles, so holding on it would stall this route
        // forever. Each must bypass the constraint (route dispatches on its own
        // schedule), while a ghost-driving partner that has not advanced HOLDS.
        // RouteStatus is internal, so the cases are driven by name (a public Theory
        // signature cannot expose the internal enum) and parsed inside.
        [Theory]
        [InlineData("Paused", true)]
        [InlineData("EndpointLost", true)]
        [InlineData("MissingSourceRecording", true)]
        [InlineData("SourceChanged", true)]
        [InlineData("Active", false)]
        [InlineData("InTransit", false)]
        [InlineData("WaitingForResources", false)]
        [InlineData("WaitingForFunds", false)]
        [InlineData("DestinationFull", false)]
        public void PartnerStatus_BypassSet_MatchesNonGhostDriving(
            string partnerStatusName, bool expectBypass)
        {
            RouteStatus partnerStatus = (RouteStatus)Enum.Parse(typeof(RouteStatus), partnerStatusName);
            // Partner in the given status with NO completed cycle; the route already
            // consumed the partner's baseline (cursor 0, partner completed 0), so the
            // alternation gate WOULD hold a ghost-driving partner. A non-ghost-driving
            // partner bypasses.
            var partner = BuildLinkedRoute("route-p", linkedRouteId: "route-q",
                dispatchPriority: 5, status: partnerStatus, completedCycles: 0);
            // The route under test is the NON-seed (higher priority value) so the
            // deadlock-seed bypass does NOT mask the result.
            var q = BuildLinkedRoute("route-q", linkedRouteId: "route-p",
                dispatchPriority: 9, completedCycles: 0, lastConsumedPartnerCycle: 0);
            RouteStore.AddRoute(partner);
            RouteStore.AddRoute(q);

            bool satisfied = RouteDispatchEvaluator.PartnerConstraintSatisfied(q, out _);

            // expectBypass true -> constraint satisfied (dispatch allowed);
            // expectBypass false -> held (a ghost-driving partner that has not advanced).
            Assert.Equal(expectBypass, satisfied);
        }
    }
}
