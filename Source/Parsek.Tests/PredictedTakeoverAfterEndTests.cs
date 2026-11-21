using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A takeover at or after the UT a predicted Landed / Splashed / Orbiting ending completes
    /// (the replay reached it and spawned the vessel there, the player then flies the spawned
    /// vessel) keeps the whole predicted tail: it already happened in the replayed timeline.
    /// Only the ending itself goes, so the flight continues from the spawn. A predicted crash
    /// whose vessel is still alive after the crash UT is not that case: stock contradicted it
    /// and it is dropped as before.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTakeoverAfterEndTests : IDisposable
    {
        private const uint StagePid = 777u;
        private const uint SpawnPid = 9001u;
        private const string StageGuid = "11111111111111111111111111111111";

        private readonly List<string> logLines = new List<string>();

        public PredictedTakeoverAfterEndTests()
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

        // A capsule Parsek stopped watching at UT 160 whose recording kept a predicted descent
        // to a touchdown at UT 400.
        private static Recording MakePredictedTouchdown(TerminalState terminal = TerminalState.Landed)
        {
            var rec = new Recording
            {
                RecordingId = "rec_capsule",
                TreeId = "tree_asr",
                VesselName = "Capsule",
                VesselPersistentId = StagePid,
                RecordedVesselGuid = StageGuid,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 15000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 160.0, endUT = 400.0, bodyName = "Kerbin",
                semiMajorAxis = 400000, eccentricity = 0.9, isPredicted = true,
            });
            rec.TerminalStateValue = terminal;
            rec.TerminalPredicted = terminal != TerminalState.Orbiting;
            rec.ExplicitEndUT = 400.0;
            rec.TerminalPosition = new SurfacePosition { body = "Kerbin", latitude = 1, longitude = 2, altitude = 70 };
            rec.TerrainHeightAtEnd = 65.0;
            rec.CrewEndStatesResolved = true;
            rec.CrewEndStates = new Dictionary<string, KerbalEndState> { { "Jebediah Kerman", KerbalEndState.Aboard } };
            return rec;
        }

        private static int PredictedCount(Recording rec)
        {
            int n = 0;
            for (int i = 0; i < rec.OrbitSegments.Count; i++)
                if (rec.OrbitSegments[i].isPredicted) n++;
            return n;
        }

        // catches: the takeover of the spawned capsule dropping the descent it already flew in
        // the replayed timeline, so later replays stop it in mid-air at UT 160 and then show it
        // on the ground.
        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        public void Drop_TakeoverAfterPredictedTouchdown_KeepsTheWholeDescent(TerminalState terminal)
        {
            Recording rec = MakePredictedTouchdown(terminal);

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 450.0, "test");

            Assert.False(result.Dropped);
            Assert.Equal("takeover-after-predicted-end", result.Reason);
            Assert.True(result.TerminalCleared);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(400.0, rec.OrbitSegments[0].endUT);
            Assert.Equal(0, PredictedCount(rec));
            Assert.Equal(400.0, rec.EndUT);
            Assert.Null(rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.Null(rec.CrewEndStates);
            Assert.False(rec.CrewEndStatesResolved);
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]")
                && l.Contains("Predicted ending kept for takeover after its end")
                && l.Contains("rec=rec_capsule")
                && l.Contains("predictedEndUT=400.000")
                && l.Contains("keptSegments=1")
                && l.Contains("previousTerminal=" + terminal));
        }

        // catches: the keep rule firing before the predicted ending completed (the capsule is
        // still coming down at the takeover, so the descent past the takeover is a guess).
        [Fact]
        public void Drop_TakeoverBeforePredictedTouchdown_StillDropsTheDescent()
        {
            Recording rec = MakePredictedTouchdown();

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 300.0, "test");

            Assert.True(result.Dropped);
            Assert.Equal(160.0, result.CutUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.Null(rec.TerminalStateValue);
        }

        // catches: a vessel still alive after the UT of its predicted crash (stock contradicted
        // the prediction, no spawn ever happened) keeping that crash as history.
        [Fact]
        public void Drop_TakeoverAfterPredictedCrash_DropsTheCrash()
        {
            Recording rec = MakePredictedTouchdown(TerminalState.Destroyed);
            rec.TerminalPosition = null;

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 450.0, "test");

            Assert.True(result.Dropped);
            Assert.Equal(160.0, result.CutUT);
            Assert.Empty(rec.OrbitSegments);
            Assert.Null(rec.TerminalStateValue);
        }

        // catches: a Q8 final-orbit spawn flown later losing the escape tail it was spawned
        // from (the live orbit is past every predicted segment, so the coast match finds no
        // segment at the takeover and the old rule cut at the last real sample).
        [Fact]
        public void Drop_TakeoverAfterFinalOrbitEnding_KeepsTheTail()
        {
            Recording rec = MakePredictedTouchdown(TerminalState.Orbiting);
            rec.TerminalPosition = null;
            rec.TerminalOrbitBody = "Sun";
            rec.TerminalOrbitSemiMajorAxis = 1.5e10;
            var evidence = new TakeoverCoastEvidence
            {
                LiveOrbit = new OrbitSegment
                {
                    startUT = 900.0, endUT = 900.0, bodyName = "Sun", semiMajorAxis = 1.5e10,
                },
                GravParameter = 1.1723328e18,
            };

            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 900.0, "test", evidence);

            Assert.Equal("takeover-after-predicted-end", result.Reason);
            Assert.Single(rec.OrbitSegments);
            Assert.Equal(0, PredictedCount(rec));
            Assert.Equal(400.0, rec.EndUT);
            Assert.Null(rec.TerminalStateValue);
            Assert.Null(rec.TerminalOrbitBody);
        }

        // A lander that touched down for real at UT 160 (its last sample: a packed landed vessel
        // records none) and was committed at UT 3760: the background recorder moved its explicit
        // end to the commit UT.
        private static Recording MakeRealLanderCommittedLater(TerminalState terminal)
        {
            var rec = new Recording
            {
                RecordingId = "rec_lander",
                TreeId = "tree_asr",
                VesselName = "Lander",
                VesselPersistentId = StagePid,
                RecordedVesselGuid = StageGuid,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Mun", altitude = 500 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Mun", altitude = 3 });
            rec.TerminalStateValue = terminal;
            rec.ExplicitEndUT = 3760.0;
            rec.TerminalPosition = new SurfacePosition { body = "Mun", latitude = 1, longitude = 2, altitude = 3 };
            return rec;
        }

        // catches (review B1): a real Landed / Splashed / Orbiting / SubOrbital ending whose
        // explicit end the background recorder moved to the commit UT read as a prediction, so
        // a later Tracking Station Fly or Switch-To wiped the real ending and its touchdown spot.
        [Theory]
        [InlineData(TerminalState.Landed)]
        [InlineData(TerminalState.Splashed)]
        [InlineData(TerminalState.Orbiting)]
        [InlineData(TerminalState.SubOrbital)]
        public void HasPredictedEnding_RealEndingWithCommitUTEnd_IsFalse(TerminalState terminal)
        {
            Recording rec = MakeRealLanderCommittedLater(terminal);

            Assert.False(PredictedEndingTakeover.HasPredictedEnding(rec));
            PredictedEndingDropResult result =
                PredictedEndingTakeover.DropPredictedEndingForTakeover(rec, 4000.0, "test");

            Assert.False(result.Dropped);
            Assert.Equal("no-predicted-ending", result.Reason);
            Assert.Equal(terminal, rec.TerminalStateValue);
            Assert.NotNull(rec.TerminalPosition);
            Assert.Equal(3760.0, rec.ExplicitEndUT);
        }

        // catches (review B1): the committed-tree restore of a real lander dropping its real
        // ending from the clone the flight resumes.
        [Fact]
        public void TryTakeCommittedTree_RealLanderCommittedLater_KeepsItsEnding()
        {
            Recording committed = MakeRealLanderCommittedLater(TerminalState.Landed);
            var tree = new RecordingTree { Id = "tree_asr", TreeName = "ASR" };
            tree.AddOrReplaceRecording(committed);
            tree.ActiveRecordingId = committed.RecordingId;
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(committed);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                StagePid, out RecordingTree liveTree, out _, out _,
                activeVesselGuid: StageGuid, liveUT: 4000.0);

            Assert.True(taken);
            Recording clone = liveTree.Recordings["rec_lander"];
            Assert.Equal(TerminalState.Landed, clone.TerminalStateValue);
            Assert.NotNull(clone.TerminalPosition);
        }

        // catches: the spawned-vessel restore (Tracking Station Fly of the spawned capsule)
        // still dropping the predicted descent from the clone it resumes.
        [Fact]
        public void TryTakeCommittedTree_SpawnedCapsuleFlownAfterTouchdown_KeepsDescentInClone()
        {
            Recording committed = MakePredictedTouchdown();
            committed.VesselSpawned = true;
            committed.SpawnedVesselPersistentId = SpawnPid;
            var tree = new RecordingTree { Id = "tree_asr", TreeName = "ASR" };
            tree.AddOrReplaceRecording(committed);
            tree.ActiveRecordingId = committed.RecordingId;
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(committed);

            bool taken = ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                SpawnPid, out RecordingTree liveTree, out string recId, out _,
                activeVesselGuid: null, liveUT: 450.0);

            Assert.True(taken);
            Assert.Equal("rec_capsule", recId);
            Recording clone = liveTree.Recordings["rec_capsule"];
            Assert.NotSame(committed, clone);
            Assert.Single(clone.OrbitSegments);
            Assert.Equal(0, PredictedCount(clone));
            Assert.Equal(400.0, clone.EndUT);
            Assert.Null(clone.TerminalStateValue);

            Assert.Equal(TerminalState.Landed, committed.TerminalStateValue);
            Assert.Equal(1, PredictedCount(committed));
        }
    }
}
