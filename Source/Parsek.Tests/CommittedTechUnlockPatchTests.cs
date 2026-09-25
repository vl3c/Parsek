using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A committed tech unlock whose UT the Space Center clock passes must reach stock R&amp;D.
    ///
    /// The KSC ledger cursor (<c>ParsekKSC.AdvanceCareerLedgerForKscUT</c>) recalculates
    /// through <see cref="LedgerOrchestrator.RecalculateAndPatchForLiveTimelineEvent"/> when
    /// the clock reaches the next committed row. When that row is the LAST one, no row lies
    /// after now, so the recalculation is cutoff-less and skips the two-direction
    /// <see cref="KspStatePatcher.PatchTechTree"/> (#559). Before the fix nothing else applied
    /// the unlock: the walk charged the node's science and stock kept it locked. The add-only
    /// committed-unlock pass (<see cref="KspStatePatcher.PlanCommittedTechUnlocksForPatch"/>)
    /// now unlocks every committed node at or before the live clock on that walk.
    /// </summary>
    [Collection("Sequential")]
    public class CommittedTechUnlockPatchTests : IDisposable
    {
        private const string OrchestratorTag = "[LedgerOrchestrator]";
        private const string PatcherTag = "[KspStatePatcher]";

        private const string NodeAtT = "survivability";
        private const string LaterNode = "advRocketry";
        private const double T = 200.0;

        private readonly List<string> logLines = new List<string>();

        public CommittedTechUnlockPatchTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
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

        private static GameAction TechRow(double ut, string nodeId, bool affordable = true)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.ScienceSpending,
                NodeId = nodeId,
                Cost = 5f,
                Affordable = affordable,
            };
        }

        private static void SeedLedger(params GameAction[] rows)
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.ScienceInitial,
                InitialScience = 1000f
            });
            foreach (var row in rows)
                Ledger.AddAction(row);
        }

        // ================================================================
        // The KSC cursor crossing the LAST committed row, end to end
        // ================================================================

        [Fact]
        public void KscCursorCrossesLastCommittedTechRow_NodeIsPlannedAvailable()
        {
            SeedLedger(TechRow(T, NodeAtT));
            const double lastApplied = 190.0;
            const double now = T + 0.5;
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;

            // The cursor's own decisions, as AdvanceCareerLedgerForKscUT takes them.
            Assert.True(LedgerOrchestrator.TryGetNextActionUTAfter(lastApplied, out double next));
            Assert.Equal(T, next);
            Assert.True(ParsekKSC.ShouldAdvanceCareerLedgerForKscUT(now, lastApplied, next, 0.05));
            string reason = ParsekKSC.GetCareerLedgerAdvanceReasonForKscUT(now, lastApplied, 0.05);

            LedgerOrchestrator.RecalculateAndPatchForLiveTimelineEvent(now, reason);

            // The shape: no committed row after the clock, so the walk is cutoff-less.
            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("Live-event recalc decision")
                && l.Contains("hasFutureLedgerActions=False"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains(OrchestratorTag) && l.Contains("tech-tree patch enabled"));
            // The committed node is in the add-only unlock plan at the live clock.
            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("committed tech unlocks add-only at liveUT=200.5")
                && l.Contains("count=1")
                && l.Contains("ids=[" + NodeAtT + "]"));
            // And the plan reached the patcher (R&D is absent headlessly, so it stops there).
            Assert.Contains(logLines, l =>
                l.Contains(PatcherTag)
                && l.Contains("PatchCommittedTechUnlocks: ResearchAndDevelopment.Instance is null"));
        }

        [Fact]
        public void KscCursorCrossesMidTimelineRow_TwoDirectionPatchOwnsTheTree()
        {
            // Control: with a committed row still ahead, the cursor passes a tech cutoff, so
            // PatchTechTree runs and the add-only pass stays out of it.
            SeedLedger(TechRow(T, NodeAtT), TechRow(T + 100.0, LaterNode));
            const double now = T + 0.5;
            CommittedFutureIndexCache.NowUtProviderForTesting = () => now;

            LedgerOrchestrator.RecalculateAndPatchForLiveTimelineEvent(now, "ksc-clock");

            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag) && l.Contains("hasFutureLedgerActions=True"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains(OrchestratorTag) && l.Contains("committed tech unlocks"));
        }

        [Fact]
        public void CutoffLessWalkBeforeTheRow_NeverUnlocksAFutureNode()
        {
            // A cutoff-less recalc (e.g. a commit) while a committed tech row is still ahead
            // of the clock: the add-only set is capped at the clock and stays empty.
            SeedLedger(TechRow(T, NodeAtT));
            CommittedFutureIndexCache.NowUtProviderForTesting = () => T - 50.0;

            LedgerOrchestrator.RecalculateAndPatch();

            Assert.Contains(logLines, l =>
                l.Contains(OrchestratorTag)
                && l.Contains("committed tech unlocks add-only at liveUT=150")
                && l.Contains("count=0"));
            Assert.DoesNotContain(logLines, l => l.Contains(OrchestratorTag) && l.Contains(NodeAtT));
        }

        // ================================================================
        // PlanCommittedTechUnlocksForPatch (pure)
        // ================================================================

        [Fact]
        public void Plan_ClockPastTheRow_IncludesNode()
        {
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, null, T + 0.5, false, out string skip);

            Assert.Null(skip);
            Assert.Equal(new[] { NodeAtT }, ids);
        }

        [Fact]
        public void Plan_ClockExactlyAtTheRow_IncludesNode()
        {
            // The research block lifts when row.UT > now turns false; the unlock matches it.
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, null, T, false, out _);

            Assert.Equal(new[] { NodeAtT }, ids);
        }

        [Fact]
        public void Plan_ClockBeforeTheRow_ExcludesFutureNode()
        {
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, null, T - 0.01, false, out string skip);

            Assert.Null(skip);
            Assert.Empty(ids);
        }

        [Fact]
        public void Plan_TechCutoffSupplied_SkipsSoPatchTechTreeOwnsBothDirections()
        {
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, T + 1.0, T + 1.0, false, out string skip);

            Assert.Null(ids);
            Assert.Equal(KspStatePatcher.CommittedTechUnlockSkipTechCutoff, skip);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void Plan_ClockNotReady_Skips(double liveUT)
        {
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, null, liveUT, false, out string skip);

            Assert.Null(ids);
            Assert.Equal(KspStatePatcher.CommittedTechUnlockSkipClockNotReady, skip);
        }

        [Fact]
        public void Plan_RewindUtAdjustmentPending_Skips()
        {
            // The clock is about to move back; reading it now would unlock a node the rewind
            // just re-locked.
            var ids = KspStatePatcher.PlanCommittedTechUnlocksForPatch(
                new List<GameAction> { TechRow(T, NodeAtT) }, null, T + 500.0, true, out string skip);

            Assert.Null(ids);
            Assert.Equal(KspStatePatcher.CommittedTechUnlockSkipRewindPending, skip);
        }

        [Fact]
        public void Build_UnaffordableRow_UnlocksNothing()
        {
            var ids = KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(
                new List<GameAction> { TechRow(T, NodeAtT, affordable: false) }, T + 1.0);

            Assert.Empty(ids);
        }

        [Fact]
        public void Build_KeepsOnlyScienceSpendingNodeRows_SortedAndDeduplicated()
        {
            var actions = new List<GameAction>
            {
                TechRow(T, NodeAtT),
                TechRow(T - 10.0, LaterNode),
                TechRow(T - 5.0, NodeAtT),
                TechRow(T - 1.0, null),
                new GameAction { UT = T - 2.0, Type = GameActionType.ScienceEarning, NodeId = "basicScience" },
                null,
            };

            var ids = KspStatePatcher.BuildCommittedTechUnlockIdsForPatch(actions, T);

            Assert.Equal(new[] { LaterNode, NodeAtT }, ids);
        }
    }
}
