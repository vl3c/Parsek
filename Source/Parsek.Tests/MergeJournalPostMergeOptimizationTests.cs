using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q6 (2026-11-18, "Yes, right after merge"): one committed-list optimizer
    /// pass runs as soon as a Re-Fly merge is done (journal and marker cleared), on the live
    /// merge and on the load-time finisher, so the new attempt is split into its rows at once
    /// instead of on the next commit or cold load.
    /// </summary>
    [Collection("Sequential")]
    public class MergeJournalPostMergeOptimizationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly List<string> durableSaveCheckpoints = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public MergeJournalPostMergeOptimizationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            MergeJournalOrchestrator.ResetTestOverrides();
            MergeJournalOrchestrator.DurableSaveForTesting = label => durableSaveCheckpoints.Add(label);
        }

        public void Dispose()
        {
            MergeJournalOrchestrator.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
        }

        private static ParsekScenario InstallFixture(out Recording provisional)
        {
            var origin = new Recording
            {
                RecordingId = "rec_origin", VesselName = "o", TreeId = "tree_1",
                MergeState = MergeState.Immutable, TerminalStateValue = TerminalState.Destroyed,
            };
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            provisional = new Recording
            {
                RecordingId = "rec_prov", VesselName = "rec_prov", TreeId = "tree_1",
                MergeState = MergeState.NotCommitted, TerminalStateValue = TerminalState.Landed,
            };
            provisional.Points.Add(new TrajectoryPoint { ut = 40.0 });
            provisional.Points.Add(new TrajectoryPoint { ut = 60.0 });

            var tree = new RecordingTree
            {
                Id = "tree_1", TreeName = "Test_tree_1", BranchPoints = new List<BranchPoint>(),
            };
            foreach (var rec in new[] { origin, provisional })
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, "tree_1");
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == "tree_1") trees.RemoveAt(i);
            trees.Add(tree);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = "sess_q6",
                    TreeId = "tree_1",
                    ActiveReFlyRecordingId = "rec_prov",
                    OriginChildRecordingId = "rec_origin",
                    RewindPointId = "rp_1",
                    InvokedUT = 0.0,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        [Fact]
        public void RunMerge_RunsTheOptimizerPassAfterTheJournalAndMarkerClear()
        {
            var scenario = InstallFixture(out var provisional);
            var marker = scenario.ActiveReFlySessionMarker;
            var seen = new List<string>();
            MergeJournalOrchestrator.PostMergeOptimizationPassForTesting = route =>
            {
                Assert.Null(scenario.ActiveMergeJournal);
                Assert.Null(scenario.ActiveReFlySessionMarker);
                seen.Add(route + "@" + string.Join(",", durableSaveCheckpoints));
            };

            Assert.True(MergeJournalOrchestrator.RunMerge(marker, provisional));

            // Once, before Durable Save #3 so that save holds the split.
            Assert.Equal(new[] { "merge@begin,split,durable1,durable2" }, seen.ToArray());
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("Post-merge optimization pass ran sess=sess_q6 route=merge"));
        }

        [Fact]
        public void Finisher_RunsTheOptimizerPassAfterCompleting()
        {
            var scenario = InstallFixture(out _);
            scenario.ActiveMergeJournal = new MergeJournal
            {
                JournalId = "mj_q6", SessionId = "sess_q6", TreeId = "tree_1",
                Phase = MergeJournal.Phases.Split,
                StartedRealTime = "2026-11-18T10:00:00.0000000Z",
            };
            var seen = new List<string>();
            MergeJournalOrchestrator.PostMergeOptimizationPassForTesting = route =>
            {
                Assert.Null(scenario.ActiveMergeJournal);
                Assert.Null(scenario.ActiveReFlySessionMarker);
                seen.Add(route);
            };

            Assert.True(MergeJournalOrchestrator.RunFinisher());

            Assert.Equal(new[] { "finisher" }, seen.ToArray());
        }

        [Fact]
        public void PostMergePass_Throws_MergeStands_Logged()
        {
            var scenario = InstallFixture(out var provisional);
            MergeJournalOrchestrator.PostMergeOptimizationPassForTesting =
                route => throw new InvalidOperationException("synthetic optimizer failure");

            Assert.True(MergeJournalOrchestrator.RunMerge(
                scenario.ActiveReFlySessionMarker, provisional));

            Assert.Null(scenario.ActiveMergeJournal);
            Assert.Contains(logLines, l => l.Contains("[MergeJournal]")
                && l.Contains("Post-merge optimization pass threw InvalidOperationException")
                && l.Contains("merge stands"));
        }
    }
}
