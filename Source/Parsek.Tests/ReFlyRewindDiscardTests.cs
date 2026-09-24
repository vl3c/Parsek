using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A plain rewind (Rewind-to-Launch, Warp-to-time's go-back) taken while a Re-Fly session
    /// is live ends that session at once, the design section 6.8 Space Center way (operator
    /// ruling 2026-09-24, todo REFLY-DESTROYED-THEN-RTL-MID-SESSION). Drives the real
    /// <see cref="MergeDialog.TryDiscardLiveReFlySessionForRewind"/> the rewind entry points
    /// call, then the following ordinary load's <see cref="LoadTimeSweep"/>.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyRewindDiscardTests : IDisposable
    {
        private const string TreeId = "tree-rtl-refly";
        private const string SessionId = "sess-rtl-refly";
        private const string OriginRpId = "rp-rtl-origin";
        private const string SessionRpId = "rp-rtl-session";
        private const string OriginId = "rec-rtl-origin";
        private const string ProvisionalId = "rec-rtl-provisional";

        private readonly List<string> logLines = new List<string>();

        public ReFlyRewindDiscardTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.SuppressLogging = true;
            GameStateStore.SuppressLogging = true;
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SkipSidecarCurrencyCheckForTesting = true;
            LedgerOrchestrator.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateRecorder.PendingScienceSubjects.Clear();
            ParsekScenario.ResetInstanceForTesting();
            RecordingStore.SaveGameForTesting = null;
            MergeJournalOrchestrator.ResetTestOverrides();
            RewindPointReaper.ResetTestOverrides();
            TreeDiscardPurge.ResetTestOverrides();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = false;
        }

        // ---------- Fixture ---------------------------------------------

        private sealed class LiveSession
        {
            public ParsekScenario Scenario;
            public RecordingTree Tree;
            public Recording Origin;
            public Recording Provisional;
            public RewindPoint OriginRp;
            public RewindPoint SessionRp;
        }

        private static Recording MakeRecording(string id, double startUT, double endUT)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Vessel-" + id,
                TreeId = TreeId,
            };
            rec.Points.Add(new TrajectoryPoint { ut = startUT });
            rec.Points.Add(new TrajectoryPoint { ut = endUT });
            return rec;
        }

        /// <summary>
        /// The in-memory shape of a live Re-Fly in FLIGHT: the committed tree was detached
        /// (<c>RemoveCommittedTreeById</c>) and lives on as the flight scene's active tree, the
        /// provisional sits in the committed list as NotCommitted, the origin RP and one RP the
        /// session itself authored (staging during the re-fly) are both session-provisional.
        /// </summary>
        private static LiveSession BuildLiveSession(bool treeInPendingSlot = false)
        {
            var origin = MakeRecording(OriginId, 100.0, 200.0);
            origin.MergeState = MergeState.CommittedProvisional;
            origin.TerminalStateValue = TerminalState.Destroyed;
            origin.ParentBranchPointId = "bp-rtl-origin";

            var provisional = MakeRecording(ProvisionalId, 150.0, 260.0);
            provisional.MergeState = MergeState.NotCommitted;
            provisional.CreatingSessionId = SessionId;
            provisional.ProvisionalForRpId = OriginRpId;
            provisional.SupersedeTargetId = OriginId;
            RecordingStore.AddProvisional(provisional);

            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Tree-" + TreeId,
                RootRecordingId = OriginId,
                ActiveRecordingId = ProvisionalId,
            };
            tree.Recordings[OriginId] = origin;
            tree.Recordings[ProvisionalId] = provisional;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-rtl-origin",
                Type = BranchPointType.JointBreak,
                ChildRecordingIds = new List<string> { OriginId, ProvisionalId },
            });
            if (treeInPendingSlot)
                RecordingStore.StashPendingTree(tree);

            var originRp = new RewindPoint
            {
                RewindPointId = OriginRpId,
                BranchPointId = "bp-rtl-origin",
                UT = 150.0,
                QuicksaveFilename = OriginRpId + ".sfs",
                SessionProvisional = true,
                CreatingSessionId = SessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = OriginId, Controllable = true },
                },
            };
            var sessionRp = new RewindPoint
            {
                RewindPointId = SessionRpId,
                BranchPointId = "bp-rtl-session",
                UT = 180.0,
                QuicksaveFilename = SessionRpId + ".sfs",
                SessionProvisional = true,
                CreatingSessionId = SessionId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = ProvisionalId, Controllable = true },
                },
            };

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { originRp, sessionRp },
                ActiveReFlySessionMarker = new ReFlySessionMarker
                {
                    SessionId = SessionId,
                    TreeId = TreeId,
                    ActiveReFlyRecordingId = ProvisionalId,
                    OriginChildRecordingId = OriginId,
                    SupersedeTargetId = OriginId,
                    RewindPointId = OriginRpId,
                    InvokedUT = 150.0,
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            return new LiveSession
            {
                Scenario = scenario,
                Tree = tree,
                Origin = origin,
                Provisional = provisional,
                OriginRp = originRp,
                SessionRp = sessionRp,
            };
        }

        private static bool CommittedHas(string recordingId)
        {
            foreach (var rec in RecordingStore.CommittedRecordings)
                if (rec != null && rec.RecordingId == recordingId)
                    return true;
            return false;
        }

        private static bool CommittedTreeHas(string treeId)
        {
            foreach (var tree in RecordingStore.CommittedTrees)
                if (tree != null && tree.Id == treeId)
                    return true;
            return false;
        }

        // ---------- Live session ends at the rewind ---------------------

        [Fact]
        public void LiveSessionOnLiveTree_RewindEndsItLikeTheSpaceCenterEnd()
        {
            var s = BuildLiveSession();

            bool ended = MergeDialog.TryDiscardLiveReFlySessionForRewind(
                s.Tree, "Rewind", out string droppedLiveTreeId);

            Assert.True(ended);
            // Marker cleared.
            Assert.Null(s.Scenario.ActiveReFlySessionMarker);
            // Provisional discarded from the store and from the tree.
            Assert.False(CommittedHas(ProvisionalId));
            Assert.False(s.Tree.Recordings.ContainsKey(ProvisionalId));
            // Session-provisional RP purged; the origin RP survives, promoted.
            Assert.DoesNotContain(s.Scenario.RewindPoints, rp => rp.RewindPointId == SessionRpId);
            Assert.Contains(s.Scenario.RewindPoints, rp => rp.RewindPointId == OriginRpId);
            Assert.False(s.OriginRp.SessionProvisional);
            Assert.Null(s.OriginRp.CreatingSessionId);
            // The detached committed tree is back, sanitized, so the rewind replays it.
            Assert.True(CommittedTreeHas(TreeId));
            Assert.True(CommittedHas(OriginId));
            // The origin slot is still offered in Unfinished Flights.
            Assert.True(EffectiveState.IsUnfinishedFlight(s.Origin));
            // The scene exit drops the live reference without a stash, and the
            // SceneExitInterceptor prefix bypasses on the same flag (no Re-Fly dialog).
            Assert.True(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.Equal(TreeId, droppedLiveTreeId);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFlyForRewind")
                && l.Contains("sess=" + SessionId)
                && l.Contains("rewind=Rewind")
                && l.Contains("discardedSessionRps=1")
                && l.Contains("liveTreeDropped=True"));
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("Origin RP promoted to persistent rp=" + OriginRpId)
                && l.Contains("reason=discardReFlyForRewind"));
        }

        [Fact]
        public void LiveSessionEndedByRewind_LeavesNoZombieForTheNextLoadsSweep()
        {
            var s = BuildLiveSession();
            MergeDialog.TryDiscardLiveReFlySessionForRewind(s.Tree, "Rewind", out _);
            logLines.Clear();

            LoadTimeSweep.Run();

            Assert.Contains(logLines, l => l.Contains("[LoadSweep]")
                && l.Contains("discarded=0 ")
                && l.Contains("discardedRps=0"));
            Assert.Contains(s.Scenario.RewindPoints, rp => rp.RewindPointId == OriginRpId);
            Assert.True(CommittedHas(OriginId));
        }

        [Fact]
        public void SessionTreeInPendingSlot_OtherLiveTree_EndsSessionWithoutDroppingTheLiveTree()
        {
            var s = BuildLiveSession(treeInPendingSlot: true);
            var otherLive = new RecordingTree { Id = "tree-unrelated-live", TreeName = "Unrelated" };

            bool ended = MergeDialog.TryDiscardLiveReFlySessionForRewind(
                otherLive, "Warp-to-game-start", out string droppedLiveTreeId);

            Assert.True(ended);
            Assert.Null(s.Scenario.ActiveReFlySessionMarker);
            Assert.False(RecordingStore.HasPendingTree);
            Assert.True(CommittedTreeHas(TreeId));
            Assert.False(CommittedHas(ProvisionalId));
            // An unrelated live tree takes the ordinary scene-exit commit.
            Assert.False(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.Null(droppedLiveTreeId);
            Assert.Contains(logLines, l => l.Contains("End reason=discardReFlyForRewind")
                && l.Contains("treeWasPending=True")
                && l.Contains("liveTreeDropped=False"));
        }

        [Fact]
        public void FailedRewindLoad_UndoReturnsTheLiveTreeToTheFlightScene()
        {
            var s = BuildLiveSession();
            MergeDialog.TryDiscardLiveReFlySessionForRewind(s.Tree, "Rewind", out string droppedLiveTreeId);

            RecordingStore.UndoLiveTreeDropAfterFailedRewind(droppedLiveTreeId, "Rewind");

            Assert.False(CommittedTreeHas(TreeId));
            Assert.False(CommittedHas(OriginId));
            Assert.False(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.True(s.Tree.Recordings.ContainsKey(OriginId));
            // The session stays ended.
            Assert.Null(s.Scenario.ActiveReFlySessionMarker);
            Assert.Contains(logLines, l => l.Contains("[Rewind]")
                && l.Contains("load failed after ending the live Re-Fly session")
                && l.Contains("tree=" + TreeId));
        }

        [Fact]
        public void FailedRewindLoad_NothingDropped_UndoIsANoOp()
        {
            RecordingStore.ArmNextTreeSceneExitCommitSuppression("owned-by-someone-else");

            RecordingStore.UndoLiveTreeDropAfterFailedRewind(null, "Rewind");

            Assert.True(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
        }

        // ---------- Mirror direction: what must not change --------------

        [Fact]
        public void NoLiveSession_PlainRewindTouchesNothing()
        {
            var tree = new RecordingTree { Id = TreeId, TreeName = "Live" };
            var foreignRp = new RewindPoint
            {
                RewindPointId = "rp-foreign-session",
                SessionProvisional = true,
                CreatingSessionId = "sess-other",
                ChildSlots = new List<ChildSlot>(),
            };
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint> { foreignRp },
            };
            ParsekScenario.SetInstanceForTesting(scenario);

            bool ended = MergeDialog.TryDiscardLiveReFlySessionForRewind(tree, "Rewind", out string dropped);

            Assert.False(ended);
            Assert.Null(dropped);
            Assert.Single(scenario.RewindPoints);
            Assert.True(foreignRp.SessionProvisional);
            Assert.False(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.DoesNotContain(logLines, l => l.Contains("End reason="));
        }

        [Fact]
        public void MergeJournalActive_RewindLeavesTheSessionAlone()
        {
            var s = BuildLiveSession();
            var journal = new MergeJournal { JournalId = "journal-rtl", SessionId = SessionId };
            s.Scenario.ActiveMergeJournal = journal;

            bool ended = MergeDialog.TryDiscardLiveReFlySessionForRewind(s.Tree, "Rewind", out _);

            Assert.False(ended);
            Assert.NotNull(s.Scenario.ActiveReFlySessionMarker);
            Assert.True(CommittedHas(ProvisionalId));
            Assert.Equal(2, s.Scenario.RewindPoints.Count);
            Assert.False(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("Rewind discard refused: merge journal active"));
        }

        [Fact]
        public void MergeDialogDiscard_KeepsItsOwnEndReasonAndPromotionTokens()
        {
            // The shared discard body is parameterized by reason; the scene-exit dialog's
            // tokens are what RF-3 / S4.3 pin, so they must not move.
            var s = BuildLiveSession(treeInPendingSlot: true);

            MergeDialog.MergeDiscard(s.Tree);

            Assert.Null(s.Scenario.ActiveReFlySessionMarker);
            Assert.False(RecordingStore.IsNextTreeSceneExitCommitSuppressionArmed);
            Assert.Contains(logLines, l => l.Contains("[ReFlySession]")
                && l.Contains("End reason=discardReFlyAttemptFromMergeDialog sess=" + SessionId));
            Assert.Contains(logLines, l => l.Contains("Origin RP promoted to persistent rp=" + OriginRpId)
                && l.Contains("reason=discardReFlyAttemptFromMergeDialog"));
            Assert.DoesNotContain(logLines, l => l.Contains("discardReFlyForRewind"));
        }

        // ---------- Source wiring: both rewind entry points ---------------
        // InitiateRewind reaches GamePersistence / KSPUtil, which xUnit cannot run, so the
        // call sites are pinned by order over comment-stripped source.

        [Fact]
        public void InitiateRewind_EndsTheLiveSessionAfterTheJournalGate_BeforeTheRewindIsArmed()
        {
            string body = RewindPointSurvivesRewindTests.MethodBody(
                RewindPointSurvivesRewindTests.ReadSource("RecordingStore.cs"),
                "internal static void InitiateRewind(Recording rec)");
            int journal = body.IndexOf("ActiveMergeJournal.Phase != MergeJournal.Phases.Complete", StringComparison.Ordinal);
            int end = body.IndexOf("MergeDialog.TryDiscardLiveReFlySessionForRewind(", StringComparison.Ordinal);
            int begin = body.IndexOf("BeginRewindForOwner(owner);", StringComparison.Ordinal);
            int load = body.IndexOf("ExecuteRewindSaveLoad(", StringComparison.Ordinal);
            int undo = body.IndexOf("UndoLiveTreeDropAfterFailedRewind(", StringComparison.Ordinal);
            Assert.True(journal >= 0 && end > journal,
                "InitiateRewind must end a live Re-Fly session, after the merge-journal refusal");
            Assert.True(begin > end, "the session must end before the rewind is armed");
            Assert.True(undo > load, "a failed load must undo the live-tree drop");
        }

        [Fact]
        public void InitiateRewindToCareerStart_EndsTheLiveSessionBeforeTheRewindIsArmed()
        {
            string body = RewindPointSurvivesRewindTests.MethodBody(
                RewindPointSurvivesRewindTests.ReadSource("RecordingStore.cs"),
                "internal static bool InitiateRewindToCareerStart(string saveFileName)");
            int refusal = body.IndexOf("InitiateRewindToCareerStart refused", StringComparison.Ordinal);
            int end = body.IndexOf("MergeDialog.TryDiscardLiveReFlySessionForRewind(", StringComparison.Ordinal);
            int begin = body.IndexOf("RewindContext.BeginRewind(", StringComparison.Ordinal);
            int load = body.IndexOf("ExecuteRewindSaveLoad(", StringComparison.Ordinal);
            int undo = body.IndexOf("UndoLiveTreeDropAfterFailedRewind(", StringComparison.Ordinal);
            Assert.True(refusal >= 0 && end > refusal,
                "the career-start rewind must end a live Re-Fly session only once it is past its refusals");
            Assert.True(begin > end, "the session must end before the rewind is armed");
            Assert.True(undo > load, "a failed load must undo the live-tree drop");
        }

        [Theory]
        [InlineData(true, true, true)]
        [InlineData(true, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, false)]
        public void ShouldDropLiveTree_OnlyWhenTheLiveTreeIsTheSessionTree(
            bool haveSessionTree, bool liveIsSessionTree, bool expected)
        {
            var sessionTree = haveSessionTree ? new RecordingTree { Id = "t" } : null;
            var live = liveIsSessionTree ? sessionTree ?? new RecordingTree { Id = "t" }
                                         : new RecordingTree { Id = "t" };

            Assert.Equal(expected, MergeDialog.ShouldDropLiveTreeOnRewindSceneExit(sessionTree, live));
            Assert.False(MergeDialog.ShouldDropLiveTreeOnRewindSceneExit(sessionTree, null));
        }
    }
}
