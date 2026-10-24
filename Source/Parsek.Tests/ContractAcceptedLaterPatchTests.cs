using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo REFLY-SESSION-SHOWS-CONTRACT-ACCEPTED-LATER: the Re-Fly invoke walks the whole
    /// ledger (horizon double.MaxValue), so a contract the player accepted at the Space
    /// Center after the rewind point is active at the end of that walk. The state patch must
    /// not restore it Active at the session's earlier clock, or the re-fly can complete or
    /// fail a contract before it was accepted.
    /// </summary>
    public class ContractAcceptedLaterPatchTests
    {
        private static GameAction Accept(string id, double ut) => new GameAction
        {
            ActionId = "accept_" + id,
            Type = GameActionType.ContractAccept,
            UT = ut,
            ContractId = id,
            DeadlineUT = double.NaN,
        };

        private static ContractsModule WalkAccepts(params GameAction[] accepts)
        {
            var module = new ContractsModule();
            module.Reset();
            foreach (var a in accepts) module.ProcessAction(a);
            return module;
        }

        // catches: the patch restoring every walk-active contract, so a contract accepted
        // at UT 2000 is Active in stock while the Re-Fly session flies at UT 1000.
        [Fact]
        public void Select_ContractAcceptedAfterNow_IsHeldOutOfStock()
        {
            var module = WalkAccepts(Accept("before", 500.0), Accept("later", 2000.0));

            List<string> acceptedLater;
            var kept = KspStatePatcher.SelectContractsAcceptedByNow(
                module.GetActiveContractIds(), module.GetActiveContractAcceptUT, 1000.0, out acceptedLater);

            Assert.Equal(new[] { "before" }, kept);
            Assert.Equal(new[] { "later" }, acceptedLater);
        }

        [Fact]
        public void Select_AcceptAtTheClockWithinTolerance_IsKept()
        {
            var module = WalkAccepts(Accept("now", 1000.5));

            List<string> acceptedLater;
            var kept = KspStatePatcher.SelectContractsAcceptedByNow(
                module.GetActiveContractIds(), module.GetActiveContractAcceptUT, 1000.0, out acceptedLater);

            Assert.Equal(new[] { "now" }, kept);
            Assert.Empty(acceptedLater);
        }

        // catches: an unready clock (0 during an early load, NaN, or the MaxValue sentinel
        // GetCurrentTimelineUTForLedgerRecalc returns without a Planetarium) emptying
        // stock's active list.
        [Theory]
        [InlineData(0.0)]
        [InlineData(-5.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        [InlineData(double.MaxValue)]
        public void Select_ClockNotReady_FiltersNothing(double nowUT)
        {
            var module = WalkAccepts(Accept("a", 500.0), Accept("b", 2000.0));

            List<string> acceptedLater;
            var kept = KspStatePatcher.SelectContractsAcceptedByNow(
                module.GetActiveContractIds(), module.GetActiveContractAcceptUT, nowUT, out acceptedLater);

            Assert.Equal(new[] { "a", "b" }, kept);
            Assert.Empty(acceptedLater);
        }

        [Fact]
        public void AcceptUT_ResolvedContract_IsNaN()
        {
            var module = WalkAccepts(Accept("c", 500.0));
            module.ProcessAction(new GameAction
            {
                ActionId = "complete_c",
                Type = GameActionType.ContractComplete,
                UT = 600.0,
                ContractId = "c",
            });

            Assert.True(double.IsNaN(module.GetActiveContractAcceptUT("c")));
            Assert.True(double.IsNaN(module.GetActiveContractAcceptUT("never")));
            Assert.True(double.IsNaN(module.GetActiveContractAcceptUT(null)));
        }

        // The walk side of the paradox the patch now prevents: a completion dated before
        // the contract's committed accept is credited, and the later accept makes the
        // contract Active again. Pinned so a change to either half is a visible decision.
        [Fact]
        public void Walk_CompletionBeforeItsAccept_IsCreditedAndTheAcceptReactivates_DocumentsWhyThePatchHoldsItBack()
        {
            var module = new ContractsModule();
            module.Reset();
            module.ProcessAction(new GameAction
            {
                ActionId = "complete_x",
                Type = GameActionType.ContractComplete,
                UT = 1100.0,
                ContractId = "x",
                RecordingId = "refly",
            });
            module.ProcessAction(Accept("x", 2000.0));

            Assert.True(module.IsContractCredited("x"));
            Assert.Contains("x", module.GetActiveContractIds());
        }

        // catches: the filter judging a cutoff walk again, or a walk past the clock
        // against a stale live clock while OnLoad runs (stock sets the clock after the
        // scenario modules load), which removed a contract that really was Active.
        [Fact]
        public void ResolveClock_CutoffWalk_FiltersNothing()
        {
            Assert.True(double.IsNaN(KspStatePatcher.ResolveContractAcceptClockUT(5000.0, null, false, () => 1000.0)));
            Assert.True(double.IsNaN(KspStatePatcher.ResolveContractAcceptClockUT(5000.0, 60.0, false, () => 1000.0)));
        }

        [Fact]
        public void ResolveClock_WalkPastTheClock_UsesTheCallersClockFirst()
        {
            Assert.Equal(60.0, KspStatePatcher.ResolveContractAcceptClockUT(double.MaxValue, 60.0, true, () => 1000.0));
            Assert.Equal(60.0, KspStatePatcher.ResolveContractAcceptClockUT(null, 60.0, false, () => 1000.0));
        }

        [Fact]
        public void ResolveClock_WalkPastTheClock_NoCallerClock_ReadsTheLiveClockOutsideOnLoad()
        {
            Assert.Equal(1000.0, KspStatePatcher.ResolveContractAcceptClockUT(null, null, false, () => 1000.0));
            Assert.Equal(1000.0, KspStatePatcher.ResolveContractAcceptClockUT(double.MaxValue, null, false, () => 1000.0));
        }

        [Fact]
        public void ResolveClock_WalkPastTheClock_DuringOnLoad_FiltersNothing()
        {
            Assert.True(double.IsNaN(KspStatePatcher.ResolveContractAcceptClockUT(null, null, true, () => 1000.0)));
            Assert.True(double.IsNaN(KspStatePatcher.ResolveContractAcceptClockUT(null, double.NaN, true, () => 1000.0)));
            Assert.True(double.IsNaN(KspStatePatcher.ResolveContractAcceptClockUT(null, null, false, null)));
        }

        // The reviewer's case: stock holds X Active, accepted at 3000, the save's clock is
        // 5000, the stale live clock 1000. A cutoff walk at 5000 must keep X.
        [Fact]
        public void StaleLiveClock_CutoffWalk_KeepsTheActiveContract()
        {
            var module = WalkAccepts(Accept("X", 3000.0));
            double clock = KspStatePatcher.ResolveContractAcceptClockUT(5000.0, null, true, () => 1000.0);

            List<string> acceptedLater;
            var kept = KspStatePatcher.SelectContractsAcceptedByNow(
                module.GetActiveContractIds(), module.GetActiveContractAcceptUT, clock, out acceptedLater);

            Assert.Equal(new[] { "X" }, kept);
            Assert.Empty(acceptedLater);
        }

        [Fact]
        public void Select_NullInputs_AreSafe()
        {
            List<string> acceptedLater;
            Assert.Empty(KspStatePatcher.SelectContractsAcceptedByNow(null, null, 1000.0, out acceptedLater));
            Assert.Empty(acceptedLater);

            var kept = KspStatePatcher.SelectContractsAcceptedByNow(
                new[] { "a", "", null }, null, 1000.0, out acceptedLater);
            Assert.Equal(new[] { "a" }, kept);
        }
    }
}
