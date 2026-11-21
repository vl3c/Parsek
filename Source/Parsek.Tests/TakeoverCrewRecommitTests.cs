using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings Q6c + Q1b: a committed predicted crash kills its crew (a Dead
    /// KerbalAssignment row), and when the player flies that stage again the clone the real
    /// flight continues from drops the prediction. When the clone is committed over the
    /// committed copy, the crew answer and its ledger rows follow the real flight: a kerbal the
    /// real flight did not kill is alive again. A recording nobody took over keeps its rows.
    /// </summary>
    [Collection("Sequential")]
    public class TakeoverCrewRecommitTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private const string TreeId = "tree-takeover-crew";
        private const string CoreId = "rec-core";
        private const string StageId = "rec-stage";
        private const uint StagePid = 4242;
        private readonly List<string> logLines = new List<string>();

        public TakeoverCrewRecommitTests()
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

        private static Recording Core()
        {
            var rec = new Recording
            {
                RecordingId = CoreId, TreeId = TreeId, VesselName = "Core", VesselPersistentId = 5u,
                ExplicitStartUT = 100.0, ExplicitEndUT = 300.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin", altitude = 80000 });
            rec.StampTerminalState(TerminalState.Orbiting, "test");
            return rec;
        }

        // A crewed stage Parsek stopped watching at UT 160 and predicted to crash at UT 400.
        private static Recording CrewedPredictedCrashStage()
        {
            var rec = new Recording
            {
                RecordingId = StageId, TreeId = TreeId, VesselName = "Crewed Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = "0123456789abcdef0123456789abcdef",
                GhostVisualSnapshot = CrewSnapshot(Jeb),
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

        private static RecordingTree CommitTreeWith(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Takeover crew", RootRecordingId = CoreId, ActiveRecordingId = CoreId,
            };
            foreach (var rec in recs)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }
            RecordingStore.AddCommittedTreeForTesting(tree);
            LedgerOrchestrator.NotifyLedgerTreeCommitted(tree);
            return tree;
        }

        private static List<GameAction> Rows(GameActionType type, string recordingId)
        {
            var rows = new List<GameAction>();
            foreach (var a in Ledger.Actions)
                if (a.Type == type && a.RecordingId == recordingId)
                    rows.Add(a);
            return rows;
        }

        private static void AddVesselLossEvent(string recordingId, double ut)
        {
            var evt = new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.ReputationChanged,
                key = KerbalDeathRepPenalty.VesselLossEventKey,
                valueBefore = 0.0,
                valueAfter = -10.0,
                recordingId = recordingId,
            };
            GameStateStore.AddEvent(ref evt);
        }

        // catches: the clone re-commit after a takeover leaving the committed prediction's
        // Dead row (and the death penalty) standing although the real flight kept Jeb alive -
        // the ledger dedup keys a KerbalAssignment row on (recording, kerbal) and drops the
        // re-derived Aboard row against the stale Dead one.
        [Fact]
        public void TakeoverRecommit_RealFlightKeepsTheKerbalAlive_DeadRowAndPenaltyGo()
        {
            AddVesselLossEvent(StageId, 400.0);
            RecordingTree committed = CommitTreeWith(Core(), CrewedPredictedCrashStage());
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Single(Rows(GameActionType.ReputationPenalty, StageId));

            // The player flies the stage again: the clone drops the prediction, the real flight
            // runs on to UT 500 and ends in orbit with Jeb aboard.
            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording stage = clone.Recordings[StageId];
            PredictedEndingTakeover.DropPredictedEndingForTakeover(stage, 250.0, "test");
            stage.Points.Add(new TrajectoryPoint { ut = 250.0, bodyName = "Kerbin", altitude = 60000 });
            stage.Points.Add(new TrajectoryPoint { ut = 500.0, bodyName = "Kerbin", altitude = 90000 });
            stage.VesselSnapshot = CrewSnapshot(Jeb);
            stage.ExplicitEndUT = 500.0;
            stage.StampTerminalState(TerminalState.Orbiting, "test");

            Assert.True(TreeCommitService.CommitTree(clone));
            LedgerOrchestrator.NotifyLedgerTreeCommitted(RecordingStore.CommittedTrees[0]);

            Recording committedStage = RecordingStore.FindRecordingById(StageId);
            Assert.Same(stage, committedStage);
            Assert.Equal(KerbalEndState.Aboard, committedStage.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Aboard, rows[0].KerbalEndStateField);
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("predicted crash taken over") && l.Contains(StageId));
        }

        // catches: the takeover drop leaving the clone's resolved Dead crew answer in place, so
        // a commit path that stamps no new terminal keeps reading the predicted death.
        [Fact]
        public void TakeoverDrop_ClearsTheCrewAnswerTheDroppedEndingDecided()
        {
            Recording stage = CrewedPredictedCrashStage();
            KerbalsModule.PopulateCrewEndStates(stage);
            Assert.Equal(KerbalEndState.Dead, stage.CrewEndStates[Jeb]);

            PredictedEndingTakeover.DropPredictedEndingForTakeover(stage, 250.0, "test");

            Assert.Null(stage.CrewEndStates);
            Assert.False(stage.CrewEndStatesResolved);
            Assert.False(stage.CrewDeathRespawns.HasValue);
        }

        // catches: the refresh reaching a recording nobody took over (owner rule: never change
        // past ledger behavior) - only the flown core changed, the stage keeps its rows.
        [Fact]
        public void Recommit_UntouchedPredictedCrash_KeepsItsRows()
        {
            AddVesselLossEvent(StageId, 400.0);
            RecordingTree committed = CommitTreeWith(Core(), CrewedPredictedCrashStage());
            GameAction deadRow = Rows(GameActionType.KerbalAssignment, StageId)[0];
            GameAction penalty = Rows(GameActionType.ReputationPenalty, StageId)[0];

            RecordingTree clone = RecordingTree.DeepClone(committed);
            Recording core = clone.Recordings[CoreId];
            core.Points.Add(new TrajectoryPoint { ut = 600.0, bodyName = "Kerbin", altitude = 90000 });
            core.ExplicitEndUT = 600.0;

            Assert.True(TreeCommitService.CommitTree(clone));
            LedgerOrchestrator.NotifyLedgerTreeCommitted(RecordingStore.CommittedTrees[0]);

            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Same(deadRow, rows[0]);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);
            Assert.Same(penalty, Rows(GameActionType.ReputationPenalty, StageId)[0]);
            Assert.DoesNotContain(logLines, l => l.Contains("predicted crash taken over"));
        }

        [Theory]
        // existing predicted Destroyed, incoming terminal, incoming predicted, incoming end, expected
        [InlineData(true, TerminalState.Orbiting, false, 500.0, true)]
        [InlineData(true, TerminalState.Destroyed, false, 500.0, true)]  // the real flight crashed for real
        [InlineData(true, TerminalState.Destroyed, true, 700.0, true)]   // abandoned again, a new prediction
        [InlineData(true, TerminalState.Destroyed, true, 400.0, false)]  // untouched
        [InlineData(false, TerminalState.Orbiting, false, 500.0, false)] // a confirmed crash is history
        public void ShouldRefreshCrewRowsOnReplace_TruthTable(
            bool existingPredicted, TerminalState incomingTerminal, bool incomingPredicted,
            double incomingEnd, bool expected)
        {
            var existing = new Recording { RecordingId = StageId, ExplicitStartUT = 100.0, ExplicitEndUT = 400.0 };
            existing.StampTerminalState(TerminalState.Destroyed, "test", predicted: existingPredicted);
            var incoming = new Recording { RecordingId = StageId, ExplicitStartUT = 100.0, ExplicitEndUT = incomingEnd };
            incoming.StampTerminalState(incomingTerminal, "test", predicted: incomingPredicted);

            Assert.Equal(expected, LedgerOrchestrator.ShouldRefreshCrewRowsOnReplace(existing, incoming));
        }
    }
}
