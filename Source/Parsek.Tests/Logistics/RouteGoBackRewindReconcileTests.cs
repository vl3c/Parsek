using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek;
using Parsek.Logistics;
using Xunit;

namespace Parsek.Tests.Logistics
{
    /// <summary>
    /// Go-back rewind route reconciliation (preservation-branch audit,
    /// 2026-07-19): the plain Rewind-to-Launch / warp-back OnLoad exit
    /// (<c>ParsekScenario.HandleRewindOnLoad</c>) must run the SAME route
    /// reconciliation as the Re-Fly seam. Covers the in-place ledger retire
    /// (<see cref="Ledger.RetireFutureRouteActionsAtRewind"/>), both-exits
    /// parity for the shared
    /// <see cref="RouteRewindClassifier.ReconcileStoreAtRewind"/> helper
    /// (direct call == <c>ReconciliationBundle.Restore(cutoff)</c> on the
    /// same fixture), and a source-text gate on the HandleRewindOnLoad
    /// hookup (the scenario's OnLoad is not xUnit-drivable; same gate
    /// pattern as <see cref="RouteStoreScenarioIntegrationTests"/>).
    /// [Collection("Sequential")] + full static reset per the shared-static
    /// rule (mirrors <see cref="RouteRewindDormantTests"/>).
    /// </summary>
    [Collection("Sequential")]
    public class RouteGoBackRewindReconcileTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteGoBackRewindReconcileTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            GroupHierarchyStore.ResetForTesting();
            GroupHierarchyStore.ResetGroupsForTesting();
            MilestoneStore.ResetForTesting();
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static Route MakeRoute(
            string id, double createdUT, RouteStatus status = RouteStatus.Active)
        {
            return new Route
            {
                Id = id,
                Name = "route-" + id,
                Status = status,
                CreatedUT = createdUT,
                Stops = new List<RouteStop>(),
            };
        }

        private static GameAction Marker(
            string routeId, GameActionType type, double ut, int sequence = 0)
        {
            return new GameAction
            {
                Type = type,
                UT = ut,
                RouteId = routeId,
                RouteStopIndex = -1,
                Sequence = sequence,
                RouteEndpointReason = "test",
            };
        }

        private static GameAction Dispatched(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteDispatched,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 0,
            };
        }

        private static GameAction Delivered(string routeId, string cycleId, double ut)
        {
            return new GameAction
            {
                Type = GameActionType.RouteCargoDelivered,
                UT = ut,
                RouteId = routeId,
                RouteCycleId = cycleId,
                RouteStopIndex = 0,
                Sequence = 3,
            };
        }

        private static GameAction NonRoute(double ut)
        {
            return new GameAction { Type = GameActionType.FundsEarning, UT = ut };
        }

        // ------------------------------------------------------------------
        // Ledger.RetireFutureRouteActionsAtRewind (in-place go-back retire)
        // ------------------------------------------------------------------

        [Fact]
        public void RetireInPlace_DropsOnlyFutureRouteRows_BumpsVersion()
        {
            Ledger.AddAction(NonRoute(100.0));
            Ledger.AddAction(Dispatched("r", "cycle-0", 200.0));   // kept (<= cutoff)
            Ledger.AddAction(Dispatched("r", "cycle-1", 700.0));   // retired
            Ledger.AddAction(Delivered("r", "cycle-1", 710.0));    // retired
            Ledger.AddAction(NonRoute(800.0));                     // non-route: kept even past cutoff
            int versionBefore = Ledger.StateVersion;

            int retired = Ledger.RetireFutureRouteActionsAtRewind(
                500.0, out List<GameAction> kept);

            Assert.Equal(2, retired);
            Assert.Equal(3, Ledger.Actions.Count);
            Assert.DoesNotContain(Ledger.Actions,
                a => a.Type == GameActionType.RouteDispatched && a.UT > 500.0);
            Assert.DoesNotContain(Ledger.Actions,
                a => a.Type == GameActionType.RouteCargoDelivered);
            // Non-route actions untouched (the determinism invariant): the
            // go-back path keeps the committed timeline's future rows.
            Assert.Contains(Ledger.Actions,
                a => a.Type == GameActionType.FundsEarning && a.UT == 800.0);
            // Kept list mirrors the surviving ledger for the store reconcile.
            Assert.Equal(3, kept.Count);
            Assert.True(Ledger.StateVersion > versionBefore,
                "in-place retire must bump the ledger StateVersion (ELS cache invalidation)");
            Assert.Contains(logLines, l =>
                l.Contains("[Ledger]") && l.Contains("RetireFutureRouteActionsAtRewind")
                && l.Contains("removed 2"));
        }

        [Fact]
        public void RetireInPlace_NoFutureRouteRows_NoOpNoVersionBump()
        {
            Ledger.AddAction(Dispatched("r", "cycle-0", 200.0));
            Ledger.AddAction(NonRoute(900.0));
            int versionBefore = Ledger.StateVersion;

            int retired = Ledger.RetireFutureRouteActionsAtRewind(
                500.0, out List<GameAction> kept);

            Assert.Equal(0, retired);
            Assert.Equal(2, Ledger.Actions.Count);
            Assert.Equal(2, kept.Count);
            Assert.Equal(versionBefore, Ledger.StateVersion);
        }

        [Fact]
        public void RetireInPlace_BoundaryRowAtCutoff_IsKept()
        {
            // Strict > per the Rec-1 emit-ordering contract: a row stamped
            // exactly at the cutoff has its physical effect in the reverted
            // world, so it must survive.
            Ledger.AddAction(Dispatched("r", "cycle-0", 500.0));

            int retired = Ledger.RetireFutureRouteActionsAtRewind(500.0, out _);

            Assert.Equal(0, retired);
            Assert.Single(Ledger.Actions);
        }

        // ------------------------------------------------------------------
        // Both-exits parity: ReconcileStoreAtRewind direct call must equal
        // the Restore(cutoff) result on the same fixture.
        // ------------------------------------------------------------------

        /// <summary>Installs the shared fixture into RouteStore + Ledger.
        /// Returns the four route instances (pre, post, q, sleeper).</summary>
        private static (Route pre, Route post, Route q, Route sleeper) InstallFixture()
        {
            // "pre": kept; abandoned-future cursor, armed flags, inflated
            // counters, linked to the future route "post".
            var pre = MakeRoute("pre", 100.0);
            pre.LastObservedLoopCycleIndex = 9;
            pre.PauseAfterCurrentCycle = true;
            pre.SendOnceArmed = true;
            pre.CompletedCycles = 9;
            pre.SkippedCycles = 9;
            pre.LinkedRouteId = "post";
            // "post": created after the cutoff -> dormant.
            var post = MakeRoute("post", 900.0);
            post.LinkedRouteId = "pre";
            // "q": Paused at capture; kept markers end in RouteResumed.
            var q = MakeRoute("q", 100.0, RouteStatus.Paused);
            // an earlier-rewind sleeper carried forward.
            var sleeper = MakeRoute("older-sleeper", 950.0);

            RouteStore.InstallRoutesAtRewind(
                new List<Route> { pre, post, q },
                new List<Route> { sleeper });

            Ledger.AddAction(Dispatched("pre", "cycle-0", 200.0));
            Ledger.AddAction(Delivered("pre", "cycle-0", 210.0));
            Ledger.AddAction(Dispatched("pre", "cycle-1", 300.0));
            Ledger.AddAction(Marker("pre", GameActionType.RoutePaused, 400.0));
            Ledger.AddAction(Marker("pre", GameActionType.RouteResumed, 700.0)); // retired
            Ledger.AddAction(Marker("q", GameActionType.RoutePaused, 200.0));
            Ledger.AddAction(Marker("q", GameActionType.RouteResumed, 300.0));
            Ledger.AddAction(Marker("q", GameActionType.RoutePaused, 800.0));    // retired
            Ledger.AddAction(NonRoute(100.0));

            return (pre, post, q, sleeper);
        }

        /// <summary>Order-independent, field-complete snapshot of the
        /// post-reconcile route-store state for parity comparison.</summary>
        private static List<string> SnapshotStoreState()
        {
            string Describe(Route r) =>
                $"{r.Id}|{r.Status}|completed={r.CompletedCycles}|skipped={r.SkippedCycles}" +
                $"|cursor={r.LastObservedLoopCycleIndex}|anchor={r.WindowAnchorCycleIndex}" +
                $"|linked={r.LinkedRouteId ?? "<null>"}|partnerCycle={r.LastConsumedPartnerCycle}" +
                $"|pauseAfter={r.PauseAfterCurrentCycle}|sendOnce={r.SendOnceArmed}";

            var lines = new List<string>();
            lines.AddRange(RouteStore.CommittedRoutes
                .Select(r => "committed:" + Describe(r)).OrderBy(s => s, StringComparer.Ordinal));
            lines.AddRange(RouteStore.DormantRoutes
                .Select(r => "dormant:" + Describe(r)).OrderBy(s => s, StringComparer.Ordinal));
            return lines;
        }

        [Fact]
        public void ReconcileStoreAtRewind_DirectCall_EqualsBundleRestoreResult()
        {
            const double cutoffUT = 500.0;

            // --- Exit A: the Re-Fly seam (Restore(bundle, cutoff)). ---
            InstallFixture();
            var bundle = ReconciliationBundle.Capture();
            ReconciliationBundle.Restore(bundle, cutoffUT);
            List<string> reFlyOutcome = SnapshotStoreState();

            // --- Reset, rebuild the identical fixture. ---
            RouteStore.ResetForTesting();
            Ledger.ResetForTesting();
            InstallFixture();

            // --- Exit B: the go-back seam (HandleRewindOnLoad shape):
            // in-place ledger retire, then the shared reconcile over live
            // RouteStore snapshots. ---
            Ledger.RetireFutureRouteActionsAtRewind(
                cutoffUT, out List<GameAction> keptLedgerActions);
            RouteRewindClassifier.ReconcileStoreAtRewind(
                new List<Route>(RouteStore.CommittedRoutes),
                new List<Route>(RouteStore.DormantRoutes),
                cutoffUT,
                keptLedgerActions,
                logTag: "Rewind",
                logPrefix: "OnLoad go-back");
            List<string> goBackOutcome = SnapshotStoreState();

            Assert.Equal(reFlyOutcome, goBackOutcome);

            // Spot-check the semantics both exits must produce: "pre" kept +
            // fully reconciled (cursor reset, derived Paused from the kept
            // marker at 400, flags cleared, counters rebuilt from kept rows:
            // 1 delivered, maxOrdinal 1 -> 1 skipped), link severed two-sided;
            // "post" + "older-sleeper" dormant, "post" keeping its
            // former-partner hint.
            Route pre = RouteStore.CommittedRoutes.First(r => r.Id == "pre");
            Assert.Equal(RouteStatus.Paused, pre.Status);
            Assert.Equal(-1, pre.LastObservedLoopCycleIndex);
            Assert.False(pre.PauseAfterCurrentCycle);
            Assert.False(pre.SendOnceArmed);
            Assert.Equal(1, pre.CompletedCycles);
            Assert.Equal(1, pre.SkippedCycles);
            Assert.Null(pre.LinkedRouteId);
            Route q = RouteStore.CommittedRoutes.First(r => r.Id == "q");
            Assert.Equal(RouteStatus.Active, q.Status);
            Assert.Equal(2, RouteStore.DormantRoutes.Count);
            Assert.Equal("pre",
                RouteStore.DormantRoutes.First(r => r.Id == "post").LinkedRouteId);

            // The go-back caller's log identity (grep-stable per exit).
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("OnLoad go-back: routes at rewind cutoff"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("OnLoad go-back: kept-route status fidelity"));
            // The Re-Fly exit kept its historical log identity (asserted in
            // RouteRewindStatusFidelityTests too; re-checked here so the
            // parity refactor cannot silently retag it).
            Assert.Contains(logLines, l =>
                l.Contains("[ReconciliationBundle]") && l.Contains("Restore: kept-route status fidelity"));
        }

        [Fact]
        public void ReconcileStoreAtRewind_NullKeptActions_TreatedAsEmpty()
        {
            var route = MakeRoute("r", 100.0);
            route.CompletedCycles = 4;
            RouteStore.InstallRoutesAtRewind(
                new List<Route> { route }, new List<Route>());

            RouteRewindClassifier.ReconcileStoreAtRewind(
                new List<Route>(RouteStore.CommittedRoutes),
                new List<Route>(RouteStore.DormantRoutes),
                500.0,
                keptActions: null,
                logTag: "Rewind",
                logPrefix: "OnLoad go-back");

            // No kept dispatch rows -> counters reset to zero; store intact.
            Assert.Single(RouteStore.CommittedRoutes);
            Assert.Equal(0, route.CompletedCycles);
        }

        // ------------------------------------------------------------------
        // Hookup gate: HandleRewindOnLoad must invoke the reconcile.
        // ------------------------------------------------------------------

        // We cannot drive ParsekScenario.OnLoad/HandleRewindOnLoad from xUnit
        // (Unity GameEvents, HighLogic, StartCoroutine, RewindContext scene
        // flow), so a source-text gate catches a literal deletion of the
        // go-back hookup - same pattern as
        // RouteStoreScenarioIntegrationTests.Scenario_OnSaveAndOnLoad_InvokeRouteStoreCodec.
        // The ordering matters: the in-place ledger retire must precede the
        // store reconcile (which consumes the kept rows), and both must run
        // before the career-state cutoff walk (GameStateStore baseline prune +
        // recalc), all inside HandleRewindOnLoad while RewindAdjustedUT is
        // still populated (EndRewind clears it).
        [Fact]
        public void HandleRewindOnLoad_RunsRetireThenReconcileBeforeCareerRestore()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            if (!File.Exists(scenarioPath))
            {
                scenarioPath = Path.Combine(projectRoot,
                    "Parsek", "ParsekScenario.cs");
            }
            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);

            int entryIdx = source.IndexOf(
                "HandleRewindOnLoad:entry", StringComparison.Ordinal);
            int retireIdx = source.IndexOf(
                "Ledger.RetireFutureRouteActionsAtRewind(", StringComparison.Ordinal);
            int reconcileIdx = source.IndexOf(
                "Logistics.RouteRewindClassifier.ReconcileStoreAtRewind(",
                StringComparison.Ordinal);
            int pruneIdx = source.IndexOf(
                "GameStateStore.PruneBaselinesAfterUT(", StringComparison.Ordinal);
            int exitIdx = source.IndexOf(
                "HandleRewindOnLoad:exit", StringComparison.Ordinal);

            Assert.True(entryIdx >= 0, "HandleRewindOnLoad entry marker missing");
            Assert.True(retireIdx > entryIdx,
                "HandleRewindOnLoad must call Ledger.RetireFutureRouteActionsAtRewind " +
                "(go-back route-row retire) after its entry marker");
            Assert.True(reconcileIdx > retireIdx,
                "HandleRewindOnLoad must call RouteRewindClassifier.ReconcileStoreAtRewind " +
                "AFTER the ledger retire (it consumes the kept rows)");
            Assert.True(pruneIdx > reconcileIdx,
                "the route reconcile must run BEFORE the career-state cutoff walk " +
                "(GameStateStore.PruneBaselinesAfterUT + recalc)");
            Assert.True(exitIdx > pruneIdx,
                "the whole block must sit inside HandleRewindOnLoad (before its exit marker)");
        }
    }
}
