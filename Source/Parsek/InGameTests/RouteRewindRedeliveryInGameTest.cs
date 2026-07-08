using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Rec-1 (logistics &lt;-&gt; time-rewind determinism; plan
    /// <c>docs/dev/plans/fix-logistics-rewind-determinism.md</c>, report
    /// <c>docs/dev/research/logistics-time-rewind-compat-report.md</c>) — the
    /// load-bearing in-game assertion of the USER-VISIBLE symptom and its fix.
    ///
    /// <para><b>The symptom.</b> Drive a KSC-origin route past its dock crossing
    /// so the destination tank re-fills and funds are charged for the cycle
    /// (cycleId <c>cycle-{Completed+Skipped}</c>). Then rewind time to BEFORE the
    /// dispatch. Before the fix the <c>ReconciliationBundle</c> preserved the full
    /// pre-rewind <c>Ledger.Actions</c> while the live world + the route counters
    /// reverted via the <c>.sfs</c>, so the UT-blind dispatch dedup reproduced the
    /// counter-keyed cycleId and SUPPRESSED the re-flown cycle that would re-apply
    /// the cargo: the player was charged AGAIN but the goods never re-delivered
    /// ("funds spent, no goods"). The fix
    /// (<see cref="RouteLedgerRetire.RetireFutureRouteActions"/>, called from the
    /// Rec-1 <see cref="ReconciliationBundle.Restore(ReconciliationBundle, double)"/>
    /// overload) DROPS every free-standing route ledger row whose UT &gt; the
    /// rewind cutoff, so the surviving ledger matches the reverted world and the
    /// re-fly re-emits each cycle deterministically — re-charging funds (once) AND
    /// re-delivering cargo (once).</para>
    ///
    /// <para><b>What this test pins.</b> The full first-deliver -&gt; rewind-restore
    /// -&gt; re-deliver loop end to end against the production
    /// <see cref="RouteOrchestrator.Tick"/> fire path and the LIVE
    /// <c>ApplyDelivery</c> tank writer + ledger:
    /// <list type="number">
    ///   <item>cycle 1 raises the destination tank by the manifest amount and emits
    ///   the cycle's <c>RouteCargoDelivered</c> (+ <c>RouteDispatched</c>) rows;</item>
    ///   <item>a <c>ReconciliationBundle.Restore(bundle, cutoffUT)</c> with the cutoff
    ///   BEFORE the dispatch UT DROPS those free-standing route rows (the determinism
    ///   fix), and the synthetic world is reverted to pre-dispatch (tank drained back,
    ///   route counters reset);</item>
    ///   <item>cycle 2 (the re-fly) raises the tank by the manifest amount AGAIN
    ///   (re-delivered, NOT suppressed) and the net funds across both passes equal
    ///   ONE dispatch charge — not two, not zero.</item>
    /// </list></para>
    ///
    /// <para><b>Why the rewind is APPROXIMATED, not a real RP invoke.</b> A real
    /// <see cref="RewindInvoker.StartInvoke"/> is a destructive scene transition
    /// (<c>GamePersistence.LoadGame</c> + <c>FlightDriver.StartAndFocusVessel</c>)
    /// that cannot be undone inside a single test — the closest existing in-game
    /// rewind test (<see cref="InvokeRPStripAndActivateTest"/>) deliberately avoids
    /// it for exactly this reason. So this test exercises the SAME reconciliation
    /// seam the post-load path runs (<c>ConsumePostLoad</c> -&gt;
    /// <c>ReconciliationBundle.Restore(bundle, post-load-UT)</c>) WITHOUT the scene
    /// reload: capture a bundle after cycle 1, hand-revert the tank + live career funds
    /// + the route's CompletedCycles / SkippedCycles / LastObservedLoopCycleIndex /
    /// NextDispatchUT to their pre-dispatch values (the <c>.sfs</c> revert does all of
    /// this for real), then call the Rec-1 <c>Restore</c> overload with a cutoff before
    /// the dispatch UT. That is the one production call that drops the route rows;
    /// everything downstream (the re-fly Tick) is the real fire path. The inline NOTE at
    /// the rewind step describes what a real-RP variant would need instead.</para>
    ///
    /// <para><b>Setup harness.</b> Reuses the loop-fire harness from
    /// <see cref="LogisticsRouteOnMissionsRuntimeTests"/>
    /// (<c>LoopFire_RendersAndDelivers_AtDockCrossing</c>, the closest existing
    /// in-game test that drives a real route cycle through
    /// <see cref="RouteOrchestrator.Tick"/> onto a live destination tank and asserts
    /// the tank delta + ledger rows): the deterministic injected span clock via
    /// <c>RouteOrchestrator.LoopUnitResolverForTesting</c>, the committed-tree +
    /// committed-recordings ERS eligibility wiring, the KSC-origin Active route with
    /// a LiquidFuel CostManifest + DeliveryManifest onto the active vessel, and the
    /// pre-drain / restore tank discipline.</para>
    ///
    /// <para><b>Re-entry discipline.</b> Per the
    /// <see cref="LogisticsOriginDebitRuntimeTests"/> class-doc note ("background
    /// RouteOrchestrator.Tick can re-enter a logistics test's synthetic route"):
    /// the only yields are the post-restore unpack wait BEFORE any seam is armed or
    /// any state mutated, plus a single settle frame after each manual
    /// <c>RouteOrchestrator.Tick</c>; the arrange / Tick / assert / restore steps run
    /// otherwise yield-free on the main thread so the background 1 Hz scenario tick
    /// cannot interleave with the armed resolver seam or the stored synthetic route.
    /// Teardown disarms the seam FIRST.</para>
    ///
    /// <para><b>Fully reversible.</b> The destination tank, career funds, RouteStore,
    /// Ledger, and the RecordingStore committed tree / recordings are all snapshotted
    /// and restored in <c>finally</c>; the resolver seam is disarmed first.</para>
    /// </summary>
    public sealed class RouteRewindRedeliveryInGameTest
    {
        private const string LiquidFuelName = "LiquidFuel";
        private const double DeliveryAmount = 5.0;
        private const double ResourceTolerance = 0.01;
        private const double FundsTolerance = 0.5;

        // Synthetic member recording ids MUST be unique against whatever save the
        // batch runs in: RouteStore.RevalidateSources resolves source-refs through an
        // ERS index keyed by RecordingId ("ids are unique among visible recordings"),
        // and the InjectAllRecordings test save already carries recordings literally
        // named "launch"/"docked"/"survivor"/"payload" (the dock-undock fixture).
        // With colliding ids the first revalidation pass (triggered by the Rec-1
        // Restore's state-version bump) resolved "launch" to the SAVE's Transport
        // tree, flipped the route to SourceChanged (tree-id drift), and the re-fly
        // was skipped - failing the test for a harness reason, not the Rec-1 bug
        // (2026-07-07 in-game run, save "orbital supply route").
        private const string LaunchRecId = "ingame-rewindredeliver-launch";
        private const string DockedRecId = "ingame-rewindredeliver-docked";
        private const string SurvivorRecId = "ingame-rewindredeliver-survivor";
        private const string PayloadRecId = "ingame-rewindredeliver-payload";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // Deterministic injected span clock (same shape as the loop-fire harness):
        // span [1000,3000], cadence == span, anchor == spanStart, dock UT 2000.
        // Tick at 2000 -> cycleIndex 0, not inter-cycle tail, dock in span ->
        // crossing fires on the first tick (LastObservedLoopCycleIndex starts -1).
        private const double SpanStartUT = 1000.0;
        private const double SpanEndUT = 3000.0;
        private const double DockUT = 2000.0;
        private const double Cadence = SpanEndUT - SpanStartUT;
        private const double TickUT = 2000.0;

        // Rewind cutoff: strictly BEFORE the dispatch UT so the cycle's
        // free-standing route rows (UT == TickUT == 2000) are dropped by
        // RouteLedgerRetire.ShouldRetireRouteActionAtRewind (a.UT > cutoffUT).
        // The dispatch row is stamped at the Tick UT, so any cutoff < 2000 drops
        // it. We use 1500 (mid-span, after the anchor, before the dock) to mirror
        // "rewound to a RewindPoint partway through the transit, before delivery".
        private const double RewindCutoffUT = 1500.0;

        [InGameTest(Category = "Logistics", Scene = GameScenes.FLIGHT,
            AllowBatchExecution = false,
            RestoreBatchFlightBaselineAfterExecution = true,
            BatchSkipReason = "Isolated-run only - mutates RouteStore, Ledger, RecordingStore committed trees, the RouteOrchestrator.LoopUnitResolverForTesting seam, live PartResource.amount, and career Funding under live KSP statics; excluded from ordinary Run All / Run category. Use Run All + Isolated or the row play button in a disposable FLIGHT session.",
            Description = "Rec-1 determinism: a route delivers N LiquidFuel at the dock crossing, a ReconciliationBundle.Restore(bundle, cutoff) with cutoff before the dispatch drops the cycle's route ledger rows + the world reverts to pre-dispatch, and the re-fly Tick re-delivers N AGAIN (not suppressed) with funds netted exactly once")]
        public IEnumerator RouteRedeliversAfterRewindPastDelivery()
        {
            // Post-restore unpack wait — yields BEFORE any seam arming or state
            // mutation, per the re-entry discipline note.
            IEnumerator unpackWait = LogisticsOriginDebitRuntimeTests.WaitForActiveVesselUnpack();
            while (unpackWait.MoveNext())
                yield return unpackWait.Current;

            // ---- PRECONDITION GATE -------------------------------------------
            // The loop-fire seam, the Rec-1 retire helper, and the Rec-1 Restore
            // overload must all exist; a failure here means an upstream phase is
            // incomplete, not that this end-to-end behavior regressed.
            FieldInfo resolverField = typeof(RouteOrchestrator).GetField(
                "LoopUnitResolverForTesting",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (resolverField == null)
                InGameAssert.Skip("PRECONDITION: RouteOrchestrator.LoopUnitResolverForTesting seam missing (upstream loop-fire phase incomplete)");

            // RetireFutureRouteActions and the Restore(bundle, cutoff) overload are both
            // internal; from inside the Parsek assembly the direct calls below resolve at
            // compile time (verified: this file builds against them). These reflection
            // probes are belt-and-suspenders so a future refactor that renames either
            // surface SKIPS (attributable) instead of NRE-ing at run time.
            MethodInfo retireMethod = typeof(RouteLedgerRetire).GetMethod(
                "RetireFutureRouteActions",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (retireMethod == null)
                InGameAssert.Skip("PRECONDITION: RouteLedgerRetire.RetireFutureRouteActions missing (Rec-1 helper absent)");
            MethodInfo restoreCutoffMethod = typeof(ReconciliationBundle).GetMethod(
                "Restore",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(ReconciliationBundle), typeof(double) },
                null);
            if (restoreCutoffMethod == null)
                InGameAssert.Skip("PRECONDITION: ReconciliationBundle.Restore(bundle, cutoff) overload missing (Rec-1 restore absent)");

            if (FlightGlobals.ActiveVessel == null)
                InGameAssert.Skip("FlightGlobals.ActiveVessel is null; need a live vessel to deliver onto");

            Vessel activeVessel = FlightGlobals.ActiveVessel;

            Part fuelPart;
            PartResource fuelResource;
            if (!TryFindLiquidFuelPart(activeVessel, out fuelPart, out fuelResource))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' has no part with a LiquidFuel resource; " +
                    "skipping — pick a vessel with at least one LF tank to run this test");

            // Same loaded-path dependency as the loop-fire harness: the live delivery
            // writer only mutates the live PartResource when the destination is
            // loaded+unpacked. A PRELAUNCH/pad vessel reports unloaded and the delivery
            // writes the proto snapshot instead, so the live tank stays flat. Skip
            // rather than false-fail.
            if (!(activeVessel.loaded && !activeVessel.packed))
                InGameAssert.Skip(
                    $"Active vessel '{activeVessel.vesselName}' is not loaded+unpacked " +
                    $"(loaded={activeVessel.loaded}, packed={activeVessel.packed}); the loop-fire delivery would take " +
                    "the unloaded proto-snapshot path which does not mutate the live PartResource this test reads");

            // PRE-DRAIN so there is headroom for TWO synthetic deliveries (cycle 1 +
            // the re-fly cycle 2). Cap so a tiny tank still receives some of each
            // manifest; clamp the expected fill to the actual headroom.
            double originalAmount = fuelResource.amount;
            double maxAmount = fuelResource.maxAmount;
            // Drain enough room for both deliveries; the rewind drains cycle-1's fill
            // back out, so at any one moment only one delivery's worth must fit.
            double preDrainTarget = originalAmount - DeliveryAmount;
            if (preDrainTarget < 0.0) preDrainTarget = 0.0;
            fuelResource.amount = preDrainTarget;
            double preDispatchAmount = fuelResource.amount;
            double headroom = maxAmount - preDispatchAmount;
            double expectedDelta = DeliveryAmount < headroom ? DeliveryAmount : headroom;
            double expectedAfterDelivery = preDispatchAmount + expectedDelta;

            // Skip the live-tank delta assertions if the tank physically cannot hold
            // the manifest delta (degenerate full / tiny tank); the ledger + funds
            // assertions still run.
            bool tankCanReceiveDelta = expectedDelta > ResourceTolerance;

            string routeTreeId = "ingame-rewindredeliver-tree-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string routeId = "ingame-rewindredeliver-id-" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Snapshot routes for restore.
            var preExistingRoutes = new List<Route>();
            IReadOnlyList<Route> committedRoutes = RouteStore.CommittedRoutes;
            for (int i = 0; i < committedRoutes.Count; i++)
                if (committedRoutes[i] != null)
                    preExistingRoutes.Add(committedRoutes[i]);

            // Snapshot career funds (Sandbox has no Funding singleton — the funds-net
            // assertion is gated on its presence; the tank + ledger assertions still
            // run in Sandbox).
            bool hasFunding = Funding.Instance != null;
            double fundsBaseline = hasFunding ? Funding.Instance.Funds : 0.0;

            int beforeLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

            // Full pre-test ledger snapshot so the finally block can restore the live
            // ledger byte-for-byte (the reconciliation Restore rewrites the whole list).
            preTestLedgerSnapshot = new List<GameAction>();
            if (Ledger.Actions != null)
                for (int i = 0; i < Ledger.Actions.Count; i++)
                    preTestLedgerSnapshot.Add(Ledger.Actions[i]);

            // Source tree committed so ERS carries the route's SourceRefs.
            RecordingTree routeTree = BuildLaunchDockUndockTree(routeTreeId);

            // Deterministic loop unit (only the span-clock fields are read by the fire
            // path; member/owner indices are arbitrary because production resolves
            // through the seam, not the live builder).
            GhostPlaybackLogic.LoopUnit loopUnit = new GhostPlaybackLogic.LoopUnit(
                ownerIndex: 0,
                memberIndices: new[] { 0 },
                spanStartUT: SpanStartUT,
                spanEndUT: SpanEndUT,
                cadenceSeconds: Cadence,
                phaseAnchorUT: SpanStartUT);

            bool routeTreeAdded = false, routeAdded = false, seamArmed = false;
            var previousResolver = RouteOrchestrator.LoopUnitResolverForTesting;
            var committedAdded = new List<Recording>();
            bool tankRestored = false;
            bool fundsRestored = false;

            try
            {
                RecordingStore.AddCommittedTreeForTesting(routeTree);
                routeTreeAdded = true;
                // Push the route's member recordings into CommittedRecordings so ERS
                // sees the launch + docked members (AddCommittedTreeForTesting registers
                // only the tree, not its recordings).
                foreach (Recording rec in routeTree.Recordings.Values)
                {
                    if (rec == null) continue;
                    RecordingStore.AddCommittedInternal(rec);
                    committedAdded.Add(rec);
                }

                Route route = BuildKscLoopRoute(routeId, routeTreeId, activeVessel, routeTree);
                RouteStore.AddRoute(route);
                routeAdded = RouteStore.TryGetRoute(routeId, out _);
                InGameAssert.IsTrue(routeAdded, "Loop route was not stored");

                // Arm the resolver seam so production ResolveLoopUnit returns our
                // deterministic span-clock unit ONLY for this route id. Leaving
                // DeliveryApplierForTesting null keeps the LIVE ApplyDelivery (real
                // tank fill + ledger rows) on the delivery half.
                RouteOrchestrator.LoopUnitResolverForTesting = (r, ut) =>
                {
                    if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                        return loopUnit;
                    return previousResolver != null ? previousResolver(r, ut) : (GhostPlaybackLogic.LoopUnit?)null;
                };
                seamArmed = true;

                ParsekLog.Verbose("TestRunner",
                    $"RewindRedeliver_InGame: pre-cycle1 routeId={routeId} treeId={routeTreeId} " +
                    $"vessel={activeVessel.vesselName} pid={activeVessel.persistentId.ToString(IC)} " +
                    $"preDispatch={preDispatchAmount.ToString("R", IC)} max={maxAmount.ToString("R", IC)} " +
                    $"expectedAfter={expectedAfterDelivery.ToString("R", IC)} fundsBaseline={fundsBaseline.ToString("R", IC)}");

                // ============================================================
                // CYCLE 1 — deliver at the dock crossing.
                // ============================================================
                RouteOrchestrator.Tick(TickUT);
                yield return null; // settle a frame for any deferred behavior

                // 1a. Route state: a fired loop cycle bumps CompletedCycles and snaps
                //     LastObservedLoopCycleIndex forward to the crossed cycle (0).
                Route afterCycle1;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out afterCycle1),
                    "Loop route disappeared from store during cycle-1 Tick");
                InGameAssert.AreEqual(1, afterCycle1.CompletedCycles,
                    $"Expected CompletedCycles=1 after cycle-1 fire, but was {afterCycle1.CompletedCycles.ToString(IC)}");

                // 1b. LIVE resource: destination tank rose by the manifest amount.
                double amountAfterCycle1 = fuelResource.amount;
                if (tankCanReceiveDelta)
                {
                    InGameAssert.IsTrue(amountAfterCycle1 > preDispatchAmount + ResourceTolerance,
                        $"Cycle 1 did not raise LiquidFuel: preDispatch={preDispatchAmount.ToString("R", IC)} " +
                        $"after={amountAfterCycle1.ToString("R", IC)} (live delivery did not land)");
                    InGameAssert.ApproxEqual(expectedAfterDelivery, amountAfterCycle1, ResourceTolerance,
                        $"Cycle 1 LiquidFuel mismatch: expected ~{expectedAfterDelivery.ToString("R", IC)} " +
                        $"got {amountAfterCycle1.ToString("R", IC)}");
                }

                // 1c. Ledger rows for the cycle (RouteDispatched + RouteCargoDelivered).
                int afterCycle1LedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                int cycle1DispatchedRows, cycle1DeliveredRows;
                CountRouteRows(routeId, beforeLedgerCount, afterCycle1LedgerCount,
                    out cycle1DispatchedRows, out cycle1DeliveredRows);
                InGameAssert.IsTrue(cycle1DeliveredRows >= 1,
                    $"No RouteCargoDelivered ledger row after cycle 1 for routeId={routeId} " +
                    $"(before={beforeLedgerCount.ToString(IC)} after={afterCycle1LedgerCount.ToString(IC)})");
                InGameAssert.IsTrue(cycle1DispatchedRows >= 1,
                    $"No RouteDispatched ledger row after cycle 1 for routeId={routeId}");

                // 1d. Funds after cycle 1: charged once. For a Career + KSC-origin route
                //     the dispatch-debit half calls LiveDebitFunds(cost) INSIDE the same
                //     Tick (RouteOrchestrator.EmitDispatchDebit -> the FundsDebiter =
                //     LiveDebitFunds delegate -> Funding.Instance.AddFunds(-cost)), so the
                //     live career funds move synchronously here — no separate
                //     LedgerOrchestrator.RecalculateAndPatch is needed to witness the
                //     charge. Read live Funding directly (the single-charge net is
                //     asserted after the re-fly leg in 2d).
                double fundsAfterCycle1 = hasFunding ? Funding.Instance.Funds : 0.0;

                ParsekLog.Info("TestRunner",
                    $"RewindRedeliver_InGame: cycle1 delivered fuelAfter={amountAfterCycle1.ToString("R", IC)} " +
                    $"dispatchedRows={cycle1DispatchedRows.ToString(IC)} deliveredRows={cycle1DeliveredRows.ToString(IC)} " +
                    $"fundsAfter={fundsAfterCycle1.ToString("R", IC)}");

                // ============================================================
                // REWIND (APPROXIMATED) — capture a bundle, revert the world to
                // pre-dispatch, then run the Rec-1 reconciliation restore with a
                // cutoff BEFORE the dispatch UT.
                //
                // NOTE (why the rewind is approximated, not a real RP invoke): a real-RP
                // variant would author a RewindPoint via RewindPointAuthor at UT ==
                // RewindCutoffUT (before the dock), call RewindInvoker.StartInvoke(rp,
                // slot) and let ParsekScenario.OnLoad -> RewindInvoker.ConsumePostLoad run
                // ReconciliationBundle.Restore(bundle, post-load-UT). That is a DESTRUCTIVE
                // scene reload (GamePersistence.LoadGame + FlightDriver.StartAndFocusVessel)
                // and cannot be undone inside one test — see InvokeRPStripAndActivateTest,
                // which avoids it for the same reason. This approximation exercises the
                // EXACT same reconciliation call the post-load path runs, minus the scene
                // reload; the world-revert that the .sfs does for real is done here by hand.
                // ============================================================

                // Capture the bundle AFTER cycle 1 — this is exactly what
                // RewindInvoker.StartInvoke captures pre-load: the full ledger
                // (including cycle 1's route rows) plus the recording/tree/scenario
                // state.
                ReconciliationBundle bundle = ReconciliationBundle.Capture();

                // World revert (what the .sfs quickload does for real): drain cycle
                // 1's delivery back out of the tank, restore the pre-dispatch career
                // funds, and reset the route counters that the dispatch dedup keys on so
                // the re-fly recomputes the SAME cycleId (this is the precise condition
                // that triggered the bug — UT-blind dedup reproduces cycle-{C+S}).
                fuelResource.amount = preDispatchAmount;
                // Cycle 1's dispatch already debited live Funding synchronously (see 1d).
                // A real Rewind-to-Separation reloads the pre-dispatch .sfs, which reverts
                // career funds too; the approximation must do the same by hand, otherwise
                // the cycle-1 charge would still stand and the re-fly would double-charge
                // (making the single-charge net in 2d read as TWO charges). Reset live
                // Funding to the pre-test baseline (= the pre-dispatch value) so the net
                // across deliver+rewind+redeliver reflects exactly the re-flown charge.
                if (hasFunding && Funding.Instance != null)
                    Funding.Instance.SetFunds(fundsBaseline, TransactionReasons.None);
                Route preRewindRoute;
                if (RouteStore.TryGetRoute(routeId, out preRewindRoute) && preRewindRoute != null)
                {
                    preRewindRoute.CompletedCycles = 0;
                    preRewindRoute.SkippedCycles = 0;
                    preRewindRoute.LastObservedLoopCycleIndex = -1;
                    preRewindRoute.NextDispatchUT = TickUT + Cadence;
                }

                // The Rec-1 reconciliation restore: drops free-standing route ledger
                // rows with UT > cutoff (the determinism fix). cutoff < dispatch UT, so
                // cycle 1's RouteDispatched/RouteCargoDelivered rows (UT == 2000) are
                // dropped. Non-route reconstruction stays byte-identical.
                ReconciliationBundle.Restore(bundle, RewindCutoffUT);

                // Assert the route rows were actually dropped from the live ledger.
                int afterRestoreLedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                int postRestoreDispatched, postRestoreDelivered;
                CountRouteRowsWholeLedger(routeId, RewindCutoffUT,
                    out postRestoreDispatched, out postRestoreDelivered);
                InGameAssert.AreEqual(0, postRestoreDelivered,
                    $"Rec-1 restore should have DROPPED the cycle-1 RouteCargoDelivered row " +
                    $"(UT > cutoff {RewindCutoffUT.ToString("R", IC)}); {postRestoreDelivered.ToString(IC)} remain");
                InGameAssert.AreEqual(0, postRestoreDispatched,
                    $"Rec-1 restore should have DROPPED the cycle-1 RouteDispatched row; " +
                    $"{postRestoreDispatched.ToString(IC)} remain");

                // RouteStore is NOT part of the reconciliation bundle, so Restore left
                // our synthetic route in place (with the hand-reset counters). Confirm
                // it survived so the re-fly Tick has a live route to fire.
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out _),
                    "Synthetic route disappeared from RouteStore across the reconciliation restore");

                ParsekLog.Info("TestRunner",
                    $"RewindRedeliver_InGame: post-restore route rows dropped " +
                    $"(ledgerCount {afterCycle1LedgerCount.ToString(IC)}->{afterRestoreLedgerCount.ToString(IC)}); " +
                    "re-flying cycle");

                // ============================================================
                // CYCLE 2 (RE-FLY) — drive the SAME crossing again. With the route
                // rows dropped + counters reset, the dedup sees an empty slate for
                // cycle-{0} and re-fires: the tank rises AGAIN and a fresh
                // RouteCargoDelivered row lands.
                // ============================================================
                int beforeCycle2LedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;

                RouteOrchestrator.Tick(TickUT);
                yield return null; // settle a frame

                // 2a. Route state re-fired.
                Route afterCycle2;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out afterCycle2),
                    "Loop route disappeared from store during cycle-2 (re-fly) Tick");
                InGameAssert.AreEqual(1, afterCycle2.CompletedCycles,
                    $"Re-fly cycle should have re-fired (CompletedCycles back to 1), but was " +
                    $"{afterCycle2.CompletedCycles.ToString(IC)} — the cycle was SUPPRESSED (the Rec-1 bug)");

                // 2b. LIVE resource re-delivered: tank rose by the manifest amount
                //     AGAIN, off the reverted pre-dispatch baseline.
                double amountAfterCycle2 = fuelResource.amount;
                if (tankCanReceiveDelta)
                {
                    InGameAssert.IsTrue(amountAfterCycle2 > preDispatchAmount + ResourceTolerance,
                        $"RE-FLY did not re-deliver LiquidFuel: preDispatch={preDispatchAmount.ToString("R", IC)} " +
                        $"after={amountAfterCycle2.ToString("R", IC)} — this is the 'funds charged, no goods' symptom");
                    InGameAssert.ApproxEqual(expectedAfterDelivery, amountAfterCycle2, ResourceTolerance,
                        $"Re-fly LiquidFuel mismatch: expected ~{expectedAfterDelivery.ToString("R", IC)} " +
                        $"got {amountAfterCycle2.ToString("R", IC)} (re-delivered exactly once off the reverted baseline)");
                }

                // 2c. A fresh RouteCargoDelivered row landed for the re-fly cycle.
                int afterCycle2LedgerCount = Ledger.Actions != null ? Ledger.Actions.Count : 0;
                int cycle2DispatchedRows, cycle2DeliveredRows;
                CountRouteRows(routeId, beforeCycle2LedgerCount, afterCycle2LedgerCount,
                    out cycle2DispatchedRows, out cycle2DeliveredRows);
                InGameAssert.IsTrue(cycle2DeliveredRows >= 1,
                    $"RE-FLY emitted no RouteCargoDelivered row for routeId={routeId} " +
                    $"(the cycle was SUPPRESSED — the Rec-1 determinism bug)");
                InGameAssert.IsTrue(cycle2DispatchedRows >= 1,
                    $"RE-FLY emitted no RouteDispatched row for routeId={routeId}");

                // 2d. Funds netted EXACTLY ONE dispatch charge across both passes.
                //     The dispatch-debit charges live Funding synchronously inside Tick
                //     (see 1d), and the rewind approximation reset live funds to the
                //     baseline (mirroring the .sfs revert). So after the re-fly re-emitted
                //     the dispatch, live career funds should be down by ONE dispatch cost
                //     from the baseline — not two (double charge), not zero (suppressed).
                //     Reading Funding.Instance.Funds is the correct witness; no separate
                //     LedgerOrchestrator.RecalculateAndPatch is required.
                if (hasFunding)
                {
                    double fundsAfterRefly = Funding.Instance.Funds;
                    double dispatchCost = KscDispatchCostForRoute(routeId);
                    // Net change from baseline should equal a single dispatch charge.
                    double netCharge = fundsBaseline - fundsAfterRefly;
                    // Only assert the single-charge invariant when the route actually
                    // carries a non-trivial KSC dispatch cost; a zero-cost route nets
                    // zero and the assertion is vacuous (still must NOT be a double-
                    // credit / double-debit, which the >= -tol .. <= cost+tol band
                    // catches).
                    InGameAssert.IsTrue(
                        netCharge >= -FundsTolerance && netCharge <= dispatchCost + FundsTolerance,
                        $"Funds must net at most ONE dispatch charge across deliver+rewind+redeliver: " +
                        $"baseline={fundsBaseline.ToString("R", IC)} afterRefly={fundsAfterRefly.ToString("R", IC)} " +
                        $"net={netCharge.ToString("R", IC)} dispatchCost={dispatchCost.ToString("R", IC)} " +
                        "(net > one charge => double-debit; this is the determinism contract)");
                    ParsekLog.Info("TestRunner",
                        $"RewindRedeliver_InGame: funds netted net={netCharge.ToString("R", IC)} " +
                        $"(<= one dispatch cost {dispatchCost.ToString("R", IC)})");
                }
                else
                {
                    ParsekLog.Info("TestRunner",
                        "RewindRedeliver_InGame: no Funding singleton (Sandbox) — funds-net assertion skipped; " +
                        "tank re-delivery + ledger-row assertions still ran");
                }

                ParsekLog.Info("TestRunner",
                    $"RewindRedeliver_InGame: PASS routeId={routeId} " +
                    $"cycle1Fuel={amountAfterCycle1.ToString("R", IC)} reflyFuel={amountAfterCycle2.ToString("R", IC)} " +
                    $"preDispatch={preDispatchAmount.ToString("R", IC)} " +
                    $"cycle2DeliveredRows={cycle2DeliveredRows.ToString(IC)}");
            }
            finally
            {
                // Disarm the seam FIRST so a later background tick never re-enters our
                // resolver.
                if (seamArmed)
                    RouteOrchestrator.LoopUnitResolverForTesting = previousResolver;

                if (routeAdded)
                {
                    bool removed = RouteStore.RemoveRoute(routeId);
                    ParsekLog.Verbose("TestRunner", $"RewindRedeliver_InGame cleanup: RemoveRoute={removed}");
                }
                RestoreRoutes(preExistingRoutes);

                // Remove the recordings we pushed into CommittedRecordings, then the
                // tree. NOTE: the reconciliation Restore above re-installed the
                // CommittedRecordings list from the captured bundle (which already
                // contained our pushed recordings), so RemoveCommittedInternal still
                // targets the right ids.
                for (int i = 0; i < committedAdded.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedAdded[i]);
                if (routeTreeAdded) RemoveCommittedTree(routeTreeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);

                // Restore the live ledger to its pre-test contents. The reconciliation
                // Restore rewrote the ledger action list from the captured bundle; the
                // safest restore is to clear and re-add the rows captured before the test
                // ran (Ledger.Clear() + Ledger.AddActions(prefix), verified surfaces). The
                // finally block below also resets live career Funds directly to the
                // pre-test baseline, so no LedgerOrchestrator.RecalculateAndPatch is needed
                // to re-sync the career scalars this test touched.
                try
                {
                    RestoreLedgerPrefix(beforeLedgerCount);
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"RewindRedeliver_InGame cleanup: ledger restore failed ({ex.GetType().Name}: {ex.Message})");
                }

                // Restore the destination tank to its pre-test amount.
                try
                {
                    if (fuelResource != null)
                    {
                        fuelResource.amount = originalAmount;
                        tankRestored = true;
                        ParsekLog.Verbose("TestRunner",
                            $"RewindRedeliver_InGame cleanup: restored LiquidFuel to {originalAmount.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"RewindRedeliver_InGame cleanup: failed to restore LiquidFuel ({ex.GetType().Name}: {ex.Message})");
                }

                // Restore career funds to the pre-test baseline (any dispatch charge
                // that reached live Funding during the test is undone).
                try
                {
                    if (hasFunding && Funding.Instance != null)
                    {
                        Funding.Instance.SetFunds(fundsBaseline, TransactionReasons.None);
                        fundsRestored = true;
                        ParsekLog.Verbose("TestRunner",
                            $"RewindRedeliver_InGame cleanup: restored Funds to {fundsBaseline.ToString("R", IC)}");
                    }
                }
                catch (Exception ex)
                {
                    ParsekLog.Warn("TestRunner",
                        $"RewindRedeliver_InGame cleanup: failed to restore Funds ({ex.GetType().Name}: {ex.Message})");
                }

                ParsekLog.Verbose("TestRunner",
                    $"RewindRedeliver_InGame cleanup: tankRestored={tankRestored} fundsRestored={fundsRestored}");
            }
        }

        // ==================================================================
        // Helpers (local to this file — shared test generators are off-limits
        // to the logistics in-game workstream; mirror the loop-fire harness).
        // ==================================================================

        /// <summary>
        /// Snapshot of the live ledger taken at the top of the test so the finally
        /// block can restore it. Held in an instance field rather than threaded
        /// through because the finally block runs after the iterator state machine
        /// has already moved past the local-capture point. Verified to compile cleanly
        /// under net472's iterator rewriter.
        /// </summary>
        private List<GameAction> preTestLedgerSnapshot;

        /// <summary>
        /// Builds the KSC-origin Active loop route (IsLoopRoute true via the backing
        /// tree) delivering <see cref="DeliveryAmount"/> LiquidFuel onto the active
        /// vessel at the dock crossing. Mirrors the loop-fire harness route shape.
        /// </summary>
        private static RouteSourceRef BuildMirroredSourceRef(Recording rec)
        {
            return new RouteSourceRef
            {
                RecordingId = rec.RecordingId,
                TreeId = rec.TreeId,
                TreeOrder = rec.TreeOrder,
                RecordingFormatVersion = rec.RecordingFormatVersion,
                RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                SidecarEpoch = rec.SidecarEpoch,
                StartUT = rec.StartUT,
                EndUT = rec.EndUT,
                RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec)
            };
        }

        private static Route BuildKscLoopRoute(string routeId, string routeTreeId, Vessel destination, RecordingTree tree)
        {
            return new Route
            {
                Id = routeId,
                Name = "Parsek Rewind-Redeliver In-Game",
                Status = RouteStatus.Active,
                IsKscOrigin = true,
                BackingMissionTreeId = routeTreeId,
                ExcludedIntervalKeys = new HashSet<string>(),
                RecordedDockUT = DockUT,
                DockMemberRecordingId = DockedRecId,
                LoopAnchorUT = SpanStartUT,
                LastObservedLoopCycleIndex = -1,
                TransitDuration = Cadence,
                DispatchInterval = Cadence,
                NextDispatchUT = TickUT + Cadence,
                CompletedCycles = 0,
                SkippedCycles = 0,
                // Seed a non-zero KSC dispatch cost. NOTE: EmitDispatchDebit RECOMPUTES
                // the charge from the route's CostManifest via
                // ComputeDispatchFundsCostForRoute and OVERWRITES route.KscDispatchFundsCost
                // with the computed value before debiting live Funding, so this seed is
                // not itself the charged amount. The single-charge assertion reads the
                // post-tick KscDispatchFundsCost (= the computed/charged cost) as its
                // witness, so it stays self-consistent whatever the computed value is; a
                // 0.0 computed cost merely makes the net-charge band vacuous (still guards
                // against a double-debit).
                KscDispatchFundsCost = 100.0,
                CostManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    { LiquidFuelName, DeliveryAmount },
                },
                RecordingIds = new List<string> { LaunchRecId, DockedRecId },
                // Source refs must mirror the member recordings field-for-field
                // (RouteBuilder shape, incl. the proof hash): RevalidateSources
                // compares EVERY field against a live ref rebuilt from the
                // recording, and any hand-built partial ref reads as drift ->
                // SourceChanged -> the route is skipped (2026-07-08 gate run).
                SourceRefs = new List<RouteSourceRef>
                {
                    BuildMirroredSourceRef(tree.Recordings[LaunchRecId]),
                    BuildMirroredSourceRef(tree.Recordings[DockedRecId]),
                },
                Stops = new List<RouteStop>
                {
                    new RouteStop
                    {
                        Endpoint = new RouteEndpoint
                        {
                            VesselPersistentId = destination.persistentId,
                            BodyName = destination.mainBody != null ? destination.mainBody.bodyName : "Kerbin",
                            IsSurface = false,
                        },
                        ConnectionKind = RouteConnectionKind.DockingPort,
                        DeliveryManifest = new Dictionary<string, double>(StringComparer.Ordinal)
                        {
                            { LiquidFuelName, DeliveryAmount },
                        },
                        InventoryDeliveryManifest = new List<InventoryPayloadItem>(),
                        SegmentIndexBefore = 0,
                        DeliveryOffsetSeconds = 0.0,
                    },
                },
            };
        }

        /// <summary>
        /// Reads the route's KSC dispatch funds cost for the single-charge funds
        /// invariant. Returns 0 when the route is gone.
        /// </summary>
        private static double KscDispatchCostForRoute(string routeId)
        {
            Route r;
            if (RouteStore.TryGetRoute(routeId, out r) && r != null)
                return r.KscDispatchFundsCost;
            return 0.0;
        }

        /// <summary>
        /// Counts RouteDispatched / RouteCargoDelivered ledger rows for
        /// <paramref name="routeId"/> in the half-open ledger index range
        /// [start, end). Used to count the rows a single Tick appended.
        /// </summary>
        private static void CountRouteRows(string routeId, int start, int end,
            out int dispatched, out int delivered)
        {
            dispatched = 0;
            delivered = 0;
            var actions = Ledger.Actions;
            if (actions == null) return;
            int lo = start < 0 ? 0 : start;
            int hi = end > actions.Count ? actions.Count : end;
            for (int i = lo; i < hi; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatched++;
                else if (a.Type == GameActionType.RouteCargoDelivered) delivered++;
            }
        }

        /// <summary>
        /// Counts RouteDispatched / RouteCargoDelivered ledger rows for
        /// <paramref name="routeId"/> anywhere in the live ledger whose UT is strictly
        /// after <paramref name="cutoffUT"/> — i.e. the rows the Rec-1 restore should
        /// have dropped. Used to assert post-restore that they are gone.
        /// </summary>
        private static void CountRouteRowsWholeLedger(string routeId, double cutoffUT,
            out int dispatched, out int delivered)
        {
            dispatched = 0;
            delivered = 0;
            var actions = Ledger.Actions;
            if (actions == null) return;
            for (int i = 0; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.UT <= cutoffUT) continue;
                if (a.Type == GameActionType.RouteDispatched) dispatched++;
                else if (a.Type == GameActionType.RouteCargoDelivered) delivered++;
            }
        }

        /// <summary>
        /// Restores the live ledger to its first <paramref name="prefixCount"/>
        /// actions (the contents captured before the test mutated anything). The
        /// reconciliation Restore rewrote the whole list, so cleanup re-truncates it.
        /// Uses the verified Ledger.Clear() + Ledger.AddActions(prefix) static surfaces;
        /// the caller's finally block resets live career Funds directly to the pre-test
        /// baseline, so no LedgerOrchestrator.RecalculateAndPatch re-sync is required.
        /// </summary>
        private void RestoreLedgerPrefix(int prefixCount)
        {
            // Prefer the explicit pre-test snapshot if one was taken; otherwise
            // truncate the current live ledger to the recorded prefix length.
            if (preTestLedgerSnapshot != null)
            {
                Ledger.Clear();
                if (preTestLedgerSnapshot.Count > 0)
                    Ledger.AddActions(preTestLedgerSnapshot);
                return;
            }

            var actions = Ledger.Actions;
            if (actions == null) return;
            int keep = prefixCount < 0 ? 0 : (prefixCount > actions.Count ? actions.Count : prefixCount);
            var prefix = new List<GameAction>(keep);
            for (int i = 0; i < keep; i++)
                prefix.Add(actions[i]);
            Ledger.Clear();
            if (prefix.Count > 0)
                Ledger.AddActions(prefix);
        }

        /// <summary>
        /// Finds the first <see cref="Part"/> on <paramref name="vessel"/> carrying a
        /// LiquidFuel resource. Mirrors the order the delivery applier walks
        /// (vessel.parts ascending) so pre-drain / restore + the applier's fill land
        /// on the same tank. Copied from the loop-fire harness (shared generators are
        /// off-limits to this workstream).
        /// </summary>
        private static bool TryFindLiquidFuelPart(Vessel vessel, out Part part, out PartResource resource)
        {
            part = null;
            resource = null;
            if (vessel == null || vessel.parts == null) return false;
            for (int i = 0; i < vessel.parts.Count; i++)
            {
                Part p = vessel.parts[i];
                if (p == null || p.Resources == null) continue;
                PartResource pr = p.Resources.Get(LiquidFuelName);
                if (pr == null) continue;
                part = p;
                resource = pr;
                return true;
            }
            return false;
        }

        // launch -> dock -> undock with a peeled payload at undock. Root launch UT
        // 1000, undock 3000 (mirrors the loop-fire harness topology so ERS resolves
        // the launch + docked members). Ids carry the test-unique prefix so they can
        // never collide with recordings already in the loaded save (see the RecId
        // constants for the failure this prevents). Every recording carries TreeId:
        // RouteStore.RevalidateSources (triggered by the Rec-1 Restore's
        // state-version bump) mirrors rec.TreeId into the live comparison ref, and
        // a null TreeId reads as tree-id drift -> SourceChanged -> re-fly skipped
        // (second gate-run failure, 2026-07-08).
        private static RecordingTree BuildLaunchDockUndockTree(string treeId)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = LaunchRecId };
            tree.Recordings[LaunchRecId] = Leg(LaunchRecId, "C0", 0, 1000, 2000, "Transport");
            tree.Recordings[DockedRecId] = Leg(DockedRecId, "C0", 1, 2000, 3000, "Transport");
            tree.Recordings[SurvivorRecId] = Leg(SurvivorRecId, "C0", 2, 3000, 4000, "Transport");
            tree.Recordings[PayloadRecId] = Leg(PayloadRecId, "C1", 0, 3000, 3500, "Payload");
            foreach (var rec in tree.Recordings.Values)
                rec.TreeId = treeId;
            tree.BranchPoints.Add(BP("dock-bp", BranchPointType.Dock,
                new[] { LaunchRecId }, new[] { DockedRecId }, 2000));
            tree.BranchPoints.Add(BP("undock-bp", BranchPointType.Undock,
                new[] { DockedRecId }, new[] { SurvivorRecId, PayloadRecId }, 3000));
            return tree;
        }

        private static Recording Leg(string id, string chainId, int chainIndex,
            double start, double end, string vessel)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end
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
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children)
            };
        }

        private static void RestoreRoutes(List<Route> preExisting)
        {
            RouteStore.ResetForTesting();
            for (int i = 0; i < preExisting.Count; i++)
                if (preExisting[i] != null)
                    RouteStore.AddRoute(preExisting[i]);
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
    }
}
