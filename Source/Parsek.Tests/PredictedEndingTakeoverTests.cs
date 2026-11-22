using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q1b: flying an abandoned stage again drops the predicted ending its
    /// recording kept, so the replay shows the real flight from the takeover on.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedEndingTakeoverTests : IDisposable
    {
        private const uint StagePid = 777u;
        private const string StageGuid = "11111111111111111111111111111111";
        private const string OtherLaunchGuid = "22222222222222222222222222222222";

        private readonly List<string> logLines = new List<string>();

        public PredictedEndingTakeoverTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        // A stage Parsek stopped watching at UT 160 (last real sample) whose recording kept a
        // predicted fall to impact at UT 400.
        private static Recording MakeAbandonedStage(string id = "rec_stage", string treeId = "tree_asr")
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = "Stage",
                VesselPersistentId = StagePid,
                RecordedVesselGuid = StageGuid,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 130.0, bodyName = "Kerbin", altitude = 30000 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 35000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 160.0, endUT = 400.0, bodyName = "Kerbin",
                semiMajorAxis = 500000, eccentricity = 0.3, isPredicted = true,
            });
            rec.TerminalStateValue = TerminalState.Destroyed;
            rec.TerminalPredicted = true;
            rec.ExplicitEndUT = 400.0;
            return rec;
        }

        private static int PredictedCount(Recording rec)
        {
            int n = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
                if (rec.OrbitSegments[i].isPredicted) n++;
            return n;
        }

        // catches: a takeover leaving the predicted fall (segments, crash, end UT) on the
        // recording the real flight continues from, so the replay plays the stage twice.
        [Fact]
        public void Drop_PredictedCrash_EndsAtLastRealSample()
        {
            Recording rec = MakeAbandonedStage();
            rec.VesselDestroyed = true;

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.True(result.Dropped);
            Assert.Equal(160.0, result.CutUT);
            Assert.Equal(1, result.RemovedSegmentCount);
            Assert.Equal(0, PredictedCount(rec));
            Assert.Null(rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.False(rec.VesselDestroyed);
            Assert.True(double.IsNaN(rec.ExplicitEndUT));
            Assert.Equal(160.0, rec.EndUT);
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]")
                && l.Contains("Predicted ending dropped for takeover")
                && l.Contains("rec=rec_stage")
                && l.Contains("removedSegments=1")
                && l.Contains("previousTerminal=Destroyed")
                && l.Contains("previousTerminalPredicted=true"));
        }

        // catches: only the predicted-crash flag being read, so a predicted landing (no flag)
        // keeps its touchdown position and ending after the player flies the stage again.
        [Fact]
        public void Drop_PredictedLanding_ClearsSurfaceMetadata()
        {
            Recording rec = MakeAbandonedStage();
            rec.TerminalStateValue = TerminalState.Landed;
            rec.TerminalPredicted = false;
            rec.TerminalPosition = new SurfacePosition { body = "Kerbin", latitude = 1, longitude = 2, altitude = 70 };
            rec.TerrainHeightAtEnd = 65.0;

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.True(result.Dropped);
            Assert.Null(rec.TerminalStateValue);
            Assert.Null(rec.TerminalPosition);
            Assert.True(double.IsNaN(rec.TerrainHeightAtEnd));
            Assert.Equal(0, PredictedCount(rec));
        }

        // catches: a predicted orbit's terminal orbit elements surviving the drop, which the
        // spawn and map paths would still read as the vessel's ending.
        [Fact]
        public void Drop_PredictedOrbit_ClearsTerminalOrbit()
        {
            Recording rec = MakeAbandonedStage();
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalPredicted = false;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitSemiMajorAxis = 700000;

            PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.Null(rec.TerminalStateValue);
            Assert.Null(rec.TerminalOrbitBody);
            Assert.Equal(0.0, rec.TerminalOrbitSemiMajorAxis);
        }

        // catches: the drop wiping a real ending (an Orbiting stamp at the last sample, no
        // predicted data): that recording is left exactly as it was.
        [Fact]
        public void Drop_RealEndingAtLastSample_IsNoOp()
        {
            Recording rec = MakeAbandonedStage();
            rec.OrbitSegments.Clear();
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 130.0, endUT = 160.0, bodyName = "Kerbin", semiMajorAxis = 700000,
            });
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalPredicted = false;
            rec.ExplicitEndUT = 160.0;

            Assert.False(PredictedEndingTakeover.HasPredictedEnding(rec));
            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.False(result.Dropped);
            Assert.Equal("no-predicted-ending", result.Reason);
            Assert.Equal(TerminalState.Orbiting, rec.TerminalStateValue);
            Assert.Equal(160.0, rec.ExplicitEndUT);
            Assert.Single(rec.OrbitSegments);
        }

        // catches: a terminal UT the background close kept past the close (a predicted ending
        // with no segment) read as real.
        [Fact]
        public void Drop_TerminalStampedPastLastSampleWithoutSegments_IsDropped()
        {
            Recording rec = MakeAbandonedStage();
            rec.OrbitSegments.Clear();
            rec.TerminalPredicted = false;
            rec.ExplicitEndUT = 300.0;

            Assert.True(PredictedEndingTakeover.HasPredictedEnding(rec));
            PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.Null(rec.TerminalStateValue);
            Assert.Equal(160.0, rec.EndUT);
        }

        // catches: the cut ignoring a takeover UT earlier than the last real sample, so a
        // predicted segment would still overlap the continuation start.
        [Fact]
        public void Drop_TakeoverBeforeLastSample_CutsAtTakeover()
        {
            Recording rec = MakeAbandonedStage();
            rec.OrbitSegments[0] = new OrbitSegment
            {
                startUT = 140.0, endUT = 400.0, bodyName = "Kerbin", semiMajorAxis = 500000, isPredicted = true,
            };

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 150.0, "test");

            Assert.Equal(150.0, result.CutUT);
            Assert.Equal(1, result.ClippedSegmentCount);
            Assert.Equal(150.0, rec.OrbitSegments[0].endUT);
        }

        // catches: a recovered / docked / boarded ending (never a prediction) being cleared.
        [Theory]
        [InlineData(TerminalState.Recovered)]
        [InlineData(TerminalState.Docked)]
        [InlineData(TerminalState.Boarded)]
        public void Drop_NeverPredictedTerminal_IsKept(TerminalState terminal)
        {
            Recording rec = MakeAbandonedStage();
            rec.TerminalStateValue = terminal;
            rec.TerminalPredicted = false;

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");

            Assert.False(result.Dropped);
            Assert.Equal(terminal, rec.TerminalStateValue);
            Assert.Equal(1, PredictedCount(rec));
        }

        private static RecordingTree MakeTreeWith(params Recording[] recs)
        {
            var tree = new RecordingTree { Id = "tree_asr", TreeName = "ASR", RootRecordingId = recs[0].RecordingId };
            for (int i = 0; i < recs.Length; i++)
                tree.Recordings[recs[i].RecordingId] = recs[i];
            return tree;
        }

        // catches: a takeover of the live vessel not finding its ended recording in the live
        // tree (the route then starts a disjoint standalone segment and the prediction stays),
        // or a relaunch of the same craft (same baked pid, other launch) claiming it.
        [Fact]
        public void ResolveTakeoverLeafInTree_FindsTheSameLaunchsPredictedLeafOnly()
        {
            var core = new Recording { RecordingId = "rec_core", VesselPersistentId = 5u, TreeId = "tree_asr" };
            RecordingTree tree = MakeTreeWith(core, MakeAbandonedStage());

            Assert.Equal("rec_stage", PredictedTakeoverLeaf.ResolveInTree(tree, StagePid, StageGuid));
            Assert.Equal("rec_stage", PredictedTakeoverLeaf.ResolveInTree(tree, StagePid, null));
            Assert.Null(PredictedTakeoverLeaf.ResolveInTree(tree, StagePid, OtherLaunchGuid));
            Assert.Null(PredictedTakeoverLeaf.ResolveInTree(tree, 5u, null));
        }

        [Fact]
        public void ResolveTakeoverLeafInTree_TwoCandidates_IsNull()
        {
            RecordingTree tree = MakeTreeWith(MakeAbandonedStage("rec_a"), MakeAbandonedStage("rec_b"));
            Assert.Null(PredictedTakeoverLeaf.ResolveInTree(tree, StagePid, StageGuid));
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]") && l.Contains("Takeover leaf ambiguous"));
        }

        // catches: a recording already continued by a same-vessel child (not a line end)
        // being picked as the takeover parent.
        [Fact]
        public void ResolveTakeoverLeafInTree_ContinuedRecording_IsNotALeaf()
        {
            Recording stage = MakeAbandonedStage();
            var child = new Recording
            {
                RecordingId = "rec_child", VesselPersistentId = StagePid, RecordedVesselGuid = StageGuid,
                TreeId = "tree_asr", ParentBranchPointId = "bp1",
            };
            stage.ChildBranchPointId = "bp1";
            RecordingTree tree = MakeTreeWith(stage, child);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp1", UT = 160.0, Type = BranchPointType.VesselSwitchContinuation,
                ParentRecordingIds = new List<string> { "rec_stage" },
                ChildRecordingIds = new List<string> { "rec_child" },
            });

            Assert.Null(PredictedTakeoverLeaf.ResolveInTree(tree, StagePid, StageGuid));
        }

        // catches: the predicted fall drawn while the stage it predicts is live (the
        // ghost-plus-real-vessel duplicate), or the hide reaching the real recorded flight
        // before the last sample, or a ghost whose vessel is gone.
        [Fact]
        public void ShouldHidePredictedTailGhost_OnlyPastLastSampleWhileVesselLives()
        {
            Recording rec = MakeAbandonedStage();
            Func<Recording, bool> alive = r => true;
            Func<Recording, bool> gone = r => false;

            Assert.True(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, false, alive));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 150.0, false, alive));
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, false, gone));

            PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 250.0, "test");
            Assert.False(GhostPlaybackLogic.ShouldHidePredictedTailGhostForScene(rec, 200.0, false, alive));
        }

        // catches: the committed-tree restore refusing a predicted crash as a dead leaf, which
        // sent a Tracking Station Fly / KSC Fly / far Switch-To of the still-falling stage to a
        // disjoint standalone tree and left the committed prediction playing.
        [Fact]
        public void IsCommittedSpawnedRecordingRestorable_AcceptsPredictedCrashOnly()
        {
            Recording predicted = MakeAbandonedStage();
            RecordingTree tree = MakeTreeWith(new Recording { RecordingId = "rec_core", VesselPersistentId = 5u }, predicted);
            tree.ActiveRecordingId = "rec_core";
            Assert.True(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, predicted));

            predicted.TerminalPredicted = false;
            Assert.False(ParsekFlight.IsCommittedSpawnedRecordingRestorable(tree, predicted));
        }

        // catches: an in-place resume of debris past its 60 s limit (it would end at once and
        // re-predict the fall) instead of a plain continuation segment, or a debris piece
        // still inside its limit being sent to a segment.
        [Fact]
        public void IsDebrisPastLifetimeAtTakeover_ReadsTheSeparationLimit()
        {
            Recording stage = MakeAbandonedStage();
            stage.IsDebris = true;
            stage.ParentBranchPointId = "bp_sep";
            RecordingTree tree = MakeTreeWith(new Recording { RecordingId = "rec_core", VesselPersistentId = 5u }, stage);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp_sep", UT = 100.0, Type = BranchPointType.JointBreak,
                ParentRecordingIds = new List<string> { "rec_core" },
                ChildRecordingIds = new List<string> { "rec_stage" },
            });

            Assert.False(ParsekFlight.IsDebrisPastLifetimeAtTakeover(tree, stage, 150.0));
            Assert.True(ParsekFlight.IsDebrisPastLifetimeAtTakeover(tree, stage, 250.0));
            Assert.False(ParsekFlight.IsDebrisPastLifetimeAtTakeover(tree, stage, double.NaN));
            stage.IsDebris = false;
            Assert.False(ParsekFlight.IsDebrisPastLifetimeAtTakeover(tree, stage, 250.0));
        }

        // catches: the take resuming a committed predicted-crash stage without dropping the
        // prediction from the clone, or dropping it from the committed copy itself (a
        // discard must keep committed history).
        [Fact]
        public void TryTakeCommittedTree_PredictedCrashStage_DropsPredictionFromCloneOnly()
        {
            Recording committedStage = MakeAbandonedStage();
            RecordingTree tree = MakeTreeWith(committedStage);
            tree.ActiveRecordingId = "rec_stage";
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(committedStage);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                StagePid, out RecordingTree liveTree, out string recId, out _,
                activeVesselGuid: StageGuid, liveUT: 250.0);

            Assert.True(taken);
            Assert.Equal("rec_stage", recId);
            Recording clone = liveTree.Recordings["rec_stage"];
            Assert.NotSame(committedStage, clone);
            Assert.Null(clone.TerminalStateValue);
            Assert.Equal(0, PredictedCount(clone));
            Assert.Equal(160.0, clone.EndUT);

            Assert.Equal(TerminalState.Destroyed, committedStage.TerminalStateValue);
            Assert.True(committedStage.TerminalPredicted);
            Assert.Equal(1, PredictedCount(committedStage));
        }

        // catches: the same take for a committed stage that is NOT the tree's active
        // recording (the usual abandoned-stage leaf) refusing or keeping the prediction.
        [Fact]
        public void TryTakeCommittedTree_PredictedCrashBackgroundLeaf_Resumes()
        {
            var core = new Recording
            {
                RecordingId = "rec_core", TreeId = "tree_asr", VesselPersistentId = 5u,
                ExplicitStartUT = 100, ExplicitEndUT = 300, TerminalStateValue = TerminalState.Orbiting,
            };
            Recording committedStage = MakeAbandonedStage();
            RecordingTree tree = MakeTreeWith(core, committedStage);
            tree.ActiveRecordingId = "rec_core";
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(core);
            RecordingStore.AddCommittedInternal(committedStage);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                StagePid, out RecordingTree liveTree, out string recId,
                out ParsekFlight.CommittedSpawnedVesselRestoreAction action,
                activeVesselGuid: StageGuid, liveUT: 250.0);

            Assert.True(taken);
            Assert.Equal("rec_stage", recId);
            Assert.Equal(ParsekFlight.CommittedSpawnedVesselRestoreAction.PromoteFromBackground, action);
            Assert.Null(liveTree.Recordings["rec_stage"].TerminalStateValue);
            Assert.Equal(0, PredictedCount(liveTree.Recordings["rec_stage"]));
        }
    }
}
