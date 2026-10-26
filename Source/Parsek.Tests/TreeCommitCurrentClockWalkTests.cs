using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// TREE-COMMIT-WALKS-PAST-THE-CLOCK. The commit walks
    /// (<see cref="LedgerOrchestrator.NotifyLedgerTreeCommitted"/>, used by the merge dialog,
    /// the outside-flight auto-commit, the OnSave safety net and the in-flight commit, and
    /// <see cref="LedgerOrchestrator.OnRecordingCommitted"/>) used to walk the whole ledger,
    /// so with committed rows ahead of the clock (after a rewind) the patch wrote the
    /// end-of-timeline career into KSP. They now cut at the live clock when committed rows
    /// lie ahead of it, keep the full walk when none do, and keep the full walk while OnLoad
    /// runs (the live clock is the pre-load one there).
    /// </summary>
    [Collection("Sequential")]
    public class TreeCommitCurrentClockWalkTests : IDisposable
    {
        private const string Tag = "[LedgerOrchestrator]";
        private const string TreeId = "tree_commit_clock";
        private const string CommitRecId = "rec_commit_clock";
        private const string FutureMilestoneId = "FutureOrbit";
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public TreeCommitCurrentClockWalkTests()
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
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
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
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RecalculationEngine.ClearModules();
            KspStatePatcher.ResetForTesting();
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut, altitude = 100.0, bodyName = "Kerbin",
                rotation = Quaternion.identity, velocity = Vector3.zero,
            };
        }

        /// <summary>A committed flight 100-150 with one milestone (960 funds, 1 rep) at 120.</summary>
        private static RecordingTree InstallCommittedFlight()
        {
            var rec = new Recording
            {
                RecordingId = CommitRecId,
                VesselName = "Flea",
                TreeId = TreeId,
                VesselPersistentId = 42,
                MergeState = MergeState.Immutable,
                TerminalStateValue = TerminalState.Landed,
                RecordingFormatVersion = 0,
            };
            rec.Points.Add(PointAt(100.0));
            rec.Points.Add(PointAt(150.0));
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = TreeId,
                RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.AddOrReplaceRecording(rec);
            RecordingStore.AddCommittedInternal(rec);
            RecordingStore.AddCommittedTreeForTesting(tree);

            var e = new GameStateEvent
            {
                ut = 120.0,
                eventType = GameStateEventType.MilestoneAchieved,
                key = "RecordsSpeed",
                detail = GameStateRecorder.BuildMilestoneDetail(960.0, 1.0f, 0.0),
                recordingId = CommitRecId,
            };
            GameStateStore.AddEvent(ref e);
            return tree;
        }

        /// <summary>The career before the commit: a 10000 funds seed and a committed
        /// milestone (1000 funds, 2 rep) of another flight at <paramref name="otherUT"/>.</summary>
        private static GameAction SeedCareer(double otherUT)
        {
            Ledger.AddAction(new GameAction
            {
                UT = 0.0,
                Type = GameActionType.FundsInitial,
                InitialFunds = 10000f,
            });
            var other = new GameAction
            {
                UT = otherUT,
                Type = GameActionType.MilestoneAchievement,
                MilestoneId = FutureMilestoneId,
                MilestoneFundsAwarded = 1000f,
                MilestoneRepAwarded = 2f,
                RecordingId = "rec_other_flight",
            };
            Ledger.AddAction(other);
            return other;
        }

        private static GameAction FiledMilestone()
        {
            return Ledger.Actions.Single(a =>
                a != null
                && a.Type == GameActionType.MilestoneAchievement
                && a.RecordingId == CommitRecId);
        }

        private bool HasCurrentUtLine(string reason, string cutoff)
        {
            return logLines.Any(l => l.Contains(Tag)
                && l.Contains("Current-UT ledger recalculation: reason=" + reason)
                && l.Contains("cutoffUT=" + cutoff));
        }

        private bool HasFullReplayLine(string reason)
        {
            return logLines.Any(l => l.Contains(Tag)
                && l.Contains("Current-timeline recalc: full replay (cutoffUT=null)")
                && l.Contains("reason=" + reason));
        }

        // MUTATION NOTE: putting the cutoff-less RecalculateAndPatch() back in
        // NotifyLedgerTreeCommitted reds this: the funds read 11960 (the other flight's
        // future 1000 patched in at UT 200) and no current-UT line is written.
        [Fact]
        public void TreeCommit_WithCommittedRowsAheadOfTheClock_WalksToTheClock()
        {
            GameAction future = SeedCareer(500.0);
            var tree = InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            // The career at now: seed + this flight's milestone; the other flight's award
            // at UT 500 is still ahead of the clock.
            Assert.Equal(10960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.True(HasCurrentUtLine(LedgerOrchestrator.TreeCommitRecalcReason, "200"));
            Assert.False(HasFullReplayLine(LedgerOrchestrator.TreeCommitRecalcReason));

            // The committed future stays committed.
            Assert.Contains(future, Ledger.Actions);

            // The post-walk reconciliation reads derived fields: the filed row was walked,
            // and the row past the cutoff carries the projection walk's value (both are
            // reset to 0 at a walk's start).
            GameAction filed = FiledMilestone();
            Assert.True(filed.Effective);
            Assert.True(filed.EffectiveRep > 0f);
            Assert.True(future.Effective);
            Assert.True(future.EffectiveRep > 0f);
        }

        // The no-future-rows case keeps the walk the commit always ran.
        [Fact]
        public void TreeCommit_WithNoRowsAheadOfTheClock_KeepsTheFullWalk()
        {
            GameAction earlier = SeedCareer(80.0);
            var tree = InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.Equal(11960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.True(HasFullReplayLine(LedgerOrchestrator.TreeCommitRecalcReason));
            Assert.False(logLines.Any(l => l.Contains("Current-UT ledger recalculation:")));
            Assert.True(FiledMilestone().EffectiveRep > 0f);
            Assert.True(earlier.EffectiveRep > 0f);

            // Identical to a plain full walk over the same ledger.
            double afterCommit = LedgerOrchestrator.Funds.GetRunningBalance();
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.Equal(afterCommit, LedgerOrchestrator.Funds.GetRunningBalance(), 5);
        }

        // OnLoad: the live clock is the pre-load one, so the commit keeps the full walk and
        // the load's own recalculation judges the clock right after.
        [Fact]
        public void TreeCommit_DuringOnLoad_KeepsTheFullWalk()
        {
            SeedCareer(500.0);
            var tree = InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;
            ParsekScenario.SetOnLoadInProgressForTesting(true);

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.Equal(11960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Contains(logLines, l => l.Contains(Tag)
                && l.Contains("Commit ledger walk: full replay (cutoffUT=null), live clock not trusted")
                && l.Contains("reason=" + LedgerOrchestrator.TreeCommitRecalcReason)
                && l.Contains("onLoadInProgress=True"));
            Assert.False(logLines.Any(l => l.Contains("Current-UT ledger recalculation:")));
        }

        [Fact]
        public void TreeCommit_WhileRewindUtAdjustmentPending_KeepsTheFullWalk()
        {
            SeedCareer(500.0);
            var tree = InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;
            RecordingStore.RewindUTAdjustmentPending = true;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);

            Assert.Equal(11960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.Contains(logLines, l => l.Contains(Tag)
                && l.Contains("Commit ledger walk: full replay (cutoffUT=null), live clock not trusted")
                && l.Contains("rewindUTAdjustmentPending=True"));
        }

        // MUTATION NOTE: putting the cutoff-less RecalculateAndPatch() back in
        // OnRecordingCommitted reds this the same way.
        [Fact]
        public void RecordingCommit_WithCommittedRowsAheadOfTheClock_WalksToTheClock()
        {
            GameAction future = SeedCareer(500.0);
            InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;

            bool scienceAdded = false;
            LedgerOrchestrator.OnRecordingCommitted(
                CommitRecId, 100.0, 150.0, null, ref scienceAdded);

            Assert.Equal(10960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
            Assert.True(HasCurrentUtLine(LedgerOrchestrator.RecordingCommitRecalcReason, "200"));
            Assert.Contains(future, Ledger.Actions);
            Assert.True(FiledMilestone().EffectiveRep > 0f);
        }

        // A later clock crossing the committed row reaches it: the cutoff walk deferred
        // the award, it did not drop it.
        [Fact]
        public void TreeCommit_CutoffWalk_LaterWalkPastTheRowCreditsIt()
        {
            SeedCareer(500.0);
            var tree = InstallCommittedFlight();
            ParsekScenario.CurrentTimelineUTProviderForTesting = () => 200.0;

            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            Assert.Equal(10960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineIfFutureActions(600.0, "test-later");
            Assert.Equal(11960.0, LedgerOrchestrator.Funds.GetRunningBalance(), 1);
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        public void ShouldCommitWalkUseLiveClock_TrustsTheClockOnlyOutsideLoadAndRewindAdjust(
            bool onLoad, bool rewindAdjust, bool expected)
        {
            Assert.Equal(expected,
                LedgerOrchestrator.ShouldCommitWalkUseLiveClock(onLoad, rewindAdjust));
        }
    }
}
