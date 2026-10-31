using System;
using System.Collections.Generic;
using System.Reflection;
using Contracts;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-CROSSED-OUTCOME-MISSING-FROM-STOCK-ARCHIVE: when the clock crosses a
    /// committed contract outcome row, PatchContracts takes the Active contract off stock's
    /// active list; it must land in stock's finished list in the walk's outcome state
    /// (silently), not vanish from the Mission Control Archive.
    /// </summary>
    [Collection("Sequential")]
    public class ContractCrossedOutcomeArchiveTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContractCrossedOutcomeArchiveTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            KspStatePatcher.ResetForTesting();
        }

        public void Dispose()
        {
            KspStatePatcher.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private sealed class FakeContract : Contract
        {
            internal FakeContract(Guid guid, State state)
            {
                typeof(Contract).GetField("contractGuid", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(this, guid);
                typeof(Contract).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(this, state);
            }

            protected override string GetTitle() => "Crossed Test";
        }

        private static KspStatePatcher.ContractFilterEntry Entry(Guid g, Contract.State state) =>
            new KspStatePatcher.ContractFilterEntry
            {
                Id = g,
                IsActive = state == Contract.State.Active,
                IsTerminal = state == Contract.State.Completed || state == Contract.State.Failed
                    || state == Contract.State.DeadlineExpired || state == Contract.State.Cancelled
                    || state == Contract.State.OfferExpired,
                State = state,
            };

        private static HashSet<KspStatePatcher.ContractRemovalKey> CurrentKeys(
            List<KspStatePatcher.ContractFilterEntry> current,
            HashSet<Guid> active, HashSet<Guid> terminal,
            Dictionary<Guid, ContractTerminalOutcome> outcomes,
            HashSet<Guid> tombstoned = null) =>
            KspStatePatcher.BuildCurrentContractRemovalKeys(current, active, terminal, outcomes, tombstoned);

        /// <summary>A real clock after every end UT the cells below use.</summary>
        private const double Clock = 2000.0;

        private static Dictionary<Guid, KspStatePatcher.RetiredContractMove> Plan(
            List<KspStatePatcher.ContractFilterEntry> current,
            List<KspStatePatcher.ContractFilterEntry> finished,
            HashSet<Guid> active,
            Dictionary<Guid, ContractTerminalOutcome> outcomes,
            Dictionary<Guid, double> uts,
            out int alreadyFinished, out int noFinishedState,
            HashSet<Guid> tombstoned = null, double clockUT = Clock)
        {
            return Plan(current, finished, active, outcomes, uts, out alreadyFinished,
                out noFinishedState, out _, tombstoned, clockUT);
        }

        private static Dictionary<Guid, KspStatePatcher.RetiredContractMove> Plan(
            List<KspStatePatcher.ContractFilterEntry> current,
            List<KspStatePatcher.ContractFilterEntry> finished,
            HashSet<Guid> active,
            Dictionary<Guid, ContractTerminalOutcome> outcomes,
            Dictionary<Guid, double> uts,
            out int alreadyFinished, out int noFinishedState, out int noClock,
            HashSet<Guid> tombstoned, double clockUT)
        {
            var terminal = new HashSet<Guid>(outcomes.Keys);
            var currentKeys = CurrentKeys(current, active, terminal, outcomes, tombstoned);
            var finishedKeys = KspStatePatcher.BuildFinishedContractRemovalKeys(
                finished, terminal, outcomes, tombstoned);
            return KspStatePatcher.PlanRetiredActiveContractMoves(current, currentKeys, terminal,
                outcomes, uts, finished, finishedKeys, clockUT, out alreadyFinished, out noFinishedState,
                out _, out noClock);
        }

        [Theory]
        [InlineData((int)ContractTerminalOutcome.Failed, Contract.State.Failed)]
        [InlineData((int)ContractTerminalOutcome.DeadlineExpired, Contract.State.DeadlineExpired)]
        [InlineData((int)ContractTerminalOutcome.Completed, Contract.State.Completed)]
        [InlineData((int)ContractTerminalOutcome.Cancelled, Contract.State.Cancelled)]
        public void CrossedOutcome_ActiveContractMovesToFinished_InTheOutcomeState(
            int outcomeValue, Contract.State expected)
        {
            var outcome = (ContractTerminalOutcome)outcomeValue;
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, outcome } },
                new Dictionary<Guid, double> { { g, 1234.5 } },
                out int already, out int noState);

            Assert.Single(moves);
            Assert.Equal(expected, moves[g].FinishedState);
            Assert.Equal(outcome, moves[g].Outcome);
            Assert.Equal(1234.5, moves[g].FinishedUT);
            Assert.Equal(0, already);
            Assert.Equal(0, noState);
        }

        [Fact]
        public void RequirementsLost_IsNotArchived_StockDropsOfferExpired()
        {
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.RequirementsLost } },
                null, out int already, out int noState);

            Assert.Empty(moves);
            Assert.Equal(1, noState);
            Assert.Equal(0, already);
        }

        [Fact]
        public void StaleActiveNotTerminalInWalk_IsDroppedNotArchived()
        {
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(), new Dictionary<Guid, ContractTerminalOutcome>(),
                null, out int already, out int noState);

            Assert.Empty(moves);
            Assert.Equal(0, already);
            Assert.Equal(0, noState);
        }

        [Fact]
        public void LedgerActiveContract_StaysActive_NoMove()
        {
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid> { g }, new Dictionary<Guid, ContractTerminalOutcome>(),
                null, out _, out _);

            Assert.Empty(moves);
        }

        [Fact]
        public void FinishedCopyAlreadySurvives_NoSecondCopy()
        {
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var finished = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Failed) };
            var moves = Plan(current, finished, new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.Failed } },
                null, out int already, out _);

            Assert.Empty(moves);
            Assert.Equal(1, already);
        }

        [Fact]
        public void FinishedCopyRemovedThisPatchAsTombstoned_TheActiveOneIsArchivedInstead()
        {
            // The old branch's Completed copy is tombstoned and removed; the surviving outcome
            // is a Failed retry, so the live Active copy is archived as Failed.
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var finished = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Completed) };
            var moves = Plan(current, finished, new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.Failed } },
                null, out int already, out _, tombstoned: new HashSet<Guid> { g });

            Assert.Single(moves);
            Assert.Equal(Contract.State.Failed, moves[g].FinishedState);
            Assert.Equal(0, already);
        }

        [Fact]
        public void OutcomeAfterTheClock_IsNotArchivedEarly()
        {
            // A walk past the clock (the Re-Fly invoke's whole-ledger walk) holds a contract
            // terminal whose committed outcome is still in the future of the session.
            var later = Guid.NewGuid();
            var now = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry>
            {
                Entry(later, Contract.State.Active),
                Entry(now, Contract.State.Active),
            };
            var outcomes = new Dictionary<Guid, ContractTerminalOutcome>
            {
                { later, ContractTerminalOutcome.Completed },
                { now, ContractTerminalOutcome.Failed },
            };
            var uts = new Dictionary<Guid, double> { { later, 5000.0 }, { now, 1000.5 } };
            var terminal = new HashSet<Guid>(outcomes.Keys);
            var currentKeys = CurrentKeys(current, new HashSet<Guid>(), terminal, outcomes);

            var moves = KspStatePatcher.PlanRetiredActiveContractMoves(current, currentKeys, terminal,
                outcomes, uts, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<KspStatePatcher.ContractRemovalKey>(), 1000.0,
                out _, out _, out int outcomeLater, out int noClock);

            Assert.Single(moves);
            Assert.True(moves.ContainsKey(now));
            Assert.Equal(1, outcomeLater);
            Assert.Equal(0, noClock);
        }

        [Theory]
        [InlineData(double.NaN)]
        [InlineData(0.0)]
        [InlineData(double.PositiveInfinity)]
        public void NoRealClock_NothingMoves_FailClosed(double clockUT)
        {
            // The full-replay paths (OnLoad with no cutoff, the deferred scene-load patch,
            // the cold-load fallback) carry no real clock: the walk may hold terminal a
            // contract whose committed outcome is still in the future.
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.Completed } },
                new Dictionary<Guid, double> { { g, 100.0 } },
                out int already, out int noState, out int noClock, null, clockUT);

            Assert.Empty(moves);
            Assert.Equal(1, noClock);
            Assert.Equal(0, already);
            Assert.Equal(0, noState);
        }

        [Fact]
        public void FullReplayThenCutoffWalk_ContractIsNeverInBothLists()
        {
            // Patch 1: a full replay (no real clock) holds the contract Completed at a
            // committed outcome after the save's clock. Nothing is filed.
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var finished = new List<KspStatePatcher.ContractFilterEntry>();
            var fullReplayOutcomes = new Dictionary<Guid, ContractTerminalOutcome>
            {
                { g, ContractTerminalOutcome.Completed }
            };
            var moves = Plan(current, finished, new HashSet<Guid>(), fullReplayOutcomes,
                new Dictionary<Guid, double> { { g, 5000.0 } },
                out _, out _, out int noClock, null, double.NaN);
            Assert.Empty(moves);
            Assert.Equal(1, noClock);
            foreach (var m in moves.Values)
                finished.Add(Entry(m.Id, m.FinishedState));

            // Patch 2: the cutoff walk at the save's clock (1000) holds it Active; the
            // restore puts it back on the active list.
            var restored = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var noOutcomes = new Dictionary<Guid, ContractTerminalOutcome>();
            KspStatePatcher.PartitionContractsForPatch(restored, finished, new HashSet<Guid> { g },
                new HashSet<Guid>(), noOutcomes, null, out var toRemoveCurrent, out var toRemoveFinished,
                out var survivingActive, out _);
            var moves2 = Plan(restored, finished, new HashSet<Guid> { g }, noOutcomes, null,
                out _, out _, out _, null, 1000.0);

            Assert.Contains(g, survivingActive);
            Assert.Empty(toRemoveCurrent);
            Assert.Empty(toRemoveFinished);
            Assert.Empty(moves2);
            Assert.DoesNotContain(finished, f => f.Id == g);
        }

        [Fact]
        public void UnknownEndUT_IsNaN()
        {
            var g = Guid.NewGuid();
            var current = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Active) };
            var moves = Plan(current, new List<KspStatePatcher.ContractFilterEntry>(),
                new HashSet<Guid>(),
                new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.Failed } },
                new Dictionary<Guid, double>(), out _, out _);

            Assert.True(double.IsNaN(moves[g].FinishedUT));
        }

        [Fact]
        public void NextPatch_KeepsTheArchivedCopy_AndNeverRestoresOrDuplicatesIt()
        {
            var g = Guid.NewGuid();
            var outcomes = new Dictionary<Guid, ContractTerminalOutcome> { { g, ContractTerminalOutcome.Failed } };
            var terminal = new HashSet<Guid> { g };
            var current = new List<KspStatePatcher.ContractFilterEntry>();
            var finished = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Failed) };

            KspStatePatcher.PartitionContractsForPatch(current, finished, new HashSet<Guid>(), terminal,
                outcomes, null, out var toRemoveCurrent, out var toRemoveFinished,
                out var survivingActive, out var survivingTerminal);
            var moves = Plan(current, finished, new HashSet<Guid>(), outcomes, null, out _, out _);

            Assert.Empty(toRemoveCurrent);
            Assert.Empty(toRemoveFinished);
            Assert.Empty(survivingActive);
            Assert.Contains(g, survivingTerminal);
            Assert.Empty(moves);
        }

        [Fact]
        public void OutcomeRowTombstonedLater_ArchivedCopyIsRemoved_SoTheRestoreCanReAddIt()
        {
            // A Re-Fly that replaces the outcome tombstones the row: the walk holds the
            // contract Active again, and the archived copy goes the way any stock-finished
            // copy of a tombstoned row goes.
            var g = Guid.NewGuid();
            var finished = new List<KspStatePatcher.ContractFilterEntry> { Entry(g, Contract.State.Failed) };
            var keys = KspStatePatcher.BuildFinishedContractRemovalKeys(finished,
                new HashSet<Guid>(), new Dictionary<Guid, ContractTerminalOutcome>(),
                new HashSet<Guid> { g });

            Assert.Single(keys);
        }

        [Fact]
        public void TryRetireContractSilently_WritesStateAndDate_FiresNothing()
        {
            var g = Guid.NewGuid();
            var c = new FakeContract(g, Contract.State.Active);
            int fired = 0;
            c.OnStateChange.Add(st => fired++);

            Assert.True(KspStatePatcher.TryRetireContractSilently(c, Contract.State.Failed, 4321.0));

            Assert.Equal(Contract.State.Failed, c.ContractState);
            Assert.Equal(4321.0, c.DateFinished);
            Assert.Equal(0, fired);
        }

        [Fact]
        public void TryRetireContractSilently_NaNUT_LeavesDateFinished()
        {
            var c = new FakeContract(Guid.NewGuid(), Contract.State.Active);
            double before = c.DateFinished;

            Assert.True(KspStatePatcher.TryRetireContractSilently(c, Contract.State.DeadlineExpired, double.NaN));

            Assert.Equal(Contract.State.DeadlineExpired, c.ContractState);
            Assert.Equal(before, c.DateFinished);
        }

        [Fact]
        public void Archive_AddsToFinished_LogsOneInfoLinePerMove()
        {
            var moved = Guid.NewGuid();
            var dropped = Guid.NewGuid();
            var removed = new List<Contract>
            {
                new FakeContract(moved, Contract.State.Active),
                new FakeContract(dropped, Contract.State.Active),
            };
            var moves = new Dictionary<Guid, KspStatePatcher.RetiredContractMove>
            {
                {
                    moved, new KspStatePatcher.RetiredContractMove
                    {
                        Id = moved,
                        Outcome = ContractTerminalOutcome.Failed,
                        FinishedState = Contract.State.Failed,
                        FinishedUT = 900.0
                    }
                }
            };
            var finished = new List<Contract>();
            int archived = 0, failed = 0;

            KspStatePatcher.ArchiveRetiredActiveContracts(removed, moves, finished, ref archived, ref failed);

            Assert.Single(finished);
            Assert.Equal(moved, finished[0].ContractGuid);
            Assert.Equal(Contract.State.Failed, finished[0].ContractState);
            Assert.Equal(1, archived);
            Assert.Equal(0, failed);
            Assert.Contains(logLines, l => l.Contains("[KspStatePatcher]")
                && l.Contains("filed ended contract " + moved)
                && l.Contains("as Failed (outcome=Failed, finishedUT=900)")
                && l.Contains("no GameEvents"));
            Assert.DoesNotContain(logLines, l => l.Contains(dropped.ToString()));
        }

        [Fact]
        public void Archive_NoFinishedList_CountsFailureAndWarns()
        {
            var g = Guid.NewGuid();
            var removed = new List<Contract> { new FakeContract(g, Contract.State.Active) };
            var moves = new Dictionary<Guid, KspStatePatcher.RetiredContractMove>
            {
                { g, new KspStatePatcher.RetiredContractMove { Id = g, Outcome = ContractTerminalOutcome.Completed,
                    FinishedState = Contract.State.Completed, FinishedUT = 1.0 } }
            };
            int archived = 0, failed = 0;

            KspStatePatcher.ArchiveRetiredActiveContracts(removed, moves, null, ref archived, ref failed);

            Assert.Equal(0, archived);
            Assert.Equal(1, failed);
            Assert.Equal(Contract.State.Active, removed[0].ContractState);
            Assert.Contains(logLines, l => l.Contains("[WARN]") && l.Contains("could not file ended contract " + g));
        }

        [Fact]
        public void ContractsModule_TerminalUTs_FollowTheWalk()
        {
            var module = new ContractsModule();
            module.ProcessAction(new GameAction { Type = GameActionType.ContractAccept, UT = 100, ContractId = "f" });
            module.ProcessAction(new GameAction { Type = GameActionType.ContractFail, UT = 800, ContractId = "f" });
            module.ProcessAction(new GameAction { Type = GameActionType.ContractAccept, UT = 100, ContractId = "c" });
            module.ProcessAction(new GameAction { Type = GameActionType.ContractComplete, UT = 500, ContractId = "c" });
            module.ProcessAction(new GameAction { Type = GameActionType.ContractAccept, UT = 100, ContractId = "x" });
            module.ProcessAction(new GameAction { Type = GameActionType.ContractCancel, UT = 300, ContractId = "x" });
            module.ProcessAction(new GameAction
            {
                Type = GameActionType.ContractAccept, UT = 100, ContractId = "d", DeadlineUT = 600f
            });
            module.CheckDeadlines(650.0);

            var uts = module.GetTerminalContractUTs();
            Assert.Equal(800.0, uts["f"]);
            Assert.Equal(500.0, uts["c"]);
            Assert.Equal(300.0, uts["x"]);
            Assert.Equal(600.0, uts["d"]);

            // A fail row on the contract the walk already expired keeps the deadline UT.
            module.ProcessAction(new GameAction { Type = GameActionType.ContractFail, UT = 650, ContractId = "d" });
            Assert.Equal(600.0, module.GetTerminalContractUTs()["d"]);

            // A re-accept clears it; a reset clears all.
            module.ProcessAction(new GameAction { Type = GameActionType.ContractAccept, UT = 900, ContractId = "f" });
            Assert.False(module.GetTerminalContractUTs().ContainsKey("f"));
            module.Reset();
            Assert.Empty(module.GetTerminalContractUTs());
        }
    }
}
