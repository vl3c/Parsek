using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q1b at a dock, a claw grab or a boarding: joining an abandoned vessel whose
    /// recording kept a predicted ending counts as flying it again. The prediction is dropped
    /// (with the coast-keep rule), the vessel's recording ends Docked / Boarded at the merge,
    /// and its crew are re-derived (Q6c had killed them with the predicted crash; they now live
    /// on the combined ship). A vessel of the live tree joins the merge as its second parent
    /// at once; a vessel of an earlier committed mission is taken over when the live tree is
    /// committed (a discard or a revert leaves it as it was).
    /// </summary>
    [Collection("Sequential")]
    public class MergePartnerTakeoverTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private const string StageTreeId = "tree-stage";
        private const string StageId = "rec-stage";
        private const uint StagePid = 4242;
        private const string StageGuid = "0123456789abcdef0123456789abcdef";
        private const string ShipTreeId = "tree-ship";
        private const string ShipId = "rec-ship";
        private const string MergedId = "rec-merged";
        private const uint ShipPid = 77;
        private readonly List<string> logLines = new List<string>();

        public MergePartnerTakeoverTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
        }

        public void Dispose()
        {
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        // A crewed stage Parsek stopped watching at UT 160 and predicted to crash at UT 400.
        private static Recording CrewedPredictedCrashStage(string treeId = StageTreeId)
        {
            var rec = new Recording
            {
                RecordingId = StageId, TreeId = treeId, VesselName = "Crewed Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = StageGuid,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                VesselSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 35000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 160.0, endUT = 400.0, bodyName = "Kerbin",
                semiMajorAxis = 500000, eccentricity = 0.3, isPredicted = true,
            });
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            rec.VesselDestroyed = true;
            rec.ExplicitEndUT = 400.0;
            return rec;
        }

        private static Recording Ship(string treeId)
        {
            var rec = new Recording
            {
                RecordingId = ShipId, TreeId = treeId, VesselName = "Rescue Ship",
                VesselPersistentId = ShipPid, ExplicitStartUT = 200.0, ExplicitEndUT = 250.0,
                ChildBranchPointId = "bp-dock",
            };
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin", altitude = 30000 });
            rec.Points.Add(new TrajectoryPoint { ut = 250.0, bodyName = "Kerbin", altitude = 34000 });
            return rec;
        }

        private static Recording Merged(string treeId)
        {
            var rec = new Recording
            {
                RecordingId = MergedId, TreeId = treeId, VesselName = "Rescue Ship",
                VesselPersistentId = ShipPid, ExplicitStartUT = 250.0, ExplicitEndUT = 600.0,
                ParentBranchPointId = "bp-dock",
                GhostVisualSnapshot = CrewSnapshot(Jeb), VesselSnapshot = CrewSnapshot(Jeb),
            };
            rec.Points.Add(new TrajectoryPoint { ut = 250.0, bodyName = "Kerbin", altitude = 34000 });
            rec.Points.Add(new TrajectoryPoint { ut = 600.0, bodyName = "Kerbin", altitude = 80000 });
            rec.StampTerminalState(TerminalState.Orbiting, "test");
            return rec;
        }

        private static RecordingTree ShipTree(BranchPointType type)
        {
            var tree = new RecordingTree
            {
                Id = ShipTreeId, TreeName = "Rescue", RootRecordingId = ShipId, ActiveRecordingId = MergedId,
            };
            tree.AddOrReplaceRecording(Ship(ShipTreeId));
            tree.AddOrReplaceRecording(Merged(ShipTreeId));
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", UT = 250.0, Type = type,
                ParentRecordingIds = new List<string> { ShipId },
                ChildRecordingIds = new List<string> { MergedId },
                TargetVesselPersistentId = StagePid,
                TargetVesselGuid = StageGuid,
            });
            return tree;
        }

        private static void CommitStageMission(Recording stage)
        {
            var tree = new RecordingTree
            {
                Id = StageTreeId, TreeName = "Stage mission", RootRecordingId = StageId, ActiveRecordingId = StageId,
            };
            tree.AddOrReplaceRecording(stage);
            RecordingStore.AddRecordingWithTreeForTesting(stage);
            RecordingStore.AddCommittedTreeForTesting(tree);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
        }

        private static List<GameAction> Rows(GameActionType type, string recordingId)
        {
            var rows = new List<GameAction>();
            foreach (var a in Ledger.Actions)
                if (a.Type == type && a.RecordingId == recordingId)
                    rows.Add(a);
            return rows;
        }

        // catches: docking a rescue ship to a committed abandoned stage leaving its predicted
        // crash standing, so the replay still crashes it and (Q6c) kills Jeb, who now lives on
        // the combined ship.
        [Theory]
        [InlineData(BranchPointType.Dock, TerminalState.Docked)]
        [InlineData(BranchPointType.Board, TerminalState.Boarded)]
        public void Commit_MergeWithCommittedPredictedStage_EndsItAtTheMerge_CrewLive(
            BranchPointType type, TerminalState expected)
        {
            Recording stage = CrewedPredictedCrashStage();
            CommitStageMission(stage);
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);

            int taken = TreeCommitService.TakeOverPredictedEndingsOfMergePartners(ShipTree(type), "test");

            Assert.Equal(1, taken);
            Assert.Equal(expected, stage.TerminalStateValue);
            Assert.False(stage.TerminalPredicted);
            Assert.False(stage.VesselDestroyed);
            Assert.Equal(250.0, stage.ExplicitEndUT);
            Assert.Equal(250.0, stage.EndUT);
            foreach (OrbitSegment seg in stage.OrbitSegments)
                Assert.False(seg.isPredicted && seg.endUT > 250.0);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Aboard, rows[0].KerbalEndStateField);
            Assert.Contains(logLines, l => l.Contains("[RecordingStore]")
                && l.Contains("merge partner's predicted ending taken over")
                && l.Contains("partner='" + StageId + "'") && l.Contains("terminal=" + expected));
        }

        // catches: the take-over running at the merge itself instead of at the commit, so a
        // Discard of the rescue flight (or a revert, where stock puts the stage back on its
        // fall) still left the committed mission rewritten. Wiring: CommitTree runs the pass.
        [Fact]
        public void CommitTree_RunsTheMergePartnerTakeOver()
        {
            Recording stage = CrewedPredictedCrashStage();
            CommitStageMission(stage);

            Assert.True(TreeCommitService.CommitTree(ShipTree(BranchPointType.Dock)));

            Assert.Equal(TerminalState.Docked, stage.TerminalStateValue);
        }

        // catches: a partner whose recording ends for real (no prediction) being rewritten,
        // and a stage of another launch (same craft-baked pid, different launch guid).
        [Fact]
        public void Commit_PartnerWithoutPredictionOrOtherLaunch_IsLeftAlone()
        {
            Recording stage = CrewedPredictedCrashStage();
            stage.RecordedVesselGuid = "ffffffffffffffffffffffffffffffff";
            CommitStageMission(stage);

            Assert.Equal(0, TreeCommitService.TakeOverPredictedEndingsOfMergePartners(
                ShipTree(BranchPointType.Dock), "test"));
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.True(stage.TerminalPredicted);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            Recording real = CrewedPredictedCrashStage();
            real.OrbitSegments.Clear();
            real.ExplicitEndUT = 160.0;
            real.StampTerminalState(TerminalState.Orbiting, "test");
            CommitStageMission(real);

            Assert.Equal(0, TreeCommitService.TakeOverPredictedEndingsOfMergePartners(
                ShipTree(BranchPointType.Dock), "test"));
            Assert.Equal(TerminalState.Orbiting, real.TerminalStateValue);
        }

        // catches: the live-tree half: a stage of the flight being recorded whose recording
        // already ended in a prediction (debris limit, unload) is no background member, so the
        // dock treated it as a foreign vessel and its predicted crash stayed in the tree.
        [Fact]
        public void LiveTree_PredictedLeafJoiningTheMerge_IsTakenOverAsTheSecondParent()
        {
            var tree = new RecordingTree { Id = ShipTreeId, TreeName = "Rescue", ActiveRecordingId = ShipId };
            Recording ship = Ship(ShipTreeId);
            ship.ChildBranchPointId = null;
            tree.AddOrReplaceRecording(ship);
            Recording stage = CrewedPredictedCrashStage(ShipTreeId);
            tree.AddOrReplaceRecording(stage);

            string id = ParsekFlightSplitBrancher.TakeOverPredictedMergePartnerLeaf(
                tree, StagePid, ShipPid, StagePid, StageGuid, null, 250.0, "test");

            Assert.Equal(StageId, id);
            Assert.Null(stage.TerminalStateValue);
            Assert.False(stage.TerminalPredicted);
            Assert.Equal(160.0, stage.EndUT);
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]")
                && l.Contains("Predicted ending dropped for takeover") && l.Contains("context=MergeBranch.test"));
        }

        [Fact]
        public void LiveTree_NoPredictedLeaf_IsNull()
        {
            var tree = new RecordingTree { Id = ShipTreeId, TreeName = "Rescue", ActiveRecordingId = ShipId };
            Recording ship = Ship(ShipTreeId);
            ship.ChildBranchPointId = null;
            tree.AddOrReplaceRecording(ship);

            Assert.Null(ParsekFlightSplitBrancher.TakeOverPredictedMergePartnerLeaf(
                tree, StagePid, ShipPid, StagePid, StageGuid, null, 250.0, "test"));
        }
    }
}
