using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// OPTIMIZER-SPLIT-RETAG-COMMIT-WALK-TRANSIENT-CLAMP. Both tree commit paths
    /// (<see cref="MergeDialog.MergeCommit"/> and the in-flight CommitTreeFlight) run the
    /// optimizer pass before <see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted"/>,
    /// and the split moves a milestone captured past the cut to the second half. The
    /// notify used to walk the ledger once per recording, so the FIRST half's walk ran
    /// before the second half's rows existed: one award short of a live pool that already
    /// held it, which the funds drawdown guard clamps (HC-2's forbidden token). The tree
    /// now files every recording's rows and walks once.
    /// <para>The seam is <see cref="LedgerOrchestrator.OnTimelineDataChanged"/>, raised
    /// once at the end of every recalculation: each raise records what that walk saw.</para>
    /// </summary>
    [Collection("Sequential")]
    public class OptimizerSplitCommitSingleWalkTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;
        private readonly List<WalkView> walks = new List<WalkView>();

        private struct WalkView
        {
            public int Milestones;
            public HashSet<string> MilestoneRecordingIds;
        }

        public OptimizerSplitCommitSingleWalkTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            GameStateRecorder.ResetForTesting();
            Ledger.ResetForTesting();
            MilestoneStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            KspStatePatcher.ResetForTesting();
        }

        private const string TreeId = "tree_split_walk";
        private const double CutUT = 20.0;

        private void CountWalks()
        {
            LedgerOrchestrator.OnTimelineDataChanged += () =>
            {
                var milestoneRows = Ledger.Actions
                    .Where(a => a != null && a.Type == GameActionType.MilestoneAchievement)
                    .ToList();
                walks.Add(new WalkView
                {
                    Milestones = milestoneRows.Count,
                    MilestoneRecordingIds = new HashSet<string>(
                        milestoneRows.Select(a => a.RecordingId ?? ""), StringComparer.Ordinal),
                });
            };
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 50000.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        private static TrackSection Section(SegmentEnvironment env, double[] uts)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = uts[0], endUT = uts[uts.Length - 1], sampleRateHz = 1f,
                minAltitude = float.NaN, maxAltitude = float.NaN,
                frames = uts.Select(PointAt).ToList(),
            };
        }

        /// <summary>
        /// An uncrewed flight, Atmospheric 8-20 then (when <paramref name="splittable"/>)
        /// ExoBallistic 20-53, which the optimizer splits once at UT 20. With
        /// <paramref name="splittable"/> false both sections are Atmospheric: no boundary.
        /// </summary>
        private static Recording BuildFlight(string id, bool splittable)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Flea",
                TreeId = TreeId,
                VesselPersistentId = 42,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
                RecordingFormatVersion = 0,
            };
            double[] atmoUTs = { 8.0, 14.0, 20.0 };
            double[] laterUTs = { 20.0, 27.0, 34.0, 40.0, 53.0 };
            foreach (double ut in atmoUTs) rec.Points.Add(PointAt(ut));
            for (int i = 1; i < laterUTs.Length; i++) rec.Points.Add(PointAt(laterUTs[i]));
            rec.TrackSections.Add(Section(SegmentEnvironment.Atmospheric, atmoUTs));
            rec.TrackSections.Add(Section(
                splittable ? SegmentEnvironment.ExoBallistic : SegmentEnvironment.Atmospheric,
                laterUTs));
            return rec;
        }

        private static RecordingTree MakeTree(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static RecordingTree InstallCommittedTree(Recording rec)
        {
            var tree = MakeTree(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);
            return tree;
        }

        private static void AddMilestone(string key, double ut, string recordingId)
        {
            var e = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.MilestoneAchieved,
                key = key,
                detail = GameStateRecorder.BuildMilestoneDetail(960.0, 1.0f, 0.0),
                recordingId = recordingId ?? "",
            };
            GameStateStore.AddEvent(ref e);
        }

        // The in-flight commit's order (CommitTreeFlight: CommitTree, the optimizer pass,
        // then NotifyLedgerTreeCommitted) on a fresh flight with one milestone on each side
        // of the cut.
        // MUTATION NOTE: restoring the per-recording RecalculateAndPatch inside
        // NotifyLedgerTreeCommitted's loop reds this: two walks, the first seeing only the
        // first half's milestone.
        [Fact]
        public void SplitThenTreeCommit_WalksOnceAfterBothHalvesAreFiled()
        {
            var rec = BuildFlight("rec_walk", splittable: true);
            var tree = InstallCommittedTree(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_walk");
            AddMilestone("Kerbin/Science", 40.0, "rec_walk");

            RecordingStore.RunOptimizationPass();
            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            string seg1 = list[0].RecordingId;
            string seg2 = list[1].RecordingId;
            Assert.Equal(2, tree.Recordings.Count);

            CountWalks();
            logLines.Clear();
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.True(walks.Count == 1,
                $"expected one walk for the whole tree, saw {walks.Count} " +
                $"(milestones per walk: {string.Join(",", walks.Select(w => w.Milestones))})");
            Assert.Equal(2, walks[0].Milestones);
            Assert.Contains(seg1, walks[0].MilestoneRecordingIds);
            Assert.Contains(seg2, walks[0].MilestoneRecordingIds);

            // Both halves' per-recording lines still print, then the batch summary.
            Assert.Contains(logLines, l => l.Contains($"Committed recording '{seg1}'"));
            Assert.Contains(logLines, l => l.Contains($"Committed recording '{seg2}'"));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("NotifyLedgerTreeCommitted: filed rows for 2 recording(s)")
                && l.Contains("before one ledger walk"));
        }

        // The scene-exit / merge-dialog commit (and the auto-merge, which calls it):
        // every walk MergeCommit runs must see both halves' milestones.
        [Fact]
        public void SplitThenMergeCommit_NoWalkSeesTheFirstHalfAlone()
        {
            var rec = BuildFlight("rec_merge", splittable: true);
            var tree = MakeTree(rec);
            RecordingStore.StashPendingTree(tree);
            AddMilestone("RecordsSpeed", 12.0, "rec_merge");
            AddMilestone("Kerbin/Science", 40.0, "rec_merge");

            CountWalks();
            MergeDialog.MergeCommit(
                tree,
                new Dictionary<string, bool> { { "rec_merge", false } },
                spawnCount: 0,
                refreshQuicksaveAfterCommit: false);

            var list = RecordingStore.CommittedRecordings;
            Assert.Equal(2, list.Count);
            Assert.True(walks.Count >= 1, "MergeCommit ran no ledger walk");
            for (int i = 0; i < walks.Count; i++)
            {
                Assert.True(walks[i].Milestones == 2,
                    $"walk {i} saw {walks[i].Milestones} milestone row(s); every walk of the " +
                    "commit must see both halves");
            }
            Assert.Contains(logLines, l => l.Contains("RetagEventsForSplitSecondHalf")
                && l.Contains("retagged=1"));
        }

        // Mirror: a commit the optimizer does not split still walks exactly once.
        [Fact]
        public void NoSplitTreeCommit_StillWalksOnce()
        {
            var rec = BuildFlight("rec_whole", splittable: false);
            var tree = InstallCommittedTree(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_whole");
            AddMilestone("Kerbin/Science", 40.0, "rec_whole");

            RecordingStore.RunOptimizationPass();
            Assert.Single(RecordingStore.CommittedRecordings);

            CountWalks();
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.Single(walks);
            Assert.Equal(2, walks[0].Milestones);
        }

        // Mirror: the standalone per-recording commit (FallbackCommitSplitRecorder) keeps
        // its own walk.
        [Fact]
        public void StandaloneRecordingCommit_StillWalksOnce()
        {
            var rec = BuildFlight("rec_alone", splittable: false);
            rec.TreeId = null;
            RecordingStore.AddCommittedInternal(rec);
            AddMilestone("RecordsSpeed", 12.0, "rec_alone");

            CountWalks();
            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                "rec_alone", rec.StartUT, rec.EndUT, null, ref scienceAdded);

            Assert.Single(walks);
            Assert.Equal(1, walks[0].Milestones);
        }

        // An empty tree files nothing and walks nothing, as before.
        [Fact]
        public void EmptyTreeCommit_DoesNotWalk()
        {
            var tree = new RecordingTree { Id = "tree_empty", TreeName = "tree_empty" };
            CountWalks();
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            Assert.Empty(walks);
        }
    }
}
