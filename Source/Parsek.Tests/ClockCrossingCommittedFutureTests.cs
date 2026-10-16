using System;
using System.Collections.Generic;
using Contracts;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// QL-1 (a quicksave loaded at the Space Center behind four KSC actions): the load walks
    /// the ledger at the loaded save's clock, and when the Space Center clock later reaches
    /// the kept committed-future rows each applies once - past the drawdown guard, with the
    /// hire made on the roster and the contract's Offered copy replaced.
    /// </summary>
    [Collection("Sequential")]
    public class ClockCrossingCommittedFutureTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ClockCrossingCommittedFutureTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static void AddAll(params GameAction[] actions)
        {
            for (int i = 0; i < actions.Length; i++)
                Ledger.AddAction(actions[i]);
        }

        private static GameAction FundsSeed(float amount) =>
            new GameAction { UT = 0.0, Type = GameActionType.FundsInitial, InitialFunds = amount };

        private static GameAction Hire(double ut, string name, float cost) =>
            new GameAction { UT = ut, Type = GameActionType.KerbalHire, KerbalName = name, HireCost = cost };

        private static GameAction Spend(double ut, float amount, string key) =>
            new GameAction
            {
                UT = ut,
                Type = GameActionType.FundsSpending,
                FundsSpendingSource = FundsSpendingSource.Other,
                DedupKey = key,
                FundsSpent = amount
            };

        // ================================================================
        // Fix A: the in-session load walks at the loaded save's clock
        // ================================================================

        [Fact]
        public void InSessionLoadClock_PrefersTheLoadedSave_OverAStalePlanetarium()
        {
            // QL-1's measurement: Planetarium still read 1000.10 inside the Space Center
            // OnLoad of a quicksave written at 409.9.
            Assert.Equal(409.9, ParsekScenario.ResolveInSessionLoadLedgerClockUT(409.9, true, 1000.1));
        }

        [Fact]
        public void InSessionLoadClock_FallsBackToPlanetarium_ThenUnknown()
        {
            Assert.Equal(1000.1, ParsekScenario.ResolveInSessionLoadLedgerClockUT(double.NaN, true, 1000.1));
            Assert.Equal(1000.1, ParsekScenario.ResolveInSessionLoadLedgerClockUT(0.0, true, 1000.1));
            Assert.True(double.IsNaN(ParsekScenario.ResolveInSessionLoadLedgerClockUT(double.NaN, false, 1000.1)));
        }

        [Fact]
        public void InSessionLoad_AtTheSaveClock_KeepsRowsAfterItAsCommittedFuture()
        {
            AddAll(FundsSeed(536558f), Spend(1000.0, 150000f, "ts-upgrade"));

            bool cutoffUsed = ParsekScenario.RecalculateLedgerForInSessionLoad(
                isRevert: false, loadedScene: GameScenes.SPACECENTER, planetariumReady: true,
                loadedUT: ParsekScenario.ResolveInSessionLoadLedgerClockUT(409.9, true, 1000.1),
                planetariumUT: 1000.1);

            Assert.True(cutoffUsed);
            Assert.Equal(536558.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("useCurrentUtCutoff=True loadedUT=409.9 planetariumUT=1000.1")
                && l.Contains("hasFutureLedgerActions=True"));
        }

        // ================================================================
        // Fix B: the drawdown guard at a clock crossing
        // ================================================================

        [Fact]
        public void LiveUnchangedSinceLastPatch_IsTheOnlyAuthorization()
        {
            // QL-1 re-flight: the load patched funds to the reservation-aware 324066.25 and
            // nothing moved them before the crossing.
            Assert.True(KspStatePatcher.IsLiveUnchangedSinceLastPatch(324066.25, 324066.25, false, 0.01));
            // Stock moved the pool since (a purchase the ledger has not caught up with).
            Assert.False(KspStatePatcher.IsLiveUnchangedSinceLastPatch(320000.0, 324066.25, false, 0.01));
            // The last patch held a standing clamp: a crossing must not wash it out.
            Assert.False(KspStatePatcher.IsLiveUnchangedSinceLastPatch(324066.25, 324066.25, true, 0.01));
            // Unknown: never patched, or a load since.
            Assert.False(KspStatePatcher.IsLiveUnchangedSinceLastPatch(324066.25, double.NaN, false, 0.01));
        }

        [Fact]
        public void FundsGuard_Unauthorized_HoldsTheReservationRelease_TheQl1ReflightDefect()
        {
            // Measured at the crossing: running 536558 without the rows, live 324066.25 (the
            // load's reservation-aware patch), target 332020 (rows applied, fail reserve
            // released). The plain guard caps the raise to live.
            var held = KspStatePatcher.ResolveFundsPatch(324066.25, 332020.0, 536558.0, false);
            Assert.True(held.Clamped);

            var applied = KspStatePatcher.ResolveFundsPatch(324066.25, 332020.0, 332020.0, true);
            Assert.False(applied.Clamped);
            Assert.Equal(7953.75, applied.Delta, 3);
        }

        [Fact]
        public void FundsGuard_Unauthorized_StillClampsTheFirstFlightsDrawdown()
        {
            var decision = KspStatePatcher.ResolveFundsPatch(
                currentFunds: 536558.0, targetFunds: 332020.0, runningFunds: 332020.0,
                authoritativeReduction: false);
            Assert.True(decision.Clamped);
            Assert.Equal(536558.0, decision.EffectiveTarget, 3);
        }

        [Fact]
        public void SplitCrossedRows_TakesOnlySnapshotRowsWithinTheCutoff()
        {
            var seed = FundsSeed(1000f);
            var crossed = Spend(500.0, 10f, "a");
            var stillFuture = Spend(900.0, 10f, "b");
            var fresh = Spend(500.0, 10f, "c");
            var snapshot = new HashSet<string>(StringComparer.Ordinal)
            {
                crossed.ActionId, stillFuture.ActionId, seed.ActionId
            };

            List<GameAction> taken;
            var rest = LedgerOrchestrator.SplitCrossedCommittedFutureRows(
                new List<GameAction> { seed, crossed, stillFuture, fresh }, snapshot, 600.0, out taken);

            Assert.Single(taken);
            Assert.Same(crossed, taken[0]);
            Assert.Equal(3, rest.Count);
            Assert.Contains(seed, rest);
            Assert.Contains(stillFuture, rest);
            Assert.Contains(fresh, rest);
        }

        [Fact]
        public void ClockAdvance_PastSnapshotRows_LogsTheCrossing()
        {
            AddAll(FundsSeed(536558f), Spend(1000.0, 150000f, "ts-upgrade"), Hire(1000.2, "Verhat Kerman", 62113f));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(409.9, "post-rewind-load");
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(1300.0, "ksc-clock");

            Assert.Equal(324445.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Clock-crossing reached committed-future rows: crossed=2")
                && l.Contains("KerbalHire@1000.2")
                && l.Contains("hires=1"));
        }

        [Fact]
        public void ForwardTimeJump_PastSnapshotRows_IsACrossingToo()
        {
            AddAll(FundsSeed(536558f), Spend(1000.0, 150000f, "ts-upgrade"));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(409.9, "post-rewind-load");
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForTimeJump(1100.0);

            Assert.Contains(logLines, l => l.Contains("Clock-crossing reached committed-future rows: crossed=1"));
        }

        [Fact]
        public void CrossedHire_NotMade_WhenTheFundsPatchDidNotApply()
        {
            // Headless there is no Funding singleton, so the funds patch skips: the hire must
            // not be made without its charge.
            AddAll(FundsSeed(536558f), Hire(1000.2, "Verhat Kerman", 62113f));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(409.9, "post-rewind-load");
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(1300.0, "ksc-clock");

            Assert.Contains(logLines, l => l.Contains("Clock-crossing hire(s) not made")
                && l.Contains("Verhat Kerman"));
            Assert.DoesNotContain(logLines, l => l.Contains("PromoteReachedCommittedHires:"));
        }

        [Fact]
        public void ClockAdvance_RowRecordedAfterThePatch_IsNotACrossedRow()
        {
            // A KSC action made live at the Space Center: stock already charged it, so the
            // cursor's next recalc must not excuse it.
            AddAll(FundsSeed(1000f));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(400.0, "ksc-spending");
            AddAll(Spend(1000.0, 100f, "fresh"));
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(1000.5, "ksc-clock");

            Assert.DoesNotContain(logLines, l => l.Contains("Clock-crossing reached"));
        }

        [Fact]
        public void LiveEventRecalc_IsNeverACrossing()
        {
            AddAll(FundsSeed(1000f), Spend(1000.0, 100f, "future"));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(400.0, "post-rewind-load");
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForLiveTimelineEvent(1300.0, "ksc-spending");

            Assert.DoesNotContain(logLines, l => l.Contains("Clock-crossing reached"));
        }

        [Fact]
        public void ClockAdvance_AfterALoadInvalidatedTheSnapshot_IsNoCrossing()
        {
            AddAll(FundsSeed(1000f), Spend(1000.0, 100f, "future"));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(400.0, "post-rewind-load");
            LedgerOrchestrator.InvalidateCommittedFutureSnapshot("in-session-load");
            logLines.Clear();

            LedgerOrchestrator.RecalculateAndPatchForClockAdvance(1300.0, "ksc-clock");

            Assert.DoesNotContain(logLines, l => l.Contains("Clock-crossing reached"));
        }

        // ================================================================
        // Fix C: the reached hire is made on the roster, uncharged
        // ================================================================

        [Fact]
        public void PromoteReachedHires_PromotesApplicants_LeavesOthersAlone()
        {
            var types = new Dictionary<string, ProtoCrewMember.KerbalType>
            {
                { "Verhat Kerman", ProtoCrewMember.KerbalType.Applicant },
                { "Jeb Kerman", ProtoCrewMember.KerbalType.Crew }
            };
            var promoted = new List<string>();

            int count = KerbalsModule.PromoteReachedCommittedHires(
                new List<string> { "Verhat Kerman", "Jeb Kerman", "Ghost Kerman" },
                name => types.TryGetValue(name, out var t) ? t : (ProtoCrewMember.KerbalType?)null,
                name => { promoted.Add(name); types[name] = ProtoCrewMember.KerbalType.Crew; return true; });

            Assert.Equal(1, count);
            Assert.Equal(new[] { "Verhat Kerman" }, promoted.ToArray());
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("Committed hire applied: 'Verhat Kerman' Applicant -> Crew"));
            Assert.Contains(logLines, l => l.Contains("not on the roster") && l.Contains("Ghost Kerman"));
            Assert.Contains(logLines, l => l.Contains("PromoteReachedCommittedHires: rows=3 promoted=1 notApplicant=1 missing=1 failed=0"));
        }

        // ================================================================
        // Fix D: the restored Active contract replaces its Offered copy
        // ================================================================

        [Fact]
        public void OfferedCopy_MatchesOnlyTheSameGuidStillOffered()
        {
            var id = new Guid("90e4faaf-2029-4c6b-8bc1-40226bb0fc27");
            var other = Guid.NewGuid();
            Func<Guid, Contract.State, KspStatePatcher.ContractFilterEntry> entry = (g, s) =>
                new KspStatePatcher.ContractFilterEntry { Id = g, State = s, IsActive = s == Contract.State.Active };

            Assert.True(KspStatePatcher.IsOfferedCopyOfRestoredContract(entry(id, Contract.State.Offered), id));
            Assert.False(KspStatePatcher.IsOfferedCopyOfRestoredContract(entry(id, Contract.State.Active), id));
            Assert.False(KspStatePatcher.IsOfferedCopyOfRestoredContract(entry(id, Contract.State.Declined), id));
            Assert.False(KspStatePatcher.IsOfferedCopyOfRestoredContract(entry(other, Contract.State.Offered), id));
        }
    }
}
