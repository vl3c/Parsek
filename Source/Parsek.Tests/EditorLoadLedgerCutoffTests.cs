using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo EDITOR-LOAD-WALKS-PAST-THE-CLOCK: after a Re-Fly merge (or any rewind) the ledger
    /// holds committed rows ahead of the clock. The in-session load into the VAB / SPH walked
    /// the whole ledger, so a committed future contract advance reached the live funds and the
    /// editor let the player build with money the career had not earned yet (L13 run
    /// 2026-10-25_0144: +6750 in the VAB, taken back at the launch). The editor now takes the
    /// current-UT cutoff like the flight, Space Center and Tracking Station loads.
    /// </summary>
    [Collection("Sequential")]
    public class EditorLoadLedgerCutoffTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public EditorLoadLedgerCutoffTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ResetAll();
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ResetAll();
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void ResetAll()
        {
            RecordingStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            RewindContext.ResetForTesting();
            RevertDetector.ResetForTesting();
        }

        // catches: the editor load going back to the plain walk, which pays the committed
        // future advance (UT 1000) into the funds at the editor's clock (UT 400).
        [Fact]
        public void InSessionEditorLoad_FutureContractAdvance_StaysOutOfTheFunds()
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = 500000f,
            });
            Ledger.AddAction(new GameAction
            {
                UT = 1000.0, Type = GameActionType.ContractAccept, ContractId = "c-future",
                ContractType = "PartTest", AdvanceFunds = 6750f, FundsPenalty = 6750f,
                RepPenalty = 3f, DeadlineUT = 9.0e6,
            });

            bool cutoffUsed = ParsekScenario.RecalculateLedgerForInSessionLoad(
                isRevert: false, loadedScene: GameScenes.EDITOR, planetariumReady: true, loadedUT: 400.0);

            Assert.True(cutoffUsed);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("useCurrentUtCutoff=True")
                && l.Contains("loadedScene=EDITOR"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("RecalculateAndPatch: ") && l.Contains("cutoffUT=null"));
            Assert.Equal(500000.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }
    }
}
