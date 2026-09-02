using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using Parsek.InGameTests.Helpers;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// The roadmap's Tier C item 10 subject: TWO STORED routes competing for ONE
    /// physical source, driven through the production
    /// <see cref="RouteOrchestrator.Tick(double)"/>.
    ///
    /// <para><b>What was already covered, and what was not.</b>
    /// <c>LogisticsMultiOriginRuntimeTests.Escrow_CompetingRouteSeesReservation_Holds</c>
    /// proves the GATE and the M6 hold TOKEN, but it calls
    /// <c>RouteStore.ReserveCargo</c> by hand for a synthetic route A and evaluates
    /// <c>env.OriginHasCargo</c> by hand for a synthetic route B; neither route is
    /// ever added to <see cref="RouteStore"/> and neither is ever ticked. These cells
    /// close exactly that gap: both routes are STORED, the reservation is produced by
    /// the real <c>ReserveCycleEscrow</c> at a real multi-stop dispatch, and the
    /// competitor's hold comes from its OWN crossing inside the same production tick.
    /// </para>
    ///
    /// <para><b>Why route A must be MULTI-STOP.</b> On the single-stop path
    /// <c>ReserveCycleEscrow</c> and the window's <c>ReleaseWindowEscrow</c> are both
    /// inside one <c>EmitLoopCycle</c>, so the escrow nets to zero before any
    /// competing route is reached and contention is unobservable by construction.
    /// Only a multi-stop route holds across the dispatch-to-window-phase gap: a tick
    /// landing between dock A and dock B fires window A only, leaves
    /// <c>cycleLastDockReached</c> false, so the cycle-complete
    /// <c>RouteStore.DropRouteEscrow</c> does not run and window B's portion stays
    /// reserved across ticks.</para>
    ///
    /// <para><b>THE PRE-IMAGE IDENTITY, AND WHY THERE ARE TWO CELLS.</b> The reserve
    /// is the SUMMED pickup manifest <c>M</c>; each window's release is that window's
    /// own manifest and fires TOGETHER WITH a physical debit of the SAME manifest. So
    /// a competitor's netted availability is
    /// <c>NettedAvailable(S0 - fired, M - fired) == NettedAvailable(S0 - M, 0)</c> -
    /// INVARIANT from A's dispatch through A's cycle completion (pinned headlessly by
    /// <c>RouteCargoEscrowTests.NettedAvailable_*</c>). The escrow is an exact
    /// pre-image of the debit: it cannot double-claim and it cannot over-block. Two
    /// consequences shape these cells:
    /// <list type="number">
    /// <item>Route B CANNOT become eligible from the release at A's window - the same
    /// amount is physically taken at that instant. What DOES change is the hold CAUSE,
    /// which flips <c>escrow</c> -&gt; <c>physical</c> on unchanged numbers. That is
    /// cell 1, and it is the invariant driven rather than asserted.</item>
    /// <item>B becomes eligible only from a release that takes NO cargo. The
    /// player-reachable one is route removal:
    /// <c>RouteStore.RemoveRoute</c> -&gt; <c>DropRouteEscrow</c> mid-cycle. That is
    /// cell 2. It cannot share cell 1's timeline: cell 1 consumes the hold by debiting
    /// it, cell 2 needs the hold released un-debited, and one route A cannot do
    /// both.</item>
    /// </list></para>
    ///
    /// <para><b>Determinism, and what is emulated.</b> The route TOPOLOGY and the loop
    /// CLOCK are synthetic (the builder shapes are ported from
    /// <c>LogisticsMultiOriginRuntimeTests</c>, and the loop unit arrives through
    /// <c>RouteOrchestrator.LoopUnitResolverForTesting</c>) - that is the whole of the
    /// emulation. NOT emulated: both routes are really in <see cref="RouteStore"/>,
    /// the tick is the production <c>RouteOrchestrator.Tick</c>, the eligibility gate
    /// is the production <c>RouteDispatchEvaluator.CheckEligibility</c> over a live
    /// <see cref="LiveRouteRuntimeEnvironment"/>, the source is a live auto-spawned
    /// vessel, and every debit is a real resource write. Processing ORDER is pinned by
    /// the production priority rule: <c>CompareRoutesForTick</c> sorts ascending on
    /// <c>DispatchPriority</c>, so A (0) is always processed before B (1) within a
    /// tick - no ordering luck.</para>
    ///
    /// <para><b>The numeric band, stated because a fixture outside it must SKIP.</b>
    /// With A's windows at 4 + 6 (M = 10) and B needing 8, the source's stored
    /// LiquidFuel <c>S0</c> must satisfy: A eligible (<c>S0 &gt;= 10</c>); the phase-2
    /// short ESCROW-caused (raw <c>S0 - 4 &gt;= 8</c>, i.e. <c>S0 &gt;= 12</c>) and
    /// actually short (netted <c>S0 - 10 &lt; 8</c>, i.e. <c>S0 &lt; 18</c>). The band
    /// is therefore [12, 18) and the fixture is capped to 15. The cap only applies on
    /// the SPAWN path, so a reused pre-existing vessel outside the band SKIPs with its
    /// measured amount named.</para>
    ///
    /// <para><b>Two deliberate log seams.</b> <c>VerboseOverrideForTesting</c> forces
    /// Verbose on for the driven window (the <c>ReserveCycleEscrow</c> and
    /// <c>short-cause=</c> lines are Verbose; the BLOCKED / DropRouteEscrow / removal /
    /// cycle lines are Info and would land regardless), and
    /// <c>ResetRateLimitsForTesting</c> runs before every tick because both
    /// <c>short-cause</c> readings share the rate-limit key
    /// <c>pickup-source-short-cause-&lt;routeId&gt;</c> and the ticks execute inside one
    /// frame - without the reset the second reading would be suppressed and the
    /// cause-flip would be invisible. Both are restored in finally.</para>
    ///
    /// <para><b>Isolation.</b> Isolated tier only: spawns a vessel, wipes and rebuilds
    /// <see cref="RouteStore"/>, commits trees into <c>RecordingStore</c>, arms the
    /// orchestrator seams and writes live resource state. Everything is restored in
    /// finally; the per-test baseline quickload is the outer net.</para>
    /// </summary>
    public sealed class RouteEscrowContentionInGameTest
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        private const string LiquidFuelName = "LiquidFuel";

        // Route A's two pickup windows on the SHARED source; their sum is the
        // dispatch-time reservation M.
        private const double WindowAmountA0 = 4.0;
        private const double WindowAmountA1 = 6.0;
        private const double ReservedTotalA = WindowAmountA0 + WindowAmountA1;
        // Route B's single pickup window, sized so it is covered by the raw stored
        // amount after A's window 0 debits but NOT by the escrow-netted amount.
        private const double NeedB = 8.0;

        // Fixture shaping: target 15 LF, admissible band [12, 18) - see the class
        // remarks for the derivation of both bounds.
        private const double SourceTargetStored = 15.0;
        private const double SourceBandMin = 12.0;
        private const double SourceBandMaxExclusive = 18.0;
        private const double FixtureMinFreeCapacity = 1.0;
        private const double ResourceTolerance = 0.01;

        // Route A's deterministic span clock: docks at 1500 (pickup w0), 2000
        // (pickup w1) and 2500 (delivery, the LAST dock - never reached by either
        // cell, so A's cycle never completes and its escrow is never swept by the
        // cycle-complete DropRouteEscrow).
        private const double SpanStartA = 1000.0;
        private const double SpanEndA = 3000.0;
        private const double CadenceA = SpanEndA - SpanStartA;
        private const double DockUtA0 = 1500.0;
        private const double DockUtA1 = 2000.0;
        private const double DockUtADelivery = 2500.0;

        // Route B's own, much SHORTER span clock, so B gets a fresh owed crossing at
        // every tick this file chooses (a blocked crossing snaps B's cursor to its
        // dock cycle, so a same-cadence B would not re-cross).
        private const double SpanStartB = 1000.0;
        private const double SpanEndB = 1100.0;
        private const double CadenceB = SpanEndB - SpanStartB;
        private const double DockUtB = 1050.0;

        // TICK 1 lands after A's dock 0 (1500) and before A's dock 1 (2000), and
        // after B's dock phase within B's cycle 5 (loopUT 1060 >= 1050).
        private const double Tick1UT = 1560.0;
        // TICK 2 lands after A's dock 1 (2000) and before A's delivery dock (2500),
        // and after B's dock phase within B's cycle 10 (loopUT 1060 >= 1050).
        private const double Tick2UT = 2060.0;

        private const string IsolatedOnlyBatchSkipReason =
            "Isolated-run only - spawns a source vessel, wipes and rebuilds RouteStore, commits " +
            "recording trees, arms the RouteOrchestrator loop-unit seam and the ParsekLog verbose / " +
            "rate-limit test overrides, and writes live vessel resource state under live KSP statics. " +
            "Use Run All + Isolated or the row play button in a disposable FLIGHT session.";

        // ==================================================================
        // Cell 1: hold across the gap, then the cause flips escrow -> physical
        // ==================================================================

        [InGameTest(Category = "RouteEscrowContention", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier C item 10 phases 1-3: TWO STORED routes on ONE physical source, driven " +
                "through the production RouteOrchestrator.Tick. Tick 1 - multi-stop route A dispatches and " +
                "RESERVES its summed pickup manifest (ReserveCycleEscrow reservedSources=1), fires only its " +
                "first window, and the still-reserved second window blocks competitor B on its OWN crossing " +
                "with the M6 escrow token source-reserved:<pid>:<name>:<resource>:<routeA> and " +
                "short-cause=escrow naming route A. Tick 2 - A's second window fires, releasing the hold and " +
                "PHYSICALLY DEBITING the same amount in the same instant, so B stays blocked on UNCHANGED " +
                "numbers with the cause flipped to physical. That flip is the reserve/release pre-image " +
                "invariant driven live: the escrow withholds exactly what the debit takes - no double-claim, " +
                "no over-block. Auto-spawns the shared source; skips with a named reason when it cannot be " +
                "provided or falls outside the [12,18) LiquidFuel band the phases need")]
        public IEnumerator Escrow_MultiStopHoldBlocksStoredCompetitor_ThenCauseFlipsToPhysical()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            var fixture = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                IEnumerator ensure = EnsureSharedSource(fixture);
                while (ensure.MoveNext())
                    yield return ensure.Current;

                Vessel source = fixture.Vessel;
                double storedBefore = ProbeSourceStored(source);
                var arrange = new ContentionArrangement("hold-then-cause-flip", source);

                try
                {
                    arrange.Arm();

                    // ---- TICK 1: A dispatches + reserves + fires window 0; B blocks
                    //      on the still-held window-1 portion. ----
                    arrange.RunTick(Tick1UT);

                    double reservedAfterTick1 = ReservedAgainstB(arrange);
                    InGameAssert.ApproxEqual(WindowAmountA1, reservedAfterTick1, ResourceTolerance,
                        "After tick 1 route A must still hold its UN-FIRED window's portion " +
                        $"({WindowAmountA1.ToString("R", IC)} LF) against route B; " +
                        $"OtherRoutesReservedFor read {reservedAfterTick1.ToString("R", IC)}. A zero here " +
                        "means the cycle completed (the delivery dock was reached) and the hold was swept");
                    InGameAssert.IsTrue(RouteStore.HasEscrow(arrange.RouteAId),
                        "Route A must be holding escrow across the dispatch-to-window gap after tick 1");

                    // PHASE 1 (reserved), from the production reserve line.
                    arrange.AssertLogged(
                        "ReserveCycleEscrow: route " + arrange.ShortA + " cycle=cycle-0 context=dispatch "
                        + "reservedSources=1",
                        "the dispatch-time reserve line naming ONE summed source");
                    // A fired only its first window (the cycle is still open).
                    arrange.AssertLogged("LoopRoute(multi): route " + arrange.ShortA + " cycle=cycle-0",
                        "route A's multi-stop cycle line");
                    double storedAfterTick1 = ProbeSourceStored(source);
                    InGameAssert.ApproxEqual(storedBefore - WindowAmountA0, storedAfterTick1, ResourceTolerance,
                        $"Only route A's FIRST window ({WindowAmountA0.ToString("R", IC)} LF) may have been " +
                        $"debited on tick 1 (before={storedBefore.ToString("R", IC)} " +
                        $"after={storedAfterTick1.ToString("R", IC)})");

                    // PHASE 2 (blocked, escrow-caused), from B's OWN crossing.
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(arrange.RouteBId, out Route bAfterTick1),
                        "Route B disappeared from the store during tick 1");
                    InGameAssert.AreEqual(0, bAfterTick1.CompletedCycles,
                        "Route B must NOT have completed a cycle while route A holds the source");
                    InGameAssert.AreEqual(1, bAfterTick1.SkippedCycles,
                        "Route B's blocked crossing must have bumped SkippedCycles exactly once");
                    arrange.AssertLogged(
                        "LoopRoute: route " + arrange.ShortB + " cycle=cycle-0 BLOCKED "
                        + "kind=OriginLacksCargo reason=source-reserved:"
                        + source.persistentId.ToString(IC),
                        "route B's BLOCKED line carrying the M6 escrow hold token for the shared source");
                    arrange.AssertLogged(
                        "PickupSourcesHaveCargo: route " + arrange.ShortB + " short-cause=escrow "
                        + "pid=" + source.persistentId.ToString(IC),
                        "the escrow-cause classification line for route B");
                    arrange.AssertLogged("reservingRouteId=" + arrange.ShortA,
                        "the classification line must name route A as the reserving route");

                    // ---- TICK 2: A's window 1 fires - release AND debit in the same
                    //      instant - so B's availability is unchanged and only the
                    //      CAUSE moves. ----
                    arrange.ClearLog();
                    arrange.RunTick(Tick2UT);

                    double storedAfterTick2 = ProbeSourceStored(source);
                    InGameAssert.ApproxEqual(storedBefore - ReservedTotalA, storedAfterTick2, ResourceTolerance,
                        "After tick 2 route A must have physically debited BOTH windows " +
                        $"({ReservedTotalA.ToString("R", IC)} LF total; " +
                        $"after={storedAfterTick2.ToString("R", IC)})");
                    arrange.AssertLogged(
                        "ReleaseWindowEscrow: route " + arrange.ShortA + " cycle=cycle-0 pid="
                        + source.persistentId.ToString(IC),
                        "the window-1 release line against the same source pid the reserve keyed on");
                    double reservedAfterTick2 = ReservedAgainstB(arrange);
                    InGameAssert.ApproxEqual(0.0, reservedAfterTick2, ResourceTolerance,
                        "After route A's last pickup window fires, its reservation must net to zero " +
                        $"(read {reservedAfterTick2.ToString("R", IC)})");

                    // PHASE 3 (cause flip). The numbers B sees did NOT move: the netted
                    // availability during the hold equals the raw availability after the
                    // debit. Only the classification changes.
                    double nettedDuringHold = RoutePickupSourceGate.NettedAvailable(
                        storedAfterTick1, WindowAmountA1);
                    InGameAssert.ApproxEqual(nettedDuringHold, storedAfterTick2, ResourceTolerance,
                        "THE PRE-IMAGE INVARIANT: what route B could rely on WHILE route A held " +
                        $"({nettedDuringHold.ToString("R", IC)}) must equal the raw amount left AFTER route " +
                        $"A's debit ({storedAfterTick2.ToString("R", IC)}) - the escrow withholds exactly " +
                        "what the debit takes");
                    arrange.AssertLogged(
                        "PickupSourcesHaveCargo: route " + arrange.ShortB + " short-cause=physical "
                        + "pid=" + source.persistentId.ToString(IC),
                        "route B's hold cause must have FLIPPED to physical once the reserved cargo was " +
                        "actually taken");
                    InGameAssert.IsTrue(RouteStore.TryGetRoute(arrange.RouteBId, out Route bAfterTick2),
                        "Route B disappeared from the store during tick 2");
                    InGameAssert.AreEqual(0, bAfterTick2.CompletedCycles,
                        "Route B must still not have completed a cycle - the cargo went to route A");
                    InGameAssert.AreEqual(2, bAfterTick2.SkippedCycles,
                        "Route B's second blocked crossing must have bumped SkippedCycles to 2");

                    ParsekLog.Info("TestRunner",
                        "EscrowContention: cell=hold-then-cause-flip reserved=1 blocked=1 "
                        + "causeAfterWindow=physical eligibleAfterRouteRemoval=0"
                        + " routeA=" + arrange.ShortA + " routeB=" + arrange.ShortB
                        + " sourcePid=" + source.persistentId.ToString(IC)
                        + " stored=" + storedBefore.ToString("R", IC)
                        + " heldAcrossGap=" + WindowAmountA1.ToString("R", IC)
                        + " nettedDuringHold=" + nettedDuringHold.ToString("R", IC)
                        + " rawAfterDebit=" + storedAfterTick2.ToString("R", IC));
                }
                finally
                {
                    arrange.Teardown();
                }
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(fixture);
            }
            yield break;
        }

        // ==================================================================
        // Cell 2: removing the holder mid-cycle frees the source
        // ==================================================================

        [InGameTest(Category = "RouteEscrowContention", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = IsolatedOnlyBatchSkipReason,
            Description = "Tier C item 10 phases 1-2 then 4: the same two STORED routes on one physical " +
                "source, but instead of letting route A's second window take the reserved cargo, route A is " +
                "REMOVED mid-cycle - the player-reachable release that frees the hold WITHOUT debiting " +
                "anything (RouteStore.RemoveRoute -> DropRouteEscrow). Competitor B, blocked with " +
                "short-cause=escrow on the tick before, is then ELIGIBLE on its next crossing and physically " +
                "picks the cargo up. This is the only shape in which a release makes a competitor eligible: " +
                "a release at A's own window is accompanied by the debit it was the pre-image of, so it " +
                "cannot. Auto-spawns the shared source; skips with a named reason when it cannot be provided " +
                "or falls outside the [12,18) LiquidFuel band the phases need")]
        public IEnumerator Escrow_RouteRemovalMidCycleReleasesHold_CompetitorBecomesEligible()
        {
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            var fixture = new UnloadedFuelVesselFixture.EnsureResult();
            try
            {
                IEnumerator ensure = EnsureSharedSource(fixture);
                while (ensure.MoveNext())
                    yield return ensure.Current;

                Vessel source = fixture.Vessel;
                double storedBefore = ProbeSourceStored(source);
                var arrange = new ContentionArrangement("removal-releases-hold", source);

                try
                {
                    arrange.Arm();

                    // ---- TICK 1: identical to cell 1 - A reserves and holds, B blocks. ----
                    arrange.RunTick(Tick1UT);

                    InGameAssert.ApproxEqual(WindowAmountA1, ReservedAgainstB(arrange), ResourceTolerance,
                        "After tick 1 route A must hold its un-fired window's portion against route B");
                    arrange.AssertLogged(
                        "ReserveCycleEscrow: route " + arrange.ShortA + " cycle=cycle-0 context=dispatch "
                        + "reservedSources=1",
                        "the dispatch-time reserve line naming ONE summed source");
                    arrange.AssertLogged(
                        "LoopRoute: route " + arrange.ShortB + " cycle=cycle-0 BLOCKED "
                        + "kind=OriginLacksCargo reason=source-reserved:"
                        + source.persistentId.ToString(IC),
                        "route B's BLOCKED line carrying the M6 escrow hold token");
                    arrange.AssertLogged(
                        "PickupSourcesHaveCargo: route " + arrange.ShortB + " short-cause=escrow",
                        "the escrow-cause classification line for route B");
                    double storedAfterTick1 = ProbeSourceStored(source);

                    // ---- REMOVE route A mid-cycle. Its un-fired window never debits,
                    //      so the drop frees cargo that is still physically there. ----
                    arrange.ClearLog();
                    InGameAssert.IsTrue(RouteStore.RemoveRoute(arrange.RouteAId),
                        "RemoveRoute must report the removal of the holding route A");
                    arrange.RouteARemoved = true;
                    arrange.AssertLogged(
                        "DropRouteEscrow routeId=" + arrange.ShortA + " droppedPids=1 droppedResourceEntries=1",
                        "removing the holder must DROP its live reservation (the no-cargo release)");
                    arrange.AssertLogged("Route " + arrange.ShortA + " removed",
                        "the store's removal line");
                    InGameAssert.IsFalse(RouteStore.HasEscrow(arrange.RouteAId),
                        "Route A must hold no escrow once removed");
                    InGameAssert.ApproxEqual(0.0, ReservedAgainstB(arrange), ResourceTolerance,
                        "Route B must see nothing reserved against it once route A is gone");
                    InGameAssert.ApproxEqual(storedAfterTick1, ProbeSourceStored(source), ResourceTolerance,
                        "Removing route A must NOT move any cargo - the hold is dropped, not debited");

                    // ---- TICK 2: B's next crossing. The source physically covers it
                    //      now that nothing is reserved, so B dispatches and picks up. ----
                    int beforeLedger = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                    arrange.RunTick(Tick2UT);

                    InGameAssert.IsTrue(RouteStore.TryGetRoute(arrange.RouteBId, out Route bAfterTick2),
                        "Route B disappeared from the store during tick 2");
                    InGameAssert.AreEqual(1, bAfterTick2.CompletedCycles,
                        "Route B must complete exactly one cycle once the hold is gone " +
                        $"(SkippedCycles={bAfterTick2.SkippedCycles.ToString(IC)})");
                    InGameAssert.AreEqual(1, bAfterTick2.SkippedCycles,
                        "Route B's skip count must stay at the ONE blocked crossing from tick 1");
                    double storedAfterTick2 = ProbeSourceStored(source);
                    InGameAssert.ApproxEqual(storedAfterTick1 - NeedB, storedAfterTick2, ResourceTolerance,
                        $"Route B must physically pick up its {NeedB.ToString("R", IC)} LF once eligible " +
                        $"(before={storedAfterTick1.ToString("R", IC)} " +
                        $"after={storedAfterTick2.ToString("R", IC)})");
                    CountNewPickupRows(beforeLedger, arrange.RouteBId,
                        out int dispatchedB, out int pickedUpB);
                    InGameAssert.AreEqual(1, dispatchedB,
                        "Exactly one RouteDispatched row for route B's newly-eligible cycle");
                    InGameAssert.AreEqual(1, pickedUpB,
                        "Exactly one RouteCargoPickedUp row for route B's newly-eligible cycle");
                    arrange.AssertLogged(
                        "EmitLoopCycle: route " + arrange.ShortB + " cycle=cycle-1",
                        "route B's own cycle-emission line (its cycleId advanced past the blocked cycle-0)");

                    ParsekLog.Info("TestRunner",
                        "EscrowContention: cell=removal-releases-hold reserved=1 blocked=1 "
                        + "causeAfterWindow=escrow eligibleAfterRouteRemoval=1"
                        + " routeA=" + arrange.ShortA + " routeB=" + arrange.ShortB
                        + " sourcePid=" + source.persistentId.ToString(IC)
                        + " stored=" + storedBefore.ToString("R", IC)
                        + " freedByRemoval=" + WindowAmountA1.ToString("R", IC)
                        + " pickedUpByB=" + NeedB.ToString("R", IC));
                }
                finally
                {
                    arrange.Teardown();
                }
            }
            finally
            {
                UnloadedFuelVesselFixture.Cleanup(fixture);
            }
            yield break;
        }

        // ==================================================================
        // Shared arrangement (store, trees, seams, log capture, teardown)
        // ==================================================================

        /// <summary>
        /// Owns everything the two cells share: the two stored routes, their backing
        /// committed trees, the route-scoped loop-unit resolver seam, the log capture
        /// and the log test overrides - plus the exact-restore teardown. Constructed
        /// AFTER the source vessel is provisioned (the last yielding step), so the
        /// whole arrange / tick / assert / teardown sequence runs yield-free on the
        /// main thread and the background 1 Hz scenario tick can never interleave with
        /// an armed seam or a wiped store (the same re-entry discipline
        /// <c>LogisticsMultiOriginRuntimeTests</c> documents).
        /// </summary>
        private sealed class ContentionArrangement
        {
            internal readonly string Label;
            internal readonly string RouteAId;
            internal readonly string RouteBId;
            internal readonly string ShortA;
            internal readonly string ShortB;
            /// <summary>The shared source's live pid - the key the reserve, the
            /// release and the gate's net all agree on.</summary>
            internal readonly uint SourcePid;
            internal bool RouteARemoved;

            private readonly Vessel source;
            private readonly string treeAId;
            private readonly string treeBId;
            private readonly List<string> captured = new List<string>();
            private readonly List<Recording> committedAdded = new List<Recording>();
            private List<Route> preExistingRoutes;
            private List<KeyValuePair<ProtoPartResourceSnapshot, double>> sourceSnapshot;
            private List<KeyValuePair<PartResource, double>> deliverySnapshot;
            private Func<Route, double, GhostPlaybackLogic.LoopUnit?> previousResolver;
            private Action<string> previousObserver;
            private bool? previousVerboseOverride;
            private bool treesAdded, storeWiped, resolverArmed, logArmed;

            internal ContentionArrangement(string label, Vessel source)
            {
                Label = label;
                this.source = source;
                SourcePid = source != null ? source.persistentId : 0u;
                // The prefixes are EXACTLY 8 characters, which is RouteIds.Short's
                // truncation width - so ShortA / ShortB are the stable literals
                // "escrowA-" / "escrowB-" rather than per-run noise, and every asserted
                // line (and every H60 token) tells the holder from the competitor with
                // no ambiguity. A shorter shared prefix would truncate both to the same
                // 8 chars and the reserving-route segment of the M6 hold token could
                // not be pinned to A.
                RouteAId = "escrowA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                RouteBId = "escrowB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                ShortA = RouteIds.Short(RouteAId);
                ShortB = RouteIds.Short(RouteBId);
                treeAId = "esc-treeA-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                treeBId = "esc-treeB-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            internal void Arm()
            {
                Vessel deliveryTarget = FlightGlobals.ActiveVessel ?? source;

                sourceSnapshot = SnapshotProtoLiquidFuel(source);
                // Route A's delivery dock is never reached by either cell, but the
                // eligibility gate still runs DestinationHasCapacity at A's dispatch,
                // so the target must be able to hold the manifest. Snapshot first so
                // the teardown restores whatever the headroom drain moved.
                deliverySnapshot = SnapshotLoadedLiquidFuel(deliveryTarget);
                if (!DestinationHeadroomFixture.TryEnsureDestinationHeadroom(
                        deliveryTarget, LiquidFuelName, ReservedTotalA, out _, out string headroomSkip))
                    InGameAssert.Skip(headroomSkip);

                RecordingTree treeA = BuildBackingTree(treeAId, SpanStartA, SpanEndA);
                RecordingTree treeB = BuildBackingTree(treeBId, SpanStartB, SpanEndB);
                RecordingStore.AddCommittedTreeForTesting(treeA);
                RecordingStore.AddCommittedTreeForTesting(treeB);
                treesAdded = true;
                foreach (RecordingTree tree in new[] { treeA, treeB })
                {
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        if (rec == null) continue;
                        RecordingStore.AddCommittedInternal(rec);
                        committedAdded.Add(rec);
                    }
                }

                preExistingRoutes = SnapshotRoutes();
                RouteStore.ResetForTesting();
                storeWiped = true;
                RouteStore.AddRoute(BuildMultiStopHolderRoute(RouteAId, treeAId, source, deliveryTarget));
                RouteStore.AddRoute(BuildSingleStopCompetitorRoute(RouteBId, treeBId, source));
                InGameAssert.IsTrue(RouteStore.TryGetRoute(RouteAId, out _),
                    "The multi-stop holder route A was not stored");
                InGameAssert.IsTrue(RouteStore.TryGetRoute(RouteBId, out _),
                    "The competitor route B was not stored");

                GhostPlaybackLogic.LoopUnit unitA = new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: 0, memberIndices: new[] { 0 },
                    spanStartUT: SpanStartA, spanEndUT: SpanEndA,
                    cadenceSeconds: CadenceA, phaseAnchorUT: SpanStartA);
                GhostPlaybackLogic.LoopUnit unitB = new GhostPlaybackLogic.LoopUnit(
                    ownerIndex: 1, memberIndices: new[] { 1 },
                    spanStartUT: SpanStartB, spanEndUT: SpanEndB,
                    cadenceSeconds: CadenceB, phaseAnchorUT: SpanStartB);

                previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, RouteAId, StringComparison.Ordinal))
                        return unitA;
                    if (r != null && string.Equals(r.Id, RouteBId, StringComparison.Ordinal))
                        return unitB;
                    return previousResolver != null
                        ? previousResolver(r, ut)
                        : (GhostPlaybackLogic.LoopUnit?)null;
                };
                resolverArmed = true;

                previousObserver = ParsekLog.TestObserverForTesting;
                ParsekLog.TestObserverForTesting = line =>
                {
                    captured.Add(line);
                    previousObserver?.Invoke(line);
                };
                // The reserve line and both short-cause classification lines are
                // Verbose; force them on so a host with verbose logging off still
                // produces the phase tokens this cell (and the H60 lane) reads.
                previousVerboseOverride = ParsekLog.VerboseOverrideForTesting;
                ParsekLog.VerboseOverrideForTesting = true;
                logArmed = true;
            }

            /// <summary>
            /// One production tick, with the rate-limit state cleared first. Both
            /// <c>short-cause</c> readings share the key
            /// <c>pickup-source-short-cause-&lt;routeBId&gt;</c> and the ticks run
            /// inside a single frame, so without the reset the second reading is
            /// suppressed and the escrow -&gt; physical cause flip is invisible.
            /// </summary>
            internal void RunTick(double ut)
            {
                ParsekLog.ResetRateLimitsForTesting();
                ParsekLog.Verbose("TestRunner",
                    $"EscrowContention: cell={Label} tick ut={ut.ToString("R", IC)} "
                    + $"routeA={ShortA} routeB={ShortB}");
                RouteOrchestrator.Tick(ut);
            }

            internal void ClearLog()
            {
                captured.Clear();
            }

            internal void AssertLogged(string needle, string what)
            {
                for (int i = 0; i < captured.Count; i++)
                {
                    if (captured[i] != null
                        && captured[i].IndexOf(needle, StringComparison.Ordinal) >= 0)
                        return;
                }
                InGameAssert.Fail(
                    $"Expected {what} - no captured log line contained '{needle}' "
                    + $"(captured={captured.Count.ToString(IC)} lines)");
            }

            internal void Teardown()
            {
                if (logArmed)
                {
                    ParsekLog.VerboseOverrideForTesting = previousVerboseOverride;
                    ParsekLog.TestObserverForTesting = previousObserver;
                    ParsekLog.ResetRateLimitsForTesting();
                }
                if (resolverArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;
                if (storeWiped)
                {
                    RouteStore.ResetForTesting();
                    if (preExistingRoutes != null)
                        for (int i = 0; i < preExistingRoutes.Count; i++)
                            if (preExistingRoutes[i] != null)
                                RouteStore.AddRoute(preExistingRoutes[i]);
                }
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (treesAdded)
                {
                    RemoveCommittedTree(treeAId);
                    RemoveCommittedTree(treeBId);
                    MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                }
                try
                {
                    RestoreProtoLiquidFuel(sourceSnapshot);
                    RestoreLoadedLiquidFuel(deliverySnapshot);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"EscrowContention cell={Label} cleanup: failed to restore resource state "
                        + $"({ex.GetType().Name}: {ex.Message})");
                }
            }
        }

        // ==================================================================
        // Fixture provisioning + probes
        // ==================================================================

        private static IEnumerator EnsureSharedSource(UnloadedFuelVesselFixture.EnsureResult fixture)
        {
            IEnumerator ensure = UnloadedFuelVesselFixture.EnsureUnloadedLiquidFuelVessel(
                SourceTargetStored, FixtureMinFreeCapacity, fixture,
                excludeReusePids: null, capStoredLf: SourceTargetStored);
            while (ensure.MoveNext())
                yield return ensure.Current;

            if (fixture.Vessel == null)
                InGameAssert.Skip(
                    "PRECONDITION: could not provide the shared source vessel (>= "
                    + $"{SourceTargetStored.ToString("R", IC)} LF). Provide a fueled PRELAUNCH pad rocket");

            double stored = ProbeSourceStored(fixture.Vessel);
            if (stored < SourceBandMin || stored >= SourceBandMaxExclusive)
                InGameAssert.Skip(
                    $"Shared source '{fixture.Vessel.vesselName}' holds {stored.ToString("R", IC)} LF, "
                    + $"outside the [{SourceBandMin.ToString("R", IC)}, "
                    + $"{SourceBandMaxExclusive.ToString("R", IC)}) band the contention phases need "
                    + "(below it route A cannot dispatch or the block reads PHYSICAL rather than ESCROW; "
                    + "at or above it the source covers both routes and nothing contends). The spawn path "
                    + "caps the amount exactly; a REUSED pre-existing vessel does not take the cap");
        }

        private static double ProbeSourceStored(Vessel v)
        {
            return new LiveOriginCargoProbe(v, false).ProbeResourceStored(LiquidFuelName);
        }

        /// <summary>
        /// What route B may NOT rely on: the sum every OTHER route holds on the shared
        /// source's (pid, LiquidFuel). This is the exact quantity
        /// <see cref="LiveRouteRuntimeEnvironment"/>'s netted reader subtracts, read
        /// through the same production accessor.
        /// </summary>
        private static double ReservedAgainstB(ContentionArrangement arrange)
        {
            return RouteStore.OtherRoutesReservedFor(
                arrange.RouteBId, arrange.SourcePid, LiquidFuelName);
        }

        private static void CountNewPickupRows(int fromIndex, string routeId,
            out int dispatchedCount, out int pickedUpCount)
        {
            dispatchedCount = 0;
            pickedUpCount = 0;
            var actions = Ledger.Actions;
            if (actions == null) return;
            for (int i = fromIndex; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatchedCount++;
                else if (a.Type == GameActionType.RouteCargoPickedUp) pickedUpCount++;
            }
        }

        // ==================================================================
        // Route + tree builders (shapes ported from LogisticsMultiOriginRuntimeTests)
        // ==================================================================

        private static RouteEndpoint EndpointForVessel(Vessel v)
        {
            return new RouteEndpoint
            {
                VesselPersistentId = v != null ? v.persistentId : 0u,
                BodyName = v != null && v.mainBody != null ? v.mainBody.bodyName : "Kerbin",
                IsSurface = false,
            };
        }

        /// <summary>
        /// Route A: the HOLDER. Two pickup windows on the SAME shared source (so the
        /// gate groups them into ONE source and <c>ReserveCycleEscrow</c> reserves
        /// their SUM once, which is what makes the residual observable) plus a
        /// delivery stop carrying the LAST dock. Priority 0 so
        /// <c>CompareRoutesForTick</c> always processes it before the competitor.
        /// </summary>
        private static Route BuildMultiStopHolderRoute(
            string routeId, string treeId, Vessel source, Vessel deliveryTarget)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Escrow Holder In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = false,
                DispatchPriority = 0,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = DockUtADelivery,
                DockMemberRecordingId = "dockedLast",
                LoopAnchorUT = SpanStartA,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = CadenceA,
                DispatchInterval = CadenceA,
                NextDispatchUT = Tick1UT + CadenceA,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                Origin = EndpointForVessel(source),
                RecordingIds = new List<string> { "launch", "mid", "dockedLast" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "mid", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedLast", TreeId = treeId },
                },
                Stops = new List<RouteStop>
                {
                    PickupStop(source, WindowAmountA0, 0, DockUtA0),
                    PickupStop(source, WindowAmountA1, 1, DockUtA1),
                    new RouteStop
                    {
                        Endpoint = EndpointForVessel(deliveryTarget),
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                        InventoryPickupManifest = new List<InventoryPayloadItem>(),
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, ReservedTotalA },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 2,
                        RecordedDockUT = DockUtADelivery,
                        LastFiredCycleIndex = -1,
                    },
                },
            };
        }

        /// <summary>
        /// Route B: the COMPETITOR. A single-stop PICKUP-only route on the same shared
        /// source. Single-stop deliberately: B is the route whose CROSSING is the
        /// subject, and the single-stop path is the one an ordinary competing route
        /// takes. Priority 1 so it is always processed after the holder.
        /// </summary>
        private static Route BuildSingleStopCompetitorRoute(string routeId, string treeId, Vessel source)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Escrow Competitor In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = false,
                IsHarvestOrigin = false,
                DispatchPriority = 1,
                BackingMissionTreeId = treeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = DockUtB,
                DockMemberRecordingId = "dockedLast",
                LoopAnchorUT = SpanStartB,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = CadenceB,
                DispatchInterval = CadenceB,
                NextDispatchUT = Tick1UT + CadenceB,
                CompletedCycles = 0,
                SkippedCycles = 0,
                KscDispatchFundsCost = 0.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryCostManifest = new List<InventoryPayloadItem>(),
                Origin = EndpointForVessel(source),
                RecordingIds = new List<string> { "launch", "mid", "dockedLast" },
                SourceRefs = new List<RouteSourceRef>
                {
                    new RouteSourceRef { RecordingId = "launch", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "mid", TreeId = treeId },
                    new RouteSourceRef { RecordingId = "dockedLast", TreeId = treeId },
                },
                Stops = new List<RouteStop> { PickupStop(source, NeedB, 0, DockUtB) },
            };
        }

        private static RouteStop PickupStop(Vessel source, double amount, int segmentIndex, double dockUT)
        {
            return new RouteStop
            {
                Endpoint = EndpointForVessel(source),
                ConnectionKind = RouteConnectionKind.DockingPort,
                PickupManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, amount },
                },
                InventoryPickupManifest = new List<InventoryPayloadItem>(),
                DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal),
                InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                SegmentIndexBefore = segmentIndex,
                RecordedDockUT = dockUT,
                LastFiredCycleIndex = -1,
            };
        }

        // The member recordings only need to be COMMITTED (the ERS membership gate
        // resolves the route's SourceRefs through them); the fire path takes its loop
        // unit from the resolver seam, so the exact composition is not load-bearing.
        private static RecordingTree BuildBackingTree(string treeId, double spanStart, double spanEnd)
        {
            double mid = spanStart + (spanEnd - spanStart) * 0.5;
            var tree = new RecordingTree { Id = treeId, RootRecordingId = "launch" };
            tree.Recordings["launch"] = Leg("launch", "C0", 0, spanStart, mid);
            tree.Recordings["mid"] = Leg("mid", "C0", 1, mid, spanEnd);
            tree.Recordings["dockedLast"] = Leg("dockedLast", "C0", 2, spanEnd, spanEnd + 500.0);
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { "mid" }, new[] { "dockedLast" }, spanEnd));
            return tree;
        }

        private static Recording Leg(string id, string chainId, int chainIndex, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "EscrowContentionTransport",
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
        }

        private static BranchPoint BP(string id, BranchPointType type,
            string[] parents, string[] children, double ut)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = ut,
                SplitCause = type == BranchPointType.Undock ? "UNDOCK" : null,
                MergeCause = type == BranchPointType.Dock ? "DOCK" : null,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };
        }

        // ==================================================================
        // Snapshot / restore (same shapes as LogisticsMultiOriginRuntimeTests)
        // ==================================================================

        private static List<Route> SnapshotRoutes()
        {
            var snapshot = new List<Route>();
            IReadOnlyList<Route> committed = RouteStore.CommittedRoutes;
            for (int i = 0; i < committed.Count; i++)
                if (committed[i] != null)
                    snapshot.Add(committed[i]);
            return snapshot;
        }

        private static void RemoveCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null)
                return;
            var survivors = new List<RecordingTree>(trees.Count);
            for (int i = 0; i < trees.Count; i++)
            {
                RecordingTree t = trees[i];
                if (t != null && string.Equals(t.Id, treeId, StringComparison.Ordinal))
                    continue;
                survivors.Add(t);
            }
            RecordingStore.ClearCommittedTreesInternal();
            for (int i = 0; i < survivors.Count; i++)
                RecordingStore.AddCommittedTreeForTesting(survivors[i]);
        }

        private static List<KeyValuePair<PartResource, double>> SnapshotLoadedLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<PartResource, double>>();
            if (vessel == null || !vessel.loaded || vessel.parts == null) return snapshot;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                snapshot.Add(new KeyValuePair<PartResource, double>(pr, pr.amount));
            }
            return snapshot;
        }

        private static void RestoreLoadedLiquidFuel(List<KeyValuePair<PartResource, double>> snapshot)
        {
            if (snapshot == null) return;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }

        private static List<KeyValuePair<ProtoPartResourceSnapshot, double>> SnapshotProtoLiquidFuel(Vessel vessel)
        {
            var snapshot = new List<KeyValuePair<ProtoPartResourceSnapshot, double>>();
            ProtoVessel pv = vessel != null ? vessel.protoVessel : null;
            if (pv == null || pv.protoPartSnapshots == null) return snapshot;
            for (int i = 0; i < pv.protoPartSnapshots.Count; i++)
            {
                ProtoPartSnapshot pps = pv.protoPartSnapshots[i];
                if (pps == null || pps.resources == null) continue;
                for (int j = 0; j < pps.resources.Count; j++)
                {
                    ProtoPartResourceSnapshot prs = pps.resources[j];
                    if (prs == null) continue;
                    if (!string.Equals(prs.resourceName, LiquidFuelName, StringComparison.Ordinal)) continue;
                    snapshot.Add(new KeyValuePair<ProtoPartResourceSnapshot, double>(prs, prs.amount));
                }
            }
            return snapshot;
        }

        private static void RestoreProtoLiquidFuel(List<KeyValuePair<ProtoPartResourceSnapshot, double>> snapshot)
        {
            if (snapshot == null) return;
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].Key != null)
                    snapshot[i].Key.amount = snapshot[i].Value;
        }
    }
}
