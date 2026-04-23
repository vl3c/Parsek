using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Covers §K/#401/#396 of career-earnings-bundle plan: on load, any GameStateStore
    /// event or committed science subject that has no matching ledger action must be
    /// synthesized as a GameAction and added to the ledger. This repairs the c1
    /// (bricked funds) and sci1 (bricked science) saves on first load.
    ///
    /// Epoch isolation (review §5.6): only events whose epoch matches
    /// MilestoneStore.CurrentEpoch are considered — old-branch events must not
    /// leak into the new epoch's ledger.
    ///
    /// Idempotency: the LedgerHasMatchingAction guard makes repeat calls no-ops.
    /// </summary>
    [Collection("Sequential")]
    public class LedgerRecoveryMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public LedgerRecoveryMigrationTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            MilestoneStore.CurrentEpoch = 0;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            MilestoneStore.CurrentEpoch = 0;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        [Fact]
        public void Recovery_EmptyStore_NoOp()
        {
            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(0, recovered);
            Assert.Empty(Ledger.Actions);
        }

        [Fact]
        public void Recovery_ContractAccepted_Synthesized()
        {
            var contractEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ContractAccepted,
                key = "c-guid-1",
                detail = "title=Mun;deadline=NaN;failFunds=0;failRep=0"
            };
            GameStateStore.AddEvent(ref contractEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            Assert.Contains(Ledger.Actions, a =>
                a.Type == GameActionType.ContractAccept && a.ContractId == "c-guid-1");
        }

        [Fact]
        public void Recovery_PartPurchased_Synthesized()
        {
            var mk1Evt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.PartPurchased,
                key = "mk1pod",
                detail = "cost=600"
            };
            GameStateStore.AddEvent(ref mk1Evt);
            var tankEvt = new GameStateEvent
            {
                ut = 201,
                eventType = GameStateEventType.PartPurchased,
                key = "liquidFuelTank",
                detail = "cost=300"
            };
            GameStateStore.AddEvent(ref tankEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(2, recovered);
            Assert.Equal(2, Ledger.Actions.Count(a =>
                a.Type == GameActionType.FundsSpending &&
                a.FundsSpendingSource == FundsSpendingSource.Other));
        }

        [Fact]
        public void Recovery_ScienceSubject_Synthesized()
        {
            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "crewReport@KerbinSrfLanded",
                    science = 16.44f,
                    subjectMaxValue = 50f
                }
            });

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            var earning = Ledger.Actions.FirstOrDefault(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == "crewReport@KerbinSrfLanded");
            Assert.NotNull(earning);
            Assert.Equal(16.44f, earning.ScienceAwarded, precision: 2);
        }

        [Fact]
        public void Recovery_Idempotent_SecondCallIsNoOp()
        {
            var contractEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ContractAccepted,
                key = "c-guid-idem",
                detail = "title=X"
            };
            GameStateStore.AddEvent(ref contractEvt);

            int firstPass = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();
            int secondPass = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, firstPass);
            Assert.Equal(0, secondPass);
            Assert.Single(Ledger.Actions);
        }

        [Fact]
        public void Recovery_EpochIsolation_SkipsOldEpochEvents()
        {
            MilestoneStore.CurrentEpoch = 5;

            // Event from an old epoch — must NOT be recovered.
            var oldEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ContractAccepted,
                key = "old-guid",
                detail = "title=old"
            };
            // GameStateStore.AddEvent stamps with CurrentEpoch, so we seed it manually via
            // a lower-level add to simulate leftover events. RemoveEvent/AddEvent won't
            // preserve the epoch we want, so we set CurrentEpoch before adding.
            MilestoneStore.CurrentEpoch = 3;
            GameStateStore.AddEvent(ref oldEvt);

            // Now switch to the current epoch and add a fresh event.
            MilestoneStore.CurrentEpoch = 5;
            var newEvt = new GameStateEvent
            {
                ut = 200,
                eventType = GameStateEventType.ContractAccepted,
                key = "new-guid",
                detail = "title=new"
            };
            GameStateStore.AddEvent(ref newEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            // Only the new-epoch event synthesized.
            Assert.Equal(1, recovered);
            Assert.Contains(Ledger.Actions, a => a.ContractId == "new-guid");
            Assert.DoesNotContain(Ledger.Actions, a => a.ContractId == "old-guid");
        }

        [Fact]
        public void Recovery_ExistingMatchingAction_NotDuplicated()
        {
            LedgerOrchestrator.Initialize();

            // Pre-seed ledger with an action that matches the store event.
            Ledger.AddAction(new GameAction
            {
                UT = 100,
                Type = GameActionType.ContractAccept,
                ContractId = "already-there"
            });

            var alreadyEvt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ContractAccepted,
                key = "already-there",
                detail = ""
            };
            GameStateStore.AddEvent(ref alreadyEvt);

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(0, recovered);
            // Still only one action for this contract.
            Assert.Equal(1, Ledger.Actions.Count(a =>
                a.Type == GameActionType.ContractAccept && a.ContractId == "already-there"));
        }

        [Fact]
        public void Recovery_ExistingScienceEarning_NotDuplicated()
        {
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ScienceEarning,
                SubjectId = "already-sci",
                ScienceAwarded = 5f,
                SubjectMaxValue = 10f
            });

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "already-sci",
                    science = 5f,
                    subjectMaxValue = 10f
                }
            });

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(0, recovered);
            Assert.Equal(1, Ledger.Actions.Count(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == "already-sci"));
        }

        [Fact]
        public void Recovery_PartialExistingScienceEarning_SynthesizesOnlyMissingDelta()
        {
            LedgerOrchestrator.Initialize();

            Ledger.AddAction(new GameAction
            {
                UT = 0,
                Type = GameActionType.ScienceEarning,
                SubjectId = "partial-sci",
                ScienceAwarded = 5f,
                SubjectMaxValue = 50f
            });

            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject
                {
                    subjectId = "partial-sci",
                    science = 16.44f,
                    subjectMaxValue = 50f
                }
            });

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(1, recovered);
            var earnings = Ledger.Actions.Where(a =>
                a.Type == GameActionType.ScienceEarning && a.SubjectId == "partial-sci").ToList();
            Assert.Equal(2, earnings.Count);
            Assert.Contains(earnings, a => Math.Abs(a.ScienceAwarded - 11.44f) < 0.01f);
            Assert.Equal(16.44f, earnings.Sum(a => a.ScienceAwarded), precision: 2);
        }

        [Fact]
        public void Recovery_MultipleEventTypes_AllSynthesized()
        {
            var c1Evt = new GameStateEvent
            {
                ut = 100,
                eventType = GameStateEventType.ContractAccepted,
                key = "c1",
                detail = "title=a"
            };
            GameStateStore.AddEvent(ref c1Evt);
            var p1Evt = new GameStateEvent
            {
                ut = 101,
                eventType = GameStateEventType.PartPurchased,
                key = "p1",
                detail = "cost=500"
            };
            GameStateStore.AddEvent(ref p1Evt);
            var p2Evt = new GameStateEvent
            {
                ut = 102,
                eventType = GameStateEventType.PartPurchased,
                key = "p2",
                detail = "cost=200"
            };
            GameStateStore.AddEvent(ref p2Evt);
            ScienceTestHelpers.CommitScienceSubjects(new List<PendingScienceSubject>
            {
                new PendingScienceSubject { subjectId = "s1", science = 2f, subjectMaxValue = 10f },
                new PendingScienceSubject { subjectId = "s2", science = 1f, subjectMaxValue = 10f }
            });

            int recovered = LedgerOrchestrator.TryRecoverBrokenLedgerOnLoad();

            Assert.Equal(5, recovered);
            Assert.Contains(logLines, l =>
                l.Contains("[LedgerOrchestrator]") && l.Contains("synthesized"));
        }

        [Fact]
        public void MapEventTypeToActionType_KnownTypes_ReturnsExpected()
        {
            Assert.Equal(GameActionType.ContractAccept,
                LedgerOrchestrator.MapEventTypeToActionType(GameStateEventType.ContractAccepted));
            Assert.Equal(GameActionType.FundsSpending,
                LedgerOrchestrator.MapEventTypeToActionType(GameStateEventType.PartPurchased));
            Assert.Equal(GameActionType.ScienceSpending,
                LedgerOrchestrator.MapEventTypeToActionType(GameStateEventType.TechResearched));
        }

        [Fact]
        public void IsRecoverableEventType_FundsChanged_False()
        {
            Assert.False(LedgerOrchestrator.IsRecoverableEventType(GameStateEventType.FundsChanged));
            Assert.False(LedgerOrchestrator.IsRecoverableEventType(GameStateEventType.ContractOffered));
        }

        [Fact]
        public void IsRecoverableEventType_ContractAccepted_True()
        {
            Assert.True(LedgerOrchestrator.IsRecoverableEventType(GameStateEventType.ContractAccepted));
            Assert.True(LedgerOrchestrator.IsRecoverableEventType(GameStateEventType.PartPurchased));
        }
    }
}
