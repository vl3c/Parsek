using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Logistics;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// In-game coverage for the route-rewind timeline wave (PRs #1330 status
    /// fidelity, #1331 pending science, #1332 go-back reconcile, #1333 dormant
    /// UI + auto-pause rows), replacing the manual playtest runbook scenarios
    /// that only exercised state and logs an operator had to eyeball.
    ///
    /// <para><b>What the headless xUnit layer already proves</b> (see
    /// <c>RouteRewindStatusFidelityTests</c>, <c>RouteRewindDormantTests</c>,
    /// <c>RouteGoBackRewindReconcileTests</c>, <c>RouteDormantUiTests</c>,
    /// <c>ReconciliationBundleTests</c>): the pure classify / derive / apply /
    /// reconstruct math over hand-built inputs. <b>What THIS category adds:</b>
    /// the same seams driven against the LIVE KSP statics the production code
    /// actually runs on — a real <c>Planetarium.GetUniversalTime()</c> for the
    /// lifecycle-marker rows, the real <c>Ledger</c> / <c>RouteStore</c> /
    /// <c>RecordingStore</c> singletons behind
    /// <c>ReconciliationBundle.Capture/Restore(cutoff)</c>, the real ERS/ELS
    /// computation under <c>RevalidateSources</c>, the real
    /// <c>RouteOrchestrator.Tick</c> materialize drain, and (one test) the
    /// production <c>ParsekScenario.Update</c> 1 Hz tick wiring itself.</para>
    ///
    /// <para><b>Batch-safety contract.</b> Every test here is
    /// <c>AllowBatchExecution = true</c> and scene-agnostic
    /// (<c>InGameTestAttribute.AnyScene</c>) so the whole category runs
    /// unattended via the command seam's <c>RunTests</c> verb from whatever
    /// scene the host save loads into. To honor that: no scene loads, no save
    /// writes, no live-vessel or career-pool mutation; only in-memory statics
    /// (RouteStore, Ledger, RecordingStore committed lists,
    /// GameStateRecorder.PendingScienceSubjects) are touched and ALL of them
    /// are snapshotted up front and restored in <c>finally</c>.</para>
    ///
    /// <para><b>Store isolation.</b> Tests that run a seam which iterates or
    /// wholesale-replaces the committed route lists
    /// (<c>ReconcileStoreAtRewind</c> / live <c>RevalidateSources</c> /
    /// <c>MaterializeDueDormantRoutes</c>) first swap the store down to ONLY
    /// their synthetic routes via <c>InstallRoutesAtRewind</c> and restore the
    /// pre-test lists afterwards, so a pre-existing player route can never be
    /// cursor-reset / status-flipped / marker-stamped by a test-owned pass
    /// (the seams mutate Route INSTANCES, which no list restore could undo).</para>
    ///
    /// <para><b>Synthetic-UT discipline.</b> Seam tests anchor their synthetic
    /// cutoff at <c>liveUT + 1000000</c> so every hand-built ledger row and
    /// route stamp lives in a UT band disjoint from anything the live session
    /// wrote — the Rec-1 retire then provably touches only test rows. The
    /// lifecycle-marker and scenario-tick tests deliberately use the REAL live
    /// UT instead: that is the production contract under test.</para>
    ///
    /// <para><b>Re-entry discipline</b> (per the logistics in-game class-doc
    /// convention): the seam tests are synchronous (<c>void</c>) so the
    /// background 1 Hz scenario tick can never interleave with a half-arranged
    /// store; the two coroutine tests yield only while waiting on the
    /// production tick, with their stores isolated first.</para>
    /// </summary>
    public sealed class RouteRewindTimelineRuntimeTests
    {
        private const string Category = "RouteRewindTimeline";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        /// <summary>Synthetic-UT offset for the seam tests (see class doc).</summary>
        private const double SyntheticCutoffOffset = 1000000.0;

        // ==================================================================
        // (a) Live pause / resume / send-once lifecycle ledger rows
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "TryPause / TryActivate / TrySendOneCycleNow against the live Planetarium UT: RoutePaused/RouteResumed ledger rows land at the real clock with player reasons, Send Once arms both one-shot flags without a row, and a pause supersedes the arm")]
        public void LifecycleRows_PauseActivateSendOnce_EmitAtLivePlanetariumUT()
        {
            double liveUT = RequireLiveContext(requireScenario: false);

            string routeId = NewId("lifecycle");
            Route route = BuildMinimalRoute(routeId, createdUT: liveUT, RouteStatus.Active);

            List<GameAction> preLedger = SnapshotLedger();
            int baseCount = preLedger.Count;
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;
            bool added = false;

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                RouteStore.AddRoute(route);
                added = true;

                // --- Pause: immediate (not InTransit) -> Paused + a RoutePaused row
                // stamped at the REAL Planetarium UT via the production no-arg entry.
                double utBeforePause = Planetarium.GetUniversalTime();
                InGameAssert.IsTrue(RouteOrchestrator.TryPause(route),
                    "TryPause returned false for an Active route");
                InGameAssert.AreEqual(RouteStatus.Paused, route.Status,
                    "TryPause did not transition the route to Paused");
                GameAction pauseRow = LatestLifecycleRow(routeId, baseCount);
                InGameAssert.IsNotNull(pauseRow,
                    "No lifecycle ledger row appended by TryPause");
                InGameAssert.AreEqual(GameActionType.RoutePaused, pauseRow.Type,
                    "TryPause row type mismatch");
                InGameAssert.AreEqual("player-pause", pauseRow.RouteEndpointReason,
                    "TryPause row reason mismatch");
                InGameAssert.IsTrue(
                    pauseRow.UT >= utBeforePause - 0.001 && pauseRow.UT <= utBeforePause + 30.0,
                    $"RoutePaused row UT {pauseRow.UT.ToString("R", IC)} not at the live clock " +
                    $"(utBeforePause={utBeforePause.ToString("R", IC)})");

                // --- Activate: Paused -> Active + a RouteResumed row.
                int beforeActivate = Ledger.Actions.Count;
                InGameAssert.IsTrue(
                    RouteOrchestrator.TryActivate(route, Planetarium.GetUniversalTime()),
                    "TryActivate returned false for a Paused route");
                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    "TryActivate did not transition the route to Active");
                GameAction resumeRow = LatestLifecycleRow(routeId, beforeActivate);
                InGameAssert.IsNotNull(resumeRow, "No lifecycle row appended by TryActivate");
                InGameAssert.AreEqual(GameActionType.RouteResumed, resumeRow.Type,
                    "TryActivate row type mismatch");
                InGameAssert.AreEqual("player-activate", resumeRow.RouteEndpointReason,
                    "TryActivate row reason mismatch");

                // --- Pause again so Send Once can exercise its un-pause branch.
                InGameAssert.IsTrue(RouteOrchestrator.TryPause(route),
                    "Second TryPause returned false");
                InGameAssert.AreEqual(RouteStatus.Paused, route.Status, "Second pause failed");

                // --- Send Once from Paused: arms both one-shot flags, un-pauses to
                // Active, pulls NextDispatchUT forward, and emits NO ledger row (the
                // Send Once provenance is stamped on the dispatched row later).
                int beforeSendOnce = Ledger.Actions.Count;
                route.NextDispatchUT = liveUT + 99999.0;
                double sendOnceUT = Planetarium.GetUniversalTime();
                InGameAssert.IsTrue(RouteOrchestrator.TrySendOneCycleNow(route, sendOnceUT),
                    "TrySendOneCycleNow returned false for a Paused route");
                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    "Send Once did not un-pause the route");
                InGameAssert.IsTrue(route.SendOnceArmed, "SendOnceArmed not set");
                InGameAssert.IsTrue(route.PauseAfterCurrentCycle, "PauseAfterCurrentCycle not set");
                InGameAssert.IsTrue(route.NextDispatchUT <= sendOnceUT + 0.001,
                    $"Send Once did not pull NextDispatchUT forward " +
                    $"(next={route.NextDispatchUT.ToString("R", IC)} now={sendOnceUT.ToString("R", IC)})");
                InGameAssert.AreEqual(beforeSendOnce, Ledger.Actions.Count,
                    "Send Once must not emit a ledger row at arm time");

                // --- Pause while armed (not InTransit): the arm is consumed
                // silently (never-fired one-shot leaves no trace) and the pause
                // row lands.
                int beforeFinalPause = Ledger.Actions.Count;
                InGameAssert.IsTrue(RouteOrchestrator.TryPause(route),
                    "TryPause returned false for the armed route");
                InGameAssert.IsFalse(route.SendOnceArmed,
                    "Pause must clear the pending Send Once arm");
                InGameAssert.IsFalse(route.PauseAfterCurrentCycle,
                    "Pause must clear PauseAfterCurrentCycle");
                InGameAssert.AreEqual(RouteStatus.Paused, route.Status, "Final pause failed");
                GameAction finalPauseRow = LatestLifecycleRow(routeId, beforeFinalPause);
                InGameAssert.IsNotNull(finalPauseRow, "No lifecycle row for the final pause");
                InGameAssert.AreEqual(GameActionType.RoutePaused, finalPauseRow.Type,
                    "Final pause row type mismatch");

                // Grep-stable log contract: the production LifecycleMarker lines fired
                // for both directions.
                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "type=RoutePaused", "reason=player-pause"),
                    "No 'LifecycleMarker: ... type=RoutePaused ... reason=player-pause' log line observed");
                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "type=RouteResumed", "reason=player-activate"),
                    "No 'LifecycleMarker: ... type=RouteResumed ... reason=player-activate' log line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline lifecycle: PASS routeId={routeId} " +
                    $"rows={(Ledger.Actions.Count - baseCount).ToString(IC)} liveUT={liveUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                if (added) RouteStore.RemoveRoute(routeId);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // (b) Bundle Restore(cutoff) -> dormant -> Tick materialize
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "ReconciliationBundle.Capture -> Restore(cutoff) classifies a post-cutoff-created route to the dormant list, a pre-CreatedUT RouteOrchestrator.Tick leaves it dormant, and the first due Tick re-materializes it Paused with fresh cycle state through the real materialize drain")]
        public void BundleRestoreCutoff_PostCutoffRouteGoesDormant_TickMaterializes()
        {
            double liveUT = RequireLiveContext(requireScenario: true);
            double cutoffUT = liveUT + SyntheticCutoffOffset;
            double createdUT = cutoffUT + 5.0;

            string treeId = NewId("dormant-tree");
            string routeId = NewId("dormant");
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            List<GameAction> preLedger = SnapshotLedger();
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);
            var committedRecs = new List<Recording>();
            bool treeAdded = false;

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                // Committed source tree + recordings so the materialize-time
                // RevalidateSources sees healthy sources (the route must come back
                // Paused, not MissingSourceRecording).
                RecordingTree tree = BuildTwoLegTree(treeId, routeId);
                RecordingStore.AddCommittedTreeForTesting(tree);
                treeAdded = true;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    RecordingStore.AddCommittedInternal(rec);
                    committedRecs.Add(rec);
                }

                Route route = BuildRouteWithSources(routeId, createdUT, RouteStatus.Active, tree);
                // Store isolation: only the synthetic route participates in the
                // rewind seam + the Tick drains below.
                RouteStore.InstallRoutesAtRewind(new List<Route> { route }, new List<Route>());

                ReconciliationBundle bundle = ReconciliationBundle.Capture();
                ReconciliationBundle.Restore(bundle, cutoffUT);

                // Classified dormant: gone from committed, present in dormant.
                InGameAssert.IsFalse(RouteStore.TryGetRoute(routeId, out _),
                    "Post-cutoff-created route should have left the committed list at Restore(cutoff)");
                InGameAssert.IsTrue(DormantContains(routeId),
                    "Post-cutoff-created route not found in RouteStore.DormantRoutes after Restore(cutoff)");
                InGameAssert.IsTrue(AnyLine(lines, "InstallRoutesAtRewind:"),
                    "No 'InstallRoutesAtRewind:' log line observed at Restore(cutoff)");

                // Not due before CreatedUT: the real Tick's materialize drain must
                // leave it dormant.
                RouteOrchestrator.Tick(cutoffUT);
                InGameAssert.IsTrue(DormantContains(routeId),
                    "Route materialized BEFORE its CreatedUT (IsDormantRouteDue gate broken)");
                InGameAssert.IsFalse(RouteStore.TryGetRoute(routeId, out _),
                    "Route appeared in the committed list before its CreatedUT");

                // Due: the first Tick at/after CreatedUT materializes it fresh.
                RouteOrchestrator.Tick(createdUT + 1.0);
                Route materialized;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(routeId, out materialized),
                    "Route did not re-materialize on the first due Tick");
                InGameAssert.IsFalse(DormantContains(routeId),
                    "Materialized route still present in the dormant list");
                InGameAssert.AreEqual(RouteStatus.Paused, materialized.Status,
                    $"Materialized route must come back Paused (fresh-creation state), got {materialized.Status} " +
                    "(MissingSourceRecording here means the committed source tree was not visible to ERS)");
                InGameAssert.AreEqual(0, materialized.CompletedCycles,
                    "Materialized route CompletedCycles not reset");
                InGameAssert.AreEqual(-1L, materialized.LastObservedLoopCycleIndex,
                    "Materialized route loop cursor not reset");
                InGameAssert.IsTrue(AnyLine(lines, "Materialize:", "dormant route(s) materialized"),
                    "No 'Materialize: ... dormant route(s) materialized' log line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline dormant-materialize: PASS routeId={routeId} " +
                    $"cutoff={cutoffUT.ToString("R", IC)} createdUT={createdUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                for (int i = 0; i < committedRecs.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedRecs[i]);
                if (treeAdded) RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                RestoreLedger(preLedger);
            }
        }

        [InGameTest(Category = Category,
            Description = "Production wiring: a dormant route whose CreatedUT is ~1.5s ahead of the live clock re-materializes WITHOUT the test calling Tick - the ParsekScenario.Update 1 Hz orchestrator tick drains it (waits up to ~10s real time; skips when the game clock is frozen)")]
        public IEnumerator DormantRoute_MaterializesViaScenarioDrivenTick()
        {
            double liveUT = RequireLiveContext(requireScenario: true);

            string treeId = NewId("scenario-tick-tree");
            string routeId = NewId("scenario-tick");
            double createdUT = liveUT + 1.5;

            List<GameAction> preLedger = SnapshotLedger();
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);
            var committedRecs = new List<Recording>();
            bool treeAdded = false;
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                RecordingTree tree = BuildTwoLegTree(treeId, routeId);
                RecordingStore.AddCommittedTreeForTesting(tree);
                treeAdded = true;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    RecordingStore.AddCommittedInternal(rec);
                    committedRecs.Add(rec);
                }

                Route route = BuildRouteWithSources(routeId, createdUT, RouteStatus.Active, tree);
                // Install as DORMANT directly (the production install seam), with the
                // committed list isolated to empty so background ticks touch nothing
                // else while we wait.
                RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route> { route });
                InGameAssert.IsTrue(DormantContains(routeId), "Dormant install failed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline scenario-tick: waiting for production tick to materialize " +
                    $"routeId={routeId} createdUT={createdUT.ToString("R", IC)} liveUT={liveUT.ToString("R", IC)}");

                // Wait for ParsekScenario.Update -> RouteOrchestrator.Tick (1 UT-second
                // cadence) to cross CreatedUT and drain the dormant list. ~10s wall
                // budget; bail out early once materialized. If the game clock is not
                // advancing (paused), skip rather than false-fail.
                const float MaxWaitSeconds = 10f;
                float startRealtime = Time.realtimeSinceStartup;
                double startUT = Planetarium.GetUniversalTime();
                bool materialized = false;
                while (Time.realtimeSinceStartup - startRealtime < MaxWaitSeconds)
                {
                    if (RouteStore.TryGetRoute(routeId, out _))
                    {
                        materialized = true;
                        break;
                    }
                    if (Time.realtimeSinceStartup - startRealtime > 4f
                        && Planetarium.GetUniversalTime() - startUT < 0.5)
                    {
                        InGameAssert.Skip(
                            "Game clock is not advancing (paused?); the scenario-driven tick cannot fire. " +
                            $"utDelta={(Planetarium.GetUniversalTime() - startUT).ToString("R", IC)}");
                    }
                    yield return null;
                }

                double waitedUT = Planetarium.GetUniversalTime() - startUT;
                InGameAssert.IsTrue(materialized,
                    "Dormant route was not materialized by the production ParsekScenario.Update tick within " +
                    $"{MaxWaitSeconds.ToString(IC)}s (utAdvanced={waitedUT.ToString("R", IC)}, " +
                    $"dormantCount={RouteStore.DormantRoutes.Count.ToString(IC)}) - the 1 Hz orchestrator wiring did not fire");

                Route mat;
                RouteStore.TryGetRoute(routeId, out mat);
                InGameAssert.AreEqual(RouteStatus.Paused, mat.Status,
                    $"Scenario-tick materialized route must be Paused, got {mat.Status}");
                InGameAssert.IsTrue(AnyLine(lines, "Materialize:", "dormant route(s) materialized"),
                    "No 'Materialize:' log line observed for the scenario-driven materialization");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline scenario-tick: PASS routeId={routeId} " +
                    $"utAdvanced={waitedUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                for (int i = 0; i < committedRecs.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedRecs[i]);
                if (treeAdded) RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // (c) Kept-route reconcile through the real Restore(cutoff) seam
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Restore(cutoff) on kept routes: status derives from kept PLAYER rows (auto rows ignored), armed one-shot flags clear, abandoned-future cursors reset, cycle counters rebuild from kept dispatch/delivery rows, and the post-cutoff dispatch row is Rec-1-retired from the live ledger")]
        public void BundleRestoreCutoff_KeptRoute_StatusDerivedFlagsClearedCountersRebuilt()
        {
            double liveUT = RequireLiveContext(requireScenario: true);
            double cutoffUT = liveUT + SyntheticCutoffOffset;

            string pausedId = NewId("kept-paused");
            string resumedId = NewId("kept-resumed");
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            List<GameAction> preLedger = SnapshotLedger();
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                // Kept route A: live status Active with an abandoned future
                // (armed one-shots, stale cursors, post-cutoff in-flight cycle) and
                // a kept player-pause row that a later AUTO resume row must NOT
                // override.
                Route pausedRoute = BuildMinimalRoute(pausedId, cutoffUT - 100.0, RouteStatus.Active);
                pausedRoute.SendOnceArmed = true;
                pausedRoute.PauseAfterCurrentCycle = true;
                pausedRoute.LastObservedLoopCycleIndex = 7;
                pausedRoute.NextDispatchUT = cutoffUT + 999.0;
                pausedRoute.CurrentCycleStartUT = cutoffUT + 10.0;
                pausedRoute.CompletedCycles = 9;
                pausedRoute.SkippedCycles = 9;

                // Kept route B: live status Paused whose kept rows end in a player
                // resume -> derived Active flips it back.
                Route resumedRoute = BuildMinimalRoute(resumedId, cutoffUT - 100.0, RouteStatus.Paused);

                RouteStore.InstallRoutesAtRewind(
                    new List<Route> { pausedRoute, resumedRoute }, new List<Route>());

                // Kept rows (UT <= cutoff) + one post-cutoff dispatch row to retire.
                Ledger.AddAction(RouteRow(GameActionType.RouteDispatched, pausedId, cutoffUT - 80.0, "cycle-0"));
                Ledger.AddAction(RouteRow(GameActionType.RouteCargoDelivered, pausedId, cutoffUT - 79.0, "cycle-0"));
                Ledger.AddAction(RouteRow(GameActionType.RouteDispatched, pausedId, cutoffUT - 50.0, "cycle-1"));
                Ledger.AddAction(RouteRow(GameActionType.RoutePaused, pausedId, cutoffUT - 40.0, null, "player-pause"));
                // AUTO resume AFTER the player pause: must be ignored by derivation.
                Ledger.AddAction(RouteRow(GameActionType.RouteResumed, pausedId, cutoffUT - 30.0, null, "AutoResume:CatchUp"));
                // Post-cutoff row: Rec-1 retire target.
                Ledger.AddAction(RouteRow(GameActionType.RouteDispatched, pausedId, cutoffUT + 50.0, "cycle-2"));

                Ledger.AddAction(RouteRow(GameActionType.RoutePaused, resumedId, cutoffUT - 70.0, null, "player-pause"));
                Ledger.AddAction(RouteRow(GameActionType.RouteResumed, resumedId, cutoffUT - 60.0, null, "player-activate"));

                ReconciliationBundle bundle = ReconciliationBundle.Capture();
                ReconciliationBundle.Restore(bundle, cutoffUT);

                // Route A: derived Paused (the auto resume row did not count), armed
                // flags cleared, cursors reset, counters rebuilt from kept rows
                // (delivered cycle-0 -> completed=1; maxOrdinal=1, no kept in-flight
                // cycle -> skipped=(1+1)-1=1).
                Route keptA;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(pausedId, out keptA),
                    "Kept route A missing after Restore(cutoff)");
                InGameAssert.AreEqual(RouteStatus.Paused, keptA.Status,
                    $"Kept route A must derive Paused from its kept player rows, got {keptA.Status}");
                InGameAssert.IsFalse(keptA.SendOnceArmed, "SendOnceArmed survived the rewind");
                InGameAssert.IsFalse(keptA.PauseAfterCurrentCycle, "PauseAfterCurrentCycle survived the rewind");
                InGameAssert.AreEqual(-1L, keptA.LastObservedLoopCycleIndex, "Loop cursor not reset at rewind");
                InGameAssert.IsFalse(keptA.CurrentCycleStartUT.HasValue,
                    "Post-cutoff in-flight cycle state not cleared");
                InGameAssert.IsTrue(keptA.NextDispatchUT <= cutoffUT + 0.001,
                    $"Abandoned-future NextDispatchUT not pulled back to the cutoff " +
                    $"(next={keptA.NextDispatchUT.ToString("R", IC)})");
                InGameAssert.AreEqual(1, keptA.CompletedCycles,
                    $"CompletedCycles not reconstructed from kept rows (got {keptA.CompletedCycles.ToString(IC)})");
                InGameAssert.AreEqual(1, keptA.SkippedCycles,
                    $"SkippedCycles not reconstructed from kept rows (got {keptA.SkippedCycles.ToString(IC)})");

                // Route B: derived Active un-pauses it.
                Route keptB;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(resumedId, out keptB),
                    "Kept route B missing after Restore(cutoff)");
                InGameAssert.AreEqual(RouteStatus.Active, keptB.Status,
                    $"Kept route B must derive Active from its kept resume row, got {keptB.Status}");

                // Rec-1: the post-cutoff dispatch row left the live ledger.
                InGameAssert.AreEqual(0, CountRouteRowsAfter(pausedId, cutoffUT),
                    "Post-cutoff route rows survived the Restore(cutoff) retire");

                InGameAssert.IsTrue(AnyLine(lines, "kept-route status fidelity at cutoff"),
                    "No 'kept-route status fidelity at cutoff' summary log line observed");
                InGameAssert.IsTrue(AnyLine(lines, "Restore: retired", "free-standing route row"),
                    "No 'Restore: retired ... free-standing route row(s)' Rec-1 log line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline kept-reconcile: PASS pausedId={pausedId} resumedId={resumedId} " +
                    $"cutoff={cutoffUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // (d) Pending-science cutoff drop
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Restore(cutoff) drops pending science subjects with captureUT strictly after the cutoff (at-cutoff entries kept, Rec-1 strict-> contract) while the parameterless rollback overload restores the captured list wholesale")]
        public void BundleRestoreCutoff_PendingScience_DropsStrictlyAfterCutoff()
        {
            double liveUT = RequireLiveContext(requireScenario: true);
            double cutoffUT = liveUT + SyntheticCutoffOffset;

            var pending = GameStateRecorder.PendingScienceSubjects;
            var preScience = new List<PendingScienceSubject>(pending);
            List<GameAction> preLedger = SnapshotLedger();
            // Store isolation: the finite-cutoff Restore runs the route seam over
            // the captured committed list, and ReconcileStoreAtRewind mutates the
            // Route INSTANCES it keeps (cursor resets, counter reconstruction) -
            // pre-existing player routes must not be inside it.
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            string keptId = NewId("sci-kept");
            string atCutoffId = NewId("sci-at-cutoff");
            string droppedId = NewId("sci-dropped");

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                RouteStore.InstallRoutesAtRewind(new List<Route>(), new List<Route>());

                pending.Add(SciSubject(keptId, cutoffUT - 10.0));
                pending.Add(SciSubject(atCutoffId, cutoffUT));      // exactly at cutoff: KEPT
                pending.Add(SciSubject(droppedId, cutoffUT + 10.0)); // strictly after: DROPPED

                ReconciliationBundle bundle = ReconciliationBundle.Capture();

                // SUCCESS path: cutoff classifies by captureUT.
                ReconciliationBundle.Restore(bundle, cutoffUT);
                InGameAssert.IsTrue(HasSubject(keptId),
                    "Pre-cutoff pending science subject was dropped");
                InGameAssert.IsTrue(HasSubject(atCutoffId),
                    "At-cutoff pending science subject was dropped (strict-> boundary violated)");
                InGameAssert.IsFalse(HasSubject(droppedId),
                    "Post-cutoff pending science subject survived Restore(cutoff)");
                InGameAssert.IsTrue(
                    AnyLine(lines, "Restore: dropped", "pending science subject"),
                    "No 'Restore: dropped ... pending science subject(s)' log line observed");

                // ROLLBACK path: parameterless overload restores wholesale (blind).
                ReconciliationBundle.Restore(bundle);
                InGameAssert.IsTrue(HasSubject(droppedId),
                    "Rollback (parameterless Restore) must restore the pending science list wholesale");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline pending-science: PASS cutoff={cutoffUT.ToString("R", IC)} " +
                    $"kept={keptId} atCutoff={atCutoffId} dropped={droppedId}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                pending.Clear();
                pending.AddRange(preScience);
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // (e) Live RevalidateSources: auto-pause / auto-resume + catch-up net
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "Live RevalidateSources passes against real ERS/ELS: a vanished source recording stamps an AutoPause:MissingSourceRecording RoutePaused row, its return stamps AutoResume:SourcesRestored, and a no-flip live pass repairs a stale latest-paused history with an AutoResume:CatchUp row")]
        public void RevalidateSources_LivePass_AutoPauseResumeAndCatchUpNet()
        {
            double liveUT = RequireLiveContext(requireScenario: true);

            string treeId = NewId("revalidate-tree");
            string routeId = NewId("revalidate");
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            List<GameAction> preLedger = SnapshotLedger();
            int baseCount = preLedger.Count;
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);
            var committedRecs = new List<Recording>();
            bool treeAdded = false;
            Recording removedRec = null;

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                RecordingTree tree = BuildTwoLegTree(treeId, routeId);
                RecordingStore.AddCommittedTreeForTesting(tree);
                treeAdded = true;
                foreach (Recording rec in tree.Recordings.Values)
                {
                    RecordingStore.AddCommittedInternal(rec);
                    committedRecs.Add(rec);
                }

                Route route = BuildRouteWithSources(routeId, liveUT, RouteStatus.Active, tree);
                RouteStore.InstallRoutesAtRewind(new List<Route> { route }, new List<Route>());

                // Healthy baseline: a live pass flips nothing and writes nothing.
                int flips = RouteStore.RevalidateSources("ingame-timeline-baseline", liveUT);
                InGameAssert.AreEqual(0, flips, "Healthy synthetic route flipped on the baseline pass");

                // --- Vanish one source recording -> AutoPause row at the live UT.
                removedRec = committedRecs[1];
                RecordingStore.RemoveCommittedInternal(removedRec);
                int beforeFlip = Ledger.Actions.Count;
                flips = RouteStore.RevalidateSources("ingame-timeline-flip", liveUT);
                InGameAssert.AreEqual(1, flips, "Missing source did not flip the route");
                InGameAssert.AreEqual(RouteStatus.MissingSourceRecording, route.Status,
                    $"Route status after source vanish: {route.Status}");
                InGameAssert.AreEqual(RouteStatus.Active, route.PreMissingStatus,
                    "PreMissingStatus baseline not captured on the into-missing edge");
                GameAction autoPause = LatestLifecycleRow(routeId, beforeFlip);
                InGameAssert.IsNotNull(autoPause, "No AutoPause ledger row emitted on the live flip");
                InGameAssert.AreEqual(GameActionType.RoutePaused, autoPause.Type, "AutoPause row type mismatch");
                InGameAssert.AreEqual("AutoPause:MissingSourceRecording", autoPause.RouteEndpointReason,
                    "AutoPause row reason mismatch");

                // --- Source returns -> recovery to the pre-missing baseline +
                // AutoResume:SourcesRestored row.
                RecordingStore.AddCommittedInternal(removedRec);
                removedRec = null;
                int beforeRecover = Ledger.Actions.Count;
                flips = RouteStore.RevalidateSources("ingame-timeline-recover", liveUT);
                InGameAssert.AreEqual(1, flips, "Source return did not flip the route back");
                InGameAssert.AreEqual(RouteStatus.Active, route.Status,
                    $"Route did not recover to its pre-missing baseline, got {route.Status}");
                GameAction autoResume = LatestLifecycleRow(routeId, beforeRecover);
                InGameAssert.IsNotNull(autoResume, "No AutoResume ledger row emitted on recovery");
                InGameAssert.AreEqual(GameActionType.RouteResumed, autoResume.Type, "AutoResume row type mismatch");
                InGameAssert.AreEqual("AutoResume:SourcesRestored", autoResume.RouteEndpointReason,
                    "AutoResume row reason mismatch");

                // --- Catch-up net: fake a silent-flip desync (latest kept lifecycle
                // row says paused while the route runs Active-family); the next
                // live no-flip pass must repair it with AutoResume:CatchUp.
                Ledger.AddAction(RouteRow(GameActionType.RoutePaused, routeId, liveUT, null, "player-pause"));
                int beforeCatchUp = Ledger.Actions.Count;
                flips = RouteStore.RevalidateSources("ingame-timeline-catchup", liveUT);
                InGameAssert.AreEqual(0, flips, "Catch-up pass unexpectedly flipped the route");
                GameAction catchUp = LatestLifecycleRow(routeId, beforeCatchUp);
                InGameAssert.IsNotNull(catchUp, "Catch-up net emitted no row for the desynced route");
                InGameAssert.AreEqual(GameActionType.RouteResumed, catchUp.Type, "Catch-up row type mismatch");
                InGameAssert.AreEqual("AutoResume:CatchUp", catchUp.RouteEndpointReason,
                    "Catch-up row reason mismatch");

                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "reason=AutoPause:MissingSourceRecording"),
                    "No AutoPause LifecycleMarker log line observed");
                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "reason=AutoResume:SourcesRestored"),
                    "No AutoResume:SourcesRestored LifecycleMarker log line observed");
                InGameAssert.IsTrue(
                    AnyLine(lines, "LifecycleMarker:", "reason=AutoResume:CatchUp"),
                    "No AutoResume:CatchUp LifecycleMarker log line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline revalidate: PASS routeId={routeId} " +
                    $"rows={(Ledger.Actions.Count - baseCount).ToString(IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                if (removedRec != null)
                    RecordingStore.AddCommittedInternal(removedRec); // re-add before removal below
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                for (int i = 0; i < committedRecs.Count; i++)
                    RecordingStore.RemoveCommittedInternal(committedRecs[i]);
                if (treeAdded) RemoveCommittedTree(treeId);
                MissionStore.PruneOrphans(RecordingStore.CommittedTrees);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // (f) Go-back seam components (in-place retire + shared reconcile)
        // ==================================================================

        [InGameTest(Category = Category,
            Description = "The go-back rewind exit's two components against the live ledger/store: Ledger.RetireFutureRouteActionsAtRewind retires post-cutoff route rows in place, then the SHARED RouteRewindClassifier.ReconcileStoreAtRewind (the exact HandleRewindOnLoad call shape) dormants post-cutoff routes and reconciles kept ones from the surviving rows")]
        public void GoBackSeamComponents_RetireInPlace_ThenSharedStoreReconcile()
        {
            double liveUT = RequireLiveContext(requireScenario: false);
            double cutoffUT = liveUT + SyntheticCutoffOffset;

            string keptId = NewId("goback-kept");
            string futureId = NewId("goback-future");
            var lines = new List<string>();
            Action<string> prevObserver = ParsekLog.TestObserverForTesting;

            List<GameAction> preLedger = SnapshotLedger();
            SnapshotRouteLists(out List<Route> preCommitted, out List<Route> preDormant);

            try
            {
                ParsekLog.TestObserverForTesting = l => { lines.Add(l); prevObserver?.Invoke(l); };

                Route keptRoute = BuildMinimalRoute(keptId, cutoffUT - 50.0, RouteStatus.Active);
                keptRoute.SendOnceArmed = true;
                keptRoute.LastObservedLoopCycleIndex = 3;
                keptRoute.NextDispatchUT = cutoffUT + 500.0;
                Route futureRoute = BuildMinimalRoute(futureId, cutoffUT + 5.0, RouteStatus.Active);

                RouteStore.InstallRoutesAtRewind(
                    new List<Route> { keptRoute, futureRoute }, new List<Route>());

                // Kept rows + two post-cutoff rows (a dispatch and a pause marker)
                // that the in-place retire must remove.
                Ledger.AddAction(RouteRow(GameActionType.RouteDispatched, keptId, cutoffUT - 40.0, "cycle-0"));
                Ledger.AddAction(RouteRow(GameActionType.RouteCargoDelivered, keptId, cutoffUT - 39.0, "cycle-0"));
                Ledger.AddAction(RouteRow(GameActionType.RoutePaused, keptId, cutoffUT - 20.0, null, "player-pause"));
                Ledger.AddAction(RouteRow(GameActionType.RouteDispatched, keptId, cutoffUT + 30.0, "cycle-1"));
                Ledger.AddAction(RouteRow(GameActionType.RouteResumed, keptId, cutoffUT + 40.0, null, "player-activate"));

                // Component 1: the in-place ledger retire (go-back seam, mirrors
                // HandleRewindOnLoad's first route step).
                int retired = Ledger.RetireFutureRouteActionsAtRewind(cutoffUT, out List<GameAction> keptActions);
                InGameAssert.AreEqual(2, retired,
                    $"Expected 2 retired post-cutoff route rows, got {retired.ToString(IC)}");
                InGameAssert.AreEqual(0, CountRouteRowsAfter(keptId, cutoffUT),
                    "Post-cutoff route rows survived the in-place retire");
                InGameAssert.IsTrue(
                    AnyLine(lines, "RetireFutureRouteActionsAtRewind: removed"),
                    "No 'RetireFutureRouteActionsAtRewind: removed' log line observed");

                // Component 2: the SHARED store reconcile with the kept list, the
                // exact call shape HandleRewindOnLoad runs next (snapshots passed
                // because InstallRoutesAtRewind wholesale-replaces the lists).
                RouteRewindClassifier.ReconcileStoreAtRewind(
                    new List<Route>(RouteStore.CommittedRoutes),
                    new List<Route>(RouteStore.DormantRoutes),
                    cutoffUT,
                    keptActions,
                    logTag: "Route",
                    logPrefix: "InGameGoBackSeam");

                InGameAssert.IsFalse(RouteStore.TryGetRoute(futureId, out _),
                    "Post-cutoff-created route stayed committed across the go-back reconcile");
                InGameAssert.IsTrue(DormantContains(futureId),
                    "Post-cutoff-created route not dormanted by the go-back reconcile");

                Route kept;
                InGameAssert.IsTrue(RouteStore.TryGetRoute(keptId, out kept),
                    "Kept route missing after the go-back reconcile");
                // The post-cutoff resume row was retired, so the surviving latest
                // player row is the pause -> derived Paused.
                InGameAssert.AreEqual(RouteStatus.Paused, kept.Status,
                    $"Kept route must derive Paused from the SURVIVING rows (the retired resume must not count), got {kept.Status}");
                InGameAssert.IsFalse(kept.SendOnceArmed, "Armed one-shot flag survived the go-back reconcile");
                InGameAssert.AreEqual(-1L, kept.LastObservedLoopCycleIndex,
                    "Loop cursor not reset by the go-back reconcile");
                // Kept rows: dispatch cycle-0 delivered -> completed=1; maxOrdinal=0,
                // no in-flight -> skipped=(0+1)-1=0.
                InGameAssert.AreEqual(1, kept.CompletedCycles, "CompletedCycles not reconstructed");
                InGameAssert.AreEqual(0, kept.SkippedCycles, "SkippedCycles not reconstructed");

                InGameAssert.IsTrue(AnyLine(lines, "kept-route status fidelity at cutoff"),
                    "No 'kept-route status fidelity at cutoff' summary log line observed");

                ParsekLog.Info("TestRunner",
                    $"RouteRewindTimeline go-back-seam: PASS keptId={keptId} futureId={futureId} " +
                    $"retired={retired.ToString(IC)} cutoff={cutoffUT.ToString("R", IC)}");
            }
            finally
            {
                ParsekLog.TestObserverForTesting = prevObserver;
                RouteStore.InstallRoutesAtRewind(preCommitted, preDormant);
                RestoreLedger(preLedger);
            }
        }

        // ==================================================================
        // Helpers
        // ==================================================================

        /// <summary>
        /// Common precondition gate. Skips (attributable, never false-fails)
        /// when the live context a test needs is absent; returns the live UT.
        /// </summary>
        private static double RequireLiveContext(bool requireScenario)
        {
            if (HighLogic.CurrentGame == null)
                InGameAssert.Skip("HighLogic.CurrentGame is null; need a loaded game");
            double liveUT;
            try
            {
                liveUT = Planetarium.GetUniversalTime();
            }
            catch (Exception ex)
            {
                InGameAssert.Skip($"Planetarium.GetUniversalTime threw {ex.GetType().Name}; no live clock");
                return 0.0; // unreachable
            }
            if (liveUT <= 0.0)
                InGameAssert.Skip($"Live UT {liveUT.ToString("R", IC)} <= 0; lifecycle markers would be skipped by the UT guard");
            if (requireScenario && object.ReferenceEquals(ParsekScenario.Instance, null))
                InGameAssert.Skip("ParsekScenario.Instance is null; ReconciliationBundle needs the scenario lists");
            return liveUT;
        }

        private static string NewId(string label)
        {
            return "ingame-rrt-" + label + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static bool AnyLine(List<string> lines, params string[] fragments)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                bool all = true;
                for (int f = 0; f < fragments.Length; f++)
                {
                    if (l.IndexOf(fragments[f], StringComparison.Ordinal) < 0)
                    {
                        all = false;
                        break;
                    }
                }
                if (all) return true;
            }
            return false;
        }

        private static Route BuildMinimalRoute(string id, double createdUT, RouteStatus status)
        {
            return new Route
            {
                Id = id,
                Name = "Parsek RouteRewindTimeline " + id,
                Status = status,
                CreatedUT = createdUT,
                NextDispatchUT = double.MaxValue, // never due on a background tick
                RecordingIds = new List<string>(),
                SourceRefs = new List<RouteSourceRef>(),
                Stops = new List<RouteStop> { new RouteStop() },
            };
        }

        /// <summary>
        /// Two-leg committed chain the route's source refs mirror, so a live
        /// RevalidateSources pass resolves them through the real ERS. Ids carry
        /// the route id so parallel-running saves can never collide (see the
        /// RouteRewindRedeliveryInGameTest id-collision note).
        /// </summary>
        private static RecordingTree BuildTwoLegTree(string treeId, string idSeed)
        {
            var tree = new RecordingTree { Id = treeId, RootRecordingId = idSeed + "-leg0" };
            tree.Recordings[idSeed + "-leg0"] = Leg(idSeed + "-leg0", 0, 1000, 2000);
            tree.Recordings[idSeed + "-leg1"] = Leg(idSeed + "-leg1", 1, 2000, 3000);
            foreach (var rec in tree.Recordings.Values)
                rec.TreeId = treeId;
            return tree;
        }

        private static Recording Leg(string id, int chainIndex, double start, double end)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = "Timeline",
                ChainId = "C0",
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
            };
        }

        /// <summary>
        /// Route whose SourceRefs mirror the tree's recordings field-for-field
        /// (RouteBuilder shape incl. the proof hash) so RevalidateSources reads
        /// them as healthy, not drifted.
        /// </summary>
        private static Route BuildRouteWithSources(
            string id, double createdUT, RouteStatus status, RecordingTree tree)
        {
            Route route = BuildMinimalRoute(id, createdUT, status);
            foreach (Recording rec in tree.Recordings.Values)
            {
                route.RecordingIds.Add(rec.RecordingId);
                route.SourceRefs.Add(new RouteSourceRef
                {
                    RecordingId = rec.RecordingId,
                    TreeId = rec.TreeId,
                    TreeOrder = rec.TreeOrder,
                    RecordingFormatVersion = rec.RecordingFormatVersion,
                    RecordingSchemaGeneration = rec.RecordingSchemaGeneration,
                    SidecarEpoch = rec.SidecarEpoch,
                    StartUT = rec.StartUT,
                    EndUT = rec.EndUT,
                    RouteProofHash = RouteProofHasher.ComputeRouteProofHashFromRecording(rec),
                });
            }
            return route;
        }

        private static GameAction RouteRow(
            GameActionType type, string routeId, double ut, string cycleId, string reason = null)
        {
            return new GameAction
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = -1,
                Sequence = 0,
                RouteEndpointReason = reason,
            };
        }

        private static PendingScienceSubject SciSubject(string subjectId, double captureUT)
        {
            return new PendingScienceSubject
            {
                subjectId = subjectId,
                science = 1.5f,
                subjectMaxValue = 10f,
                captureUT = captureUT,
                reasonKey = "ingame-rrt-test",
                recordingId = null,
            };
        }

        private static bool HasSubject(string subjectId)
        {
            var pending = GameStateRecorder.PendingScienceSubjects;
            for (int i = 0; i < pending.Count; i++)
            {
                if (string.Equals(pending[i].subjectId, subjectId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool DormantContains(string routeId)
        {
            IReadOnlyList<Route> dormant = RouteStore.DormantRoutes;
            for (int i = 0; i < dormant.Count; i++)
            {
                Route r = dormant[i];
                if (r != null && string.Equals(r.Id, routeId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Latest RoutePaused/RouteResumed ledger row for <paramref name="routeId"/>
        /// appended at or after index <paramref name="fromIndex"/>; null when none.
        /// </summary>
        private static GameAction LatestLifecycleRow(string routeId, int fromIndex)
        {
            var actions = Ledger.Actions;
            if (actions == null) return null;
            for (int i = actions.Count - 1; i >= fromIndex && i >= 0; i--)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.Type == GameActionType.RoutePaused || a.Type == GameActionType.RouteResumed)
                    return a;
            }
            return null;
        }

        private static int CountRouteRowsAfter(string routeId, double cutoffUT)
        {
            int count = 0;
            var actions = Ledger.Actions;
            if (actions == null) return 0;
            for (int i = 0; i < actions.Count; i++)
            {
                GameAction a = actions[i];
                if (a == null) continue;
                if (!string.Equals(a.RouteId, routeId, StringComparison.Ordinal)) continue;
                if (a.UT > cutoffUT) count++;
            }
            return count;
        }

        private static List<GameAction> SnapshotLedger()
        {
            var snapshot = new List<GameAction>();
            var actions = Ledger.Actions;
            if (actions != null)
            {
                for (int i = 0; i < actions.Count; i++)
                    snapshot.Add(actions[i]);
            }
            return snapshot;
        }

        private static void RestoreLedger(List<GameAction> snapshot)
        {
            try
            {
                Ledger.Clear();
                if (snapshot != null && snapshot.Count > 0)
                    Ledger.AddActions(snapshot);
            }
            catch (Exception ex)
            {
                ParsekLog.Warn("TestRunner",
                    $"RouteRewindTimeline cleanup: ledger restore failed ({ex.GetType().Name}: {ex.Message})");
            }
        }

        private static void SnapshotRouteLists(out List<Route> committed, out List<Route> dormant)
        {
            committed = new List<Route>();
            dormant = new List<Route>();
            IReadOnlyList<Route> c = RouteStore.CommittedRoutes;
            for (int i = 0; i < c.Count; i++)
                if (c[i] != null) committed.Add(c[i]);
            IReadOnlyList<Route> d = RouteStore.DormantRoutes;
            for (int i = 0; i < d.Count; i++)
                if (d[i] != null) dormant.Add(d[i]);
        }

        private static void RemoveCommittedTree(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return;
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
