using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// RECOVERY-PAYOUT-MARK-LOST-ON-OPTIMIZER-SPLIT (HC-2, 2026-10-15): an in-flight Recover
    /// stamps the recording Recovered and marks its stock payout as still to come
    /// (<c>InFlightRecoveryRequest.awaitingStockPayout</c>). The commit-time optimizer split
    /// then moved the Recovered terminal onto a second half with a new id, the payout
    /// pairing looked the mark up by that new id and missed, and the last-two-points
    /// heuristic booked an in-flight milestone reward as a second recovery payout. The
    /// mark must travel with the terminal through both the split and the merge pass.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryPayoutMarkAcrossOptimizerTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RecoveryPayoutMarkAcrossOptimizerTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            LedgerOrchestrator.ResetForTesting();
            InFlightRecoveryRequest.ResetForTesting();
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateRecorder.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // Exo -> atmo recording recovered in flight. The funds column jumps by 960 on the
        // last sample (a milestone reward paid during descent), which is exactly what the
        // last-two-points heuristic misreads as a recovery payout.
        private static Recording MakeRecoveredSplittableRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Settings Probe",
                VesselPersistentId = 42,
                TerminalStateValue = TerminalState.Recovered,
                GhostVisualSnapshot = new ConfigNode("VESSEL"),
                RecordingFormatVersion = 0
            };
            rec.Points.Add(new TrajectoryPoint { ut = 17000, altitude = 80000, bodyName = "Kerbin", funds = 5000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17029, altitude = 40000, bodyName = "Kerbin", funds = 5000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17030, altitude = 30000, bodyName = "Kerbin", funds = 5000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17059, altitude = 200, bodyName = "Kerbin", funds = 5000 });
            rec.Points.Add(new TrajectoryPoint { ut = 17060, altitude = 100, bodyName = "Kerbin", funds = 5960 });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.ExoBallistic,
                startUT = 17000, endUT = 17030,
                frames = new List<TrajectoryPoint>()
            });
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                startUT = 17030, endUT = 17060,
                frames = new List<TrajectoryPoint>()
            });
            return rec;
        }

        private static Recording MakeMergeableChainSegment(string id, int chainIndex,
            double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Chain",
                ChainId = "chain1",
                ChainIndex = chainIndex,
                ChainBranch = 0,
                SegmentPhase = "exo",
                SegmentBodyName = "Mun",
                LoopPlayback = false,
                PlaybackEnabled = true,
                Hidden = false,
                LoopIntervalSeconds = LoopTiming.UntouchedLoopIntervalSentinel,
                LoopAnchorVesselId = 0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT, altitude = 50000, bodyName = "Mun" });
            rec.Points.Add(new TrajectoryPoint { ut = endUT, altitude = 50000, bodyName = "Mun" });
            return rec;
        }

        private static List<GameAction> RecoveryRows(List<GameAction> actions) =>
            actions.Where(a => a.Type == GameActionType.FundsEarning
                && a.FundsSource == FundsEarningSource.Recovery).ToList();

        private static Recording SplitSecondHalf(string originalId)
        {
            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);
            return RecordingStore.CommittedRecordings.Single(r => r.RecordingId != originalId);
        }

        [Fact]
        public void Move_MovesTheMarkOnce_AndLogs()
        {
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("a");

            Assert.True(InFlightRecoveryRequest.MoveAwaitingStockPayout("a", "b", "test"));

            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("a"));
            Assert.True(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("b"));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("payout mark moved from 'a' to 'b'") && l.Contains("context=test"));
            Assert.False(InFlightRecoveryRequest.MoveAwaitingStockPayout("a", "c", "test"));
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("c"));
        }

        [Fact]
        public void Move_IgnoresUnmarkedSameAndEmptyIds()
        {
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("a");

            Assert.False(InFlightRecoveryRequest.MoveAwaitingStockPayout("x", "b", "test"));
            Assert.False(InFlightRecoveryRequest.MoveAwaitingStockPayout("a", "a", "test"));
            Assert.False(InFlightRecoveryRequest.MoveAwaitingStockPayout("a", null, "test"));
            Assert.False(InFlightRecoveryRequest.MoveAwaitingStockPayout(null, "b", "test"));

            Assert.True(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("a"));
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("b"));
        }

        [Fact]
        public void OptimizerSplit_MovesTheMarkToTheHalfThatHoldsRecovered()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecoveredSplittableRecording("rec-recovered"));
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("rec-recovered");

            RecordingStore.RunOptimizationPass();

            Recording second = SplitSecondHalf("rec-recovered");
            Assert.Equal(TerminalState.Recovered, second.TerminalStateValue);
            // MUTATION NOTE: deleting the MoveAwaitingStockPayout call in
            // RunOptimizationSplitPass leaves the mark on the first half's id.
            Assert.True(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting(second.RecordingId));
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("rec-recovered"));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("payout mark moved from 'rec-recovered'")
                && l.Contains("context=optimization-split"));
        }

        [Fact]
        public void OptimizerSplit_ThenCostActions_BooksNoHeuristicRecoveryRow()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecoveredSplittableRecording("rec-recovered"));
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("rec-recovered");

            RecordingStore.RunOptimizationPass();
            Recording second = SplitSecondHalf("rec-recovered");

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                second.RecordingId, second.StartUT, second.EndUT);

            Assert.Empty(RecoveryRows(actions));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("recovered in flight") && l.Contains(second.RecordingId));
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting(second.RecordingId));
        }

        [Fact]
        public void OptimizerSplit_WithoutTheMark_FixtureReachesTheHeuristic()
        {
            // Control: proves the fixture's 960 jump is what the heuristic books, so the
            // empty result above is the mark's doing and not a fixture that never pairs.
            RecordingStore.AddRecordingWithTreeForTesting(MakeRecoveredSplittableRecording("rec-recovered"));

            RecordingStore.RunOptimizationPass();
            Recording second = SplitSecondHalf("rec-recovered");

            var actions = LedgerOrchestrator.CreateVesselCostActions(
                second.RecordingId, second.StartUT, second.EndUT);

            GameAction row = Assert.Single(RecoveryRows(actions));
            Assert.Equal(960.0, (double)row.FundsAwarded, 1);
        }

        [Fact]
        public void OptimizerMerge_MovesTheMarkToTheSurvivingTarget()
        {
            RecordingStore.AddRecordingWithTreeForTesting(MakeMergeableChainSegment("seg-a", 0, 17000, 17030));
            RecordingStore.AddRecordingWithTreeForTesting(MakeMergeableChainSegment("seg-b", 1, 17030, 17060));
            Recording last = MakeMergeableChainSegment("seg-c", 2, 17060, 17090);
            last.TerminalStateValue = TerminalState.Recovered;
            RecordingStore.AddRecordingWithTreeForTesting(last);
            InFlightRecoveryRequest.MarkAwaitingStockPayoutForTesting("seg-c");

            RecordingStore.RunOptimizationPass();

            Recording merged = Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Equal("seg-a", merged.RecordingId);
            Assert.Equal(TerminalState.Recovered, merged.TerminalStateValue);
            Assert.True(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("seg-a"));
            Assert.False(InFlightRecoveryRequest.IsAwaitingStockPayoutForTesting("seg-c"));
            Assert.Contains(logLines, l => l.Contains("[Recovery]")
                && l.Contains("payout mark moved from 'seg-c' to 'seg-a'")
                && l.Contains("context=optimization-merge"));
        }
    }
}
