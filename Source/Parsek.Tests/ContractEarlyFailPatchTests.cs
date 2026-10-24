using System;
using System.Collections.Generic;
using System.Reflection;
using Parsek.Patches;
using Xunit;
using Contract = Contracts.Contract;

namespace Parsek.Tests
{
    /// <summary>
    /// Rule 1 of the committed-value rule (owner ruling L1.1, game-actions design 3.13): a
    /// contract whose complete, fail or cancel the committed timeline holds after now cannot
    /// be failed earlier. Stock's single fail funnel is <c>Contract.SetState(State.Failed)</c>;
    /// <see cref="ContractEarlyFailPatch"/> refuses it there, reading the Cancel block's own
    /// predicate.
    /// </summary>
    [Collection("Sequential")]
    public class ContractEarlyFailPatchTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ContractEarlyFailPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GameStateStore.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ContractEarlyFailPatch.ResetForTesting();
            CommittedFutureIndexCache.NowUtProviderForTesting = () => 100.0;
        }

        public void Dispose()
        {
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            CommittedFutureIndexCache.ResetForTesting();
            ContractEarlyFailPatch.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private sealed class FakeContract : Contract
        {
            private readonly string title;

            internal FakeContract(string title, Guid guid, State state)
            {
                this.title = title;
                typeof(Contract).GetField("contractGuid", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, guid);
                typeof(Contract).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(this, state);
            }

            protected override string GetTitle() => title;
        }

        private static GameAction Row(GameActionType type, double ut, string id, string recordingId = null) =>
            new GameAction { UT = ut, Type = type, ContractId = id, RecordingId = recordingId };

        private static bool InvokePrefix(Contract contract, Contract.State newState)
        {
            var prefix = typeof(ContractEarlyFailPatch).GetMethod("Prefix", BindingFlags.Static | BindingFlags.NonPublic);
            return (bool)prefix.Invoke(null, new object[] { contract, newState });
        }

        [Theory]
        [InlineData(GameActionType.ContractComplete)]
        [InlineData(GameActionType.ContractFail)]
        [InlineData(GameActionType.ContractCancel)]
        public void CommittedOutcomeLater_RefusesTheFail_AndLogsIt(GameActionType outcome)
        {
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "c1"));
            Ledger.AddAction(Row(outcome, 500, "c1"));

            bool allowed = ContractEarlyFailPatch.ShouldAllowFail("c1", "Explore the Mun");

            Assert.False(allowed);
            Assert.Equal(1, ContractEarlyFailPatch.RefusedFailCount);
            string kind = outcome.ToString();
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractEarlyFailPatch]")
                && l.Contains("refusing fail of \"Explore the Mun\" guid=c1 end=Failed - committed " + kind + " ut=500 nowUT=100 recording=(ksc)"));
        }

        [Fact]
        public void NoCommittedOutcome_AllowsTheFail()
        {
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "open"));
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "other"));
            Ledger.AddAction(Row(GameActionType.ContractComplete, 500, "other"));

            Assert.True(ContractEarlyFailPatch.ShouldAllowFail("open", "Open"));
            Assert.True(ContractEarlyFailPatch.ShouldAllowFail("", "Empty"));
            Assert.Equal(0, ContractEarlyFailPatch.RefusedFailCount);
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractEarlyFailPatch]")
                && l.Contains("allowing fail for guid=open end=Failed - no committed outcome (nowUT=100)"));
        }

        [Theory]
        [InlineData(GameActionType.ContractComplete, 50.0)]
        [InlineData(GameActionType.ContractComplete, 100.0)]
        [InlineData(GameActionType.ContractFail, 50.0)]
        [InlineData(GameActionType.ContractCancel, 50.0)]
        public void CommittedOutcomeAlreadyReached_RefusesTheFail_UntilTheCareerUpdateAppliesIt(
            GameActionType outcome, double outcomeUT)
        {
            // The flight passed the committed outcome's UT, but the flight scene applies it only
            // at the next recalculation, so stock still holds the contract Active. A fail here
            // would charge a penalty the walk skips (the contract already ended), and the stock
            // pool reference would keep that gap as a leak: a permanent loss.
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "c1"));
            Ledger.AddAction(Row(outcome, outcomeUT, "c1"));

            bool allowed = ContractEarlyFailPatch.ShouldAllowFail("c1", "Explore the Mun", "DeadlineExpired");

            Assert.False(allowed);
            Assert.Equal(1, ContractEarlyFailPatch.RefusedFailCount);
            Assert.Contains(logLines, l => l.Contains("[INFO][ContractEarlyFailPatch]")
                && l.Contains("refusing fail of \"Explore the Mun\" guid=c1 end=DeadlineExpired - committed "
                    + outcome + " ut=" + outcomeUT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)
                    + " already reached nowUT=100 recording=(ksc); the contract stays Active until the next career update applies it"));
        }

        [Fact]
        public void ReachedAndFutureOutcomes_TheFutureOneIsNamed()
        {
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "c1"));
            Ledger.AddAction(Row(GameActionType.ContractComplete, 50, "c1"));
            Ledger.AddAction(Row(GameActionType.ContractFail, 500, "c1"));

            Assert.False(ContractEarlyFailPatch.ShouldAllowFail("c1", "c1"));
            Assert.Contains(logLines, l => l.Contains("refusing fail of \"c1\" guid=c1 end=Failed - committed ContractFail ut=500 nowUT=100"));
        }

        [Fact]
        public void CommittedContractResolution_IsTheEarliestAtAnyUT()
        {
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "c1"));
            Ledger.AddAction(Row(GameActionType.ContractCancel, 400, "c1"));
            Ledger.AddAction(Row(GameActionType.ContractComplete, 50, "c1"));
            var index = CommittedFutureIndexCache.Current;

            var any = StockUiReservationPredicates.CommittedContractResolution(index, "c1");
            Assert.NotNull(any);
            Assert.Equal(CommittedFutureKind.ContractComplete, any.Kind);
            Assert.Equal(50.0, any.UT);
            Assert.Null(StockUiReservationPredicates.CommittedContractResolution(index, "none"));
            Assert.Null(StockUiReservationPredicates.CommittedContractResolution(index, ""));
            Assert.Null(StockUiReservationPredicates.CommittedContractResolution(null, "c1"));
            // The ahead-of-now form (the Cancel block) is unchanged.
            Assert.Equal(CommittedFutureKind.ContractCancel,
                StockUiReservationPredicates.CommittedContractResolutionAfter(index, "c1", 100.0).Kind);
        }

        [Fact]
        public void OutcomeTaggedToAnUncommittedRecording_DoesNotBlock()
        {
            // A row of the live tree (or any recording outside the effective set) reserves
            // nothing: a flight in progress never blocks.
            Ledger.AddAction(Row(GameActionType.ContractAccept, 10, "c1"));
            Ledger.AddAction(Row(GameActionType.ContractComplete, 500, "c1", recordingId: "not-committed"));

            Assert.True(ContractEarlyFailPatch.ShouldAllowFail("c1", "c1"));
            Assert.Equal(0, ContractEarlyFailPatch.RefusedFailCount);
        }

        [Fact]
        public void BypassesWhileReplayingActions()
        {
            Ledger.AddAction(Row(GameActionType.ContractComplete, 500, "c1"));
            GameStateRecorder.IsReplayingActions = true;

            Assert.True(ContractEarlyFailPatch.ShouldAllowFail("c1", "c1"));
            Assert.Contains(logLines, l => l.Contains("[VERBOSE][ContractEarlyFailPatch]") && l.Contains("bypass - replay in progress"));
        }

        [Fact]
        public void Prefix_RefusesOnlyAnEndOfAnActiveContract()
        {
            var guid = Guid.NewGuid();
            Ledger.AddAction(Row(GameActionType.ContractComplete, 500, guid.ToString()));
            var active = new FakeContract("Active one", guid, Contract.State.Active);
            var offered = new FakeContract("Offered one", guid, Contract.State.Offered);

            Assert.False(InvokePrefix(active, Contract.State.Failed));
            Assert.True(InvokePrefix(active, Contract.State.Completed));
            Assert.False(InvokePrefix(active, Contract.State.DeadlineExpired));
            Assert.True(InvokePrefix(active, Contract.State.Cancelled));
            Assert.False(InvokePrefix(active, Contract.State.OfferExpired));
            Assert.True(InvokePrefix(offered, Contract.State.Failed));
            Assert.True(InvokePrefix(offered, Contract.State.OfferExpired));
            Assert.Equal(3, ContractEarlyFailPatch.RefusedFailCount);
            Assert.Contains(logLines, l => l.Contains("refusing fail of \"Active one\"") && l.Contains("end=DeadlineExpired"));
        }

        [Fact]
        public void TargetMethod_IsStocksProtectedSetState()
        {
            var method = ContractEarlyFailPatch.ResolveTargetMethodForTesting() as MethodInfo;

            Assert.NotNull(method);
            Assert.Equal("SetState", method.Name);
            Assert.Equal(typeof(Contract), method.DeclaringType);
            Assert.False(method.IsStatic);
            var parameters = method.GetParameters();
            Assert.Single(parameters);
            Assert.Equal(typeof(Contract.State), parameters[0].ParameterType);
        }
    }
}
