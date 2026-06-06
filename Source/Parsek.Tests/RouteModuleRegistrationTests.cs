using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Registration tests for <see cref="RouteModule"/>. Verifies the
    /// <see cref="LedgerOrchestrator"/> wires the module into the second tier
    /// AFTER <see cref="FundsModule"/> and that re-initialization is idempotent.
    /// </summary>
    [Collection("Sequential")]
    public class RouteModuleRegistrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RouteModuleRegistrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void LedgerOrchestrator_Initialize_RegistersRouteModule()
        {
            // Fails if Initialize stops constructing RouteModule or stops registering
            // it with the recalculation engine — a RouteDispatched action would
            // then silently disappear from the walk.
            LedgerOrchestrator.Initialize();

            Assert.NotNull(LedgerOrchestrator.Route);

            // Wire one seed + one RouteDispatched action through a real walk and
            // assert the route module saw it. This is the end-to-end registration
            // proof — accessor non-null alone could pass even if RegisterModule
            // was missed.
            // FundsInitial seed required because RecalculateAndPatch's seed-readiness
            // check short-circuits without one, even when the test is only verifying
            // the route module's registration wiring.
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.RouteDispatched,
                RouteId = "route-reg-1",
                RouteCycleId = "cyc-1"
            });

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains("[Route]")
                && l.Contains("Processed RouteDispatched")
                && l.Contains("route-reg-1"));
        }

        [Fact]
        public void RouteModule_RegisteredAfterFundsModule_InSecondTier()
        {
            // Fails if RouteModule is moved to the first tier OR re-ordered before
            // FundsModule inside the second tier — the future dispatch integration
            // depends on FundsModule.ProcessAction having already set Affordable on
            // a KSC-origin Career RouteCargoDebited action by the time RouteModule
            // sees it.
            //
            // BRITTLE-BY-DESIGN: we scrape the RecalcEngine registration log lines
            // because there is no public inspection API on the engine today. When
            // RecalculationEngine grows a stable ordered-module accessor (e.g.
            // GetSecondTierModuleNamesForTesting()), replace this scrape with it.
            LedgerOrchestrator.Initialize();

            int fundsIdx = -1;
            int routeIdx = -1;
            for (int i = 0; i < logLines.Count; i++)
            {
                string l = logLines[i];
                if (!l.Contains("[RecalcEngine]") || !l.Contains("Registered second-tier module"))
                    continue;
                if (l.Contains("FundsModule")) fundsIdx = i;
                else if (l.Contains("RouteModule")) routeIdx = i;
            }

            Assert.True(fundsIdx >= 0, "FundsModule registration line not found");
            Assert.True(routeIdx >= 0, "RouteModule registration line not found");
            Assert.True(fundsIdx < routeIdx,
                $"RouteModule registered before FundsModule (fundsIdx={fundsIdx}, routeIdx={routeIdx}); " +
                "the future dispatch integration depends on FundsModule running first.");
        }

        [Fact]
        public void RouteModule_Initialize_IsIdempotent()
        {
            // Fails if Initialize stops short-circuiting on the `initialized` flag —
            // a double-Process from two registrations would double-count
            // DispatchedCycles on every walk.
            LedgerOrchestrator.Initialize();
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 0f
            });
            Ledger.AddAction(new GameAction
            {
                UT = 100.0,
                Type = GameActionType.RouteDispatched,
                RouteId = "route-idem-1",
                RouteCycleId = "cyc-1"
            });

            LedgerOrchestrator.RecalculateAndPatch();

            int dispatchInfoCount = 0;
            foreach (var line in logLines)
            {
                if (line.Contains("[Route]")
                    && line.Contains("Processed RouteDispatched")
                    && line.Contains("route-idem-1"))
                {
                    dispatchInfoCount++;
                }
            }
            Assert.Equal(1, dispatchInfoCount);
        }
    }
}
