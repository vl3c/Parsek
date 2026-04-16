using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §A/#405 of career-earnings-bundle plan: five event types that fire at
    /// KSC (ContractAccepted/Completed/Failed/Cancelled + PartPurchased) must reach
    /// the ledger in real time via LedgerOrchestrator.OnKscSpending. Before the fix
    /// their handlers only wrote to GameStateStore, and nothing converted them to
    /// GameActions until the next recording commit — which for a player staying at
    /// KSC never came.
    ///
    /// The handlers themselves are private instance methods on GameStateRecorder and
    /// take KSP-only types (Contract, AvailablePart) which can't be constructed in unit
    /// tests. We therefore test the narrow contract the handlers rely on: given a well-
    /// formed GameStateEvent, OnKscSpending lands a matching GameAction in the ledger.
    /// </summary>
    [Collection("Sequential")]
    public class GameStateRecorderLedgerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public GameStateRecorderLedgerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void OnKscSpending_ContractAccepted_AddsContractAcceptAction()
        {
            var evt = new GameStateEvent
            {
                ut = 100.0,
                eventType = GameStateEventType.ContractAccepted,
                key = "contract-guid-1",
                detail = "title=Explore Mun;deadline=NaN;failFunds=5000;failRep=10"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept && a.ContractId == "contract-guid-1");
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("KSC spending recorded") && l.Contains("ContractAccept"));
        }

        [Fact]
        public void OnKscSpending_ContractCompleted_AddsContractCompleteAction()
        {
            var evt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.ContractCompleted,
                key = "contract-guid-2",
                detail = "title=Test Flight;fundsReward=8000;repReward=5;sciReward=2"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractComplete && a.ContractId == "contract-guid-2");
            Assert.NotNull(match);
            Assert.Equal(8000f, match.FundsReward);
            Assert.Equal(5f, match.RepReward);
        }

        [Fact]
        public void OnKscSpending_ContractFailed_AddsContractFailAction()
        {
            var evt = new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.ContractFailed,
                key = "contract-guid-3",
                detail = "title=Fail;fundsPenalty=2000;repPenalty=15"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractFail && a.ContractId == "contract-guid-3");
            Assert.NotNull(match);
            Assert.Equal(2000f, match.FundsPenalty);
        }

        [Fact]
        public void OnKscSpending_ContractCancelled_AddsContractCancelAction()
        {
            var evt = new GameStateEvent
            {
                ut = 400.0,
                eventType = GameStateEventType.ContractCancelled,
                key = "contract-guid-4",
                detail = "title=Cancel;fundsPenalty=500;repPenalty=3"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ContractCancel && a.ContractId == "contract-guid-4");
            Assert.NotNull(match);
            Assert.Equal(500f, match.FundsPenalty);
        }

        [Fact]
        public void OnKscSpending_PartPurchased_AddsFundsSpendingActionWithDedupKey()
        {
            var evt = new GameStateEvent
            {
                ut = 500.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600"
            };

            LedgerOrchestrator.OnKscSpending(evt);

            var match = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.FundsSpending && a.DedupKey == "mk1pod");
            Assert.NotNull(match);
            Assert.Equal(FundsSpendingSource.Other, match.FundsSpendingSource);
            Assert.Equal(600f, match.FundsSpent);
        }

        [Fact]
        public void OnKscSpending_TwoDifferentParts_BothLandInLedger()
        {
            // Regression guard for §F dedup key collision — two KSC part purchases
            // at almost-identical UTs must both survive after each is piped through
            // OnKscSpending (the second goes through DeduplicateAgainstLedger against
            // the first).
            var evtA = new GameStateEvent
            {
                ut = 1000.0,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600"
            };
            var evtB = new GameStateEvent
            {
                ut = 1000.02,
                eventType = GameStateEventType.PartPurchased,
                key = "solidBooster",
                detail = "cost=200"
            };

            LedgerOrchestrator.OnKscSpending(evtA);
            LedgerOrchestrator.OnKscSpending(evtB);

            int mkCount = 0, boosterCount = 0;
            foreach (var a in Ledger.Actions)
            {
                if (a.Type != GameActionType.FundsSpending) continue;
                if (a.DedupKey == "mk1pod") mkCount++;
                else if (a.DedupKey == "solidBooster") boosterCount++;
            }

            Assert.Equal(1, mkCount);
            Assert.Equal(1, boosterCount);
        }

        [Fact]
        public void OnKscSpending_DroppedEventType_LogsNoAction()
        {
            // FundsChanged is intentionally dropped by the converter (see §I reconciliation).
            // Guard: OnKscSpending with a dropped event type logs "produced no action"
            // and does NOT add a ledger entry.
            int before = Ledger.Actions.Count;
            var evt = new GameStateEvent
            {
                ut = 600.0,
                eventType = GameStateEventType.FundsChanged,
                key = "",
                valueBefore = 10000,
                valueAfter = 9400
            };

            LedgerOrchestrator.OnKscSpending(evt);

            Assert.Equal(before, Ledger.Actions.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("produced no action"));
        }
    }
}
