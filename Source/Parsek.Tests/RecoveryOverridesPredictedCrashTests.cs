using System;
using System.Collections.Generic;
using Xunit;
using F = Parsek.Tests.PredictedTouchdownStockFateTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Final-review fixes around recoveries of predicted endings (owner rulings P-STOCK, Q6b,
    /// Q12b, Q13, Q13b):
    /// <list type="number">
    /// <item>Q13b: any recovery of the launch-matched vessel overrides a predicted Destroyed,
    /// also after its predicted UT (a crewed stage with armed chutes predicted to crash drag-free,
    /// which stock then opened the chutes on and landed after that UT); a predicted touchdown
    /// still follows a recovery only before its touchdown, and a confirmed crash stays final.
    /// The reconciler's crash pass reads a recovery / termination row (and a live tree's dock /
    /// board) as the player's doing, never as stock confirming the crash.</item>
    /// <item>A kerbal stock recovered with the vessel is Recovered even when his START seat is
    /// a part that later exploded (he moved by IVA first): the recovery carries the recovered
    /// vessel's crew, because it drops the end snapshot that names the last seats.</item>
    /// <item>Q12b negative, and the crew of a stage whose low point sits among the peaks and
    /// whose ground scan found no crash (owner ruling 2026-11-22).</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class RecoveryOverridesPredictedCrashTests : IDisposable
    {
        private const string Jeb = F.Jeb;
        private const string Bob = "Bob Kerman";
        private const string StageId = "rec-stage-q13b";
        private const uint StagePid = 4545u;
        private const string StageGuid = "aaaabbbbccccddddeeeeffff00001111";
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppressCrewEvents;

        public RecoveryOverridesPredictedCrashTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
            priorSuppressCrewEvents = GameStateRecorder.SuppressCrewEvents;
            GameStateRecorder.SuppressCrewEvents = false;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            PredictedTouchdownStockFate.ResetForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.SuppressCrewEvents = priorSuppressCrewEvents;
            PredictedTouchdownStockFate.ResetForTesting();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            SessionSuppressionState.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.LiveClockUTProviderForTesting = null;
            KerbalsModule.LoadedSaveUTProviderForTesting = null;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ------------------------------------------------------------------ fixtures

        private static KerbalsModule Kerbals => LedgerOrchestrator.Kerbals;

        // Two parts: the command pod (pid 100) and a crew cabin (pid 200).
        private static ConfigNode Seats(string podCrew, string cabinCrew)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            var pod = snapshot.AddNode("PART");
            pod.AddValue("persistentId", "100");
            if (podCrew != null) pod.AddValue("crew", podCrew);
            var cabin = snapshot.AddNode("PART");
            cabin.AddValue("persistentId", "200");
            if (cabinCrew != null) cabin.AddValue("crew", cabinCrew);
            return snapshot;
        }

        // A crewed stage with armed (not open) chutes, left at UT 960 inside physics range and
        // predicted (Q10b) to crash drag-free at UT 1300: Jeb dies with it (Q6c).
        private static Recording ChutedStagePredictedToCrash()
        {
            var rec = new Recording
            {
                RecordingId = StageId, TreeId = F.TreeId, VesselName = "Chuted Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = StageGuid,
                VesselSnapshot = F.CrewSnapshot(Jeb),
                GhostVisualSnapshot = F.CrewSnapshot(Jeb),
                ExplicitStartUT = 900.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 960.0, bodyName = "Kerbin", altitude = 35000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 960.0, endUT = 1300.0, bodyName = "Kerbin",
                semiMajorAxis = 500000, eccentricity = 0.3, isPredicted = true,
            });
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            rec.VesselDestroyed = true;
            rec.ExplicitEndUT = 1300.0;
            return rec;
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

        private static List<GameAction> Rows(GameActionType type, string recordingId)
        {
            var rows = new List<GameAction>();
            foreach (var a in Ledger.Actions)
                if (a.Type == type && a.RecordingId == recordingId)
                    rows.Add(a);
            return rows;
        }

        private static int ApplyRecovery(string guid, double recoveryUT, IEnumerable<string> crew = null)
        {
            return ParsekScenario.ApplyRecoveryOfCommittedPredictions(
                EffectiveState.ComputeERS(), guid, recoveryUT, null, "test", crew);
        }

        // ================================================================== 1. Q13b

        // catches: a recovery after the predicted crash UT left as committed (the old
        // AfterPredictedEnd refusal): Jeb stays dead and his death penalty booked although
        // stock landed the stage under its chutes and the player recovered him.
        [Theory]
        [InlineData(1300.0)]
        [InlineData(1310.0)]
        [InlineData(5000.0)]
        public void CommittedPredictedCrash_RecoveredAfterThePredictedCrash_EndsRecovered_CrewLive(double recoveryUT)
        {
            AddVesselLossEvent(StageId, 1300.0);
            Recording stage = ChutedStagePredictedToCrash();
            F.CommitTreeWith(F.Core(), stage);
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Single(Rows(GameActionType.ReputationPenalty, StageId));
            Assert.Equal(PredictedEndingRecoveryOutcome.Recover,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, recoveryUT, ownedElsewhere: false));

            Assert.Equal(1, ApplyRecovery(StageGuid, recoveryUT, new[] { Jeb }));

            Recording after = RecordingStore.FindRecordingById(StageId);
            Assert.NotSame(stage, after);
            Assert.Equal(TerminalState.Recovered, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(after.VesselDestroyed);
            Assert.Equal(recoveryUT, after.EndUT, 6);
            // the drag-free crash arc is not what happened: the whole predicted tail goes
            foreach (OrbitSegment seg in after.OrbitSegments)
                Assert.False(seg.isPredicted && seg.endUT > 960.0 + 1e-6, "predicted tail kept past the last real sample");
            Assert.Equal(KerbalEndState.Recovered, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Recovered, rows[0].KerbalEndStateField);
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(after, false).needsSpawn);
            // the committed copy the pass read was never written
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("[" + PredictedEndingRecovery.LogTag + "]")
                && l.Contains("Predicted crash recovered by the player") && l.Contains(StageId));
        }

        // A predicted touchdown keeps the old rule: recovered at or after its touchdown, the
        // replay already landed it, an ordinary landed recovery.
        [Fact]
        public void CommittedPredictedTouchdown_RecoveredAfterTheTouchdown_LeftAsCommitted()
        {
            Recording booster = F.PredictedTouchdownBooster();
            F.CommitTreeWith(F.Core(), booster);
            double afterTouchdown = booster.EndUT + 100.0;

            Assert.Equal(PredictedEndingRecoveryOutcome.AfterPredictedEnd,
                PredictedEndingRecovery.DecideRecovery(booster, F.BoosterGuid, afterTouchdown, ownedElsewhere: false));
            Assert.Equal(0, ApplyRecovery(F.BoosterGuid, afterTouchdown));
            Assert.Same(booster, RecordingStore.FindRecordingById(F.BoosterId));
            Assert.Contains(logLines, l => l.Contains("reason=AfterPredictedEnd"));
        }

        // A confirmed crash stays final after its UT too.
        [Fact]
        public void ConfirmedCrash_RecoveryAfterItsUT_IsNeverApplied()
        {
            Recording stage = ChutedStagePredictedToCrash();
            stage.StampTerminalState(TerminalState.Destroyed, "test-confirmed");
            F.CommitTreeWith(F.Core(), stage);

            Assert.Equal(PredictedEndingRecoveryOutcome.ConfirmedEnding,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, 2000.0, ownedElsewhere: false));
            Assert.Equal(0, ApplyRecovery(StageGuid, 2000.0, new[] { Jeb }));
            Assert.Same(stage, RecordingStore.FindRecordingById(StageId));
        }

        [Fact]
        public void IsRecoveryInsidePredictedEnding_CrashAlways_TouchdownBeforeItsEnd()
        {
            Recording crash = ChutedStagePredictedToCrash();
            Assert.True(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(crash, 1100.0));
            Assert.True(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(crash, 1300.0));
            Assert.True(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(crash, 1.0e7));
            Assert.False(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(crash, double.NaN));
            Recording touchdown = F.PredictedTouchdownBooster();
            Assert.True(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(touchdown, touchdown.EndUT - 1.0));
            Assert.False(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(touchdown, touchdown.EndUT));
            Assert.False(PredictedEndingRecovery.IsRecoveryInsidePredictedEnding(null, 1100.0));
        }

        // catches: the crash pass reading a vessel the player recovered (the recovery could
        // not convert the ending: a Re-Fly session was running) as "confirmed by stock".
        [Fact]
        public void CrashPass_VesselGoneWithARecoveryRow_IsRemovedByPlayerNotGone()
        {
            Recording stage = ChutedStagePredictedToCrash();
            F.CommitTreeWith(F.Core(), stage);
            Assert.False(EffectiveState.HasRecoveryOrTerminationRowLive(stage));
            Ledger.AddAction(new GameAction
            {
                UT = 1500.0, Type = GameActionType.VesselRecovered, RecordingId = StageId, Sequence = 77,
            });
            EffectiveState.ResetCachesForTesting();
            Assert.True(EffectiveState.HasRecoveryOrTerminationRowLive(stage));
            // the spawn-side predicate refuses a Destroyed terminal on purpose
            Assert.False(EffectiveState.IsRecoveredByLedgerRowLive(stage));

            var pass = PredictedCrashReconciler.EvaluatePass(
                new List<Recording> { stage }, null, r => default(PredictedCrashLiveFacts), 2000.0, true, 1.0,
                new Dictionary<string, double>(), null, EffectiveState.HasRecoveryOrTerminationRowLive);

            Assert.Empty(pass.Gone);
            Assert.Empty(pass.Contradicted);
            Assert.Equal(1, pass.Count(PredictedCrashReconcileOutcome.RemovedByPlayer));
            Assert.Equal(0, pass.Count(PredictedCrashReconcileOutcome.VesselGone));
            string summary = PredictedCrashReconciler.FormatPassSummary(pass, "TRACKSTATION", 2000.0, 0, 0);
            Assert.Contains("vesselGone=0", summary);
            Assert.Contains("removedByPlayer=1", summary);
        }

        [Fact]
        public void CrashPass_TerminationRowCounts_AndNoRowStaysGone()
        {
            Recording stage = ChutedStagePredictedToCrash();
            var candidate = PredictedCrashReconciler.BuildCandidate(stage, null, r => true);
            Assert.True(candidate.RemovedByPlayer);
            Assert.Equal(PredictedCrashReconcileOutcome.RemovedByPlayer,
                PredictedCrashReconciler.Decide(candidate, default(PredictedCrashLiveFacts), 2000.0, true, false, 0.0));
            // a live vessel is still judged as before (the recovery row never hides a vessel stock has)
            var live = new PredictedCrashLiveFacts { Found = true, LandedOrSplashed = true };
            Assert.Equal(PredictedCrashReconcileOutcome.AwaitingConfirmation,
                PredictedCrashReconciler.Decide(candidate, live, 2000.0, true, false, 0.0));
            var plain = PredictedCrashReconciler.BuildCandidate(stage, null);
            Assert.False(plain.RemovedByPlayer);
            Assert.Equal(PredictedCrashReconcileOutcome.VesselGone,
                PredictedCrashReconciler.Decide(plain, default(PredictedCrashLiveFacts), 2000.0, true, false, 0.0));

            Ledger.AddAction(new GameAction
            {
                UT = 1500.0, Type = GameActionType.VesselTerminated, RecordingId = StageId, Sequence = 78,
            });
            EffectiveState.ResetCachesForTesting();
            Assert.True(EffectiveState.HasRecoveryOrTerminationRowLive(stage));
            Assert.False(EffectiveState.HasRecoveryOrTerminationRowLive(null));
        }

        // ================================================================== 2. recovered seat

        // catches: Jeb moved by IVA from the pod (100) to the cabin (200), the pod later
        // exploded, the player recovered the vessel with Jeb in it: the recovery dropped the
        // end snapshot, the per-part rule read his START seat and called him Dead.
        [Fact]
        public void Recovered_CrewTransferredOutOfADestroyedPart_ReadsRecovered()
        {
            Recording rec = SeatedLander(endPodCrew: null, endCabinCrew: Jeb);
            rec.PartEvents.Add(new PartEvent { ut = 300, partPersistentId = 100u, eventType = PartEventType.Destroyed });

            ParsekScenario.ApplyTerminalEventStamp(rec, TerminalState.Recovered, 500.0, "test", new[] { Jeb });
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Null(rec.VesselSnapshot);
            Assert.Equal(new List<string> { Jeb }, rec.RecoveredCrewNames);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            Assert.Contains(logLines, l => l.Contains("stock recovered him"));
        }

        // Review B2 stays: Bob sat in the pod, the pod exploded and killed him (stock did not
        // recover him); Jeb in the cabin came home.
        [Fact]
        public void Recovered_KerbalStockDidNotRecover_WhosePartDied_StaysDead()
        {
            Recording rec = SeatedLander(endPodCrew: Bob, endCabinCrew: Jeb, startPodCrew: Bob, startCabinCrew: Jeb);
            rec.PartEvents.Add(new PartEvent { ut = 300, partPersistentId = 100u, eventType = PartEventType.Destroyed });

            ParsekScenario.ApplyTerminalEventStamp(rec, TerminalState.Recovered, 500.0, "test", new[] { Jeb });
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Bob]);
        }

        // No recovered crew known (an older recording, or the crew could not be read): the
        // per-part rule reads the start seat, today's answer.
        [Fact]
        public void Recovered_NoRecoveredCrewKnown_FallsBackToTheStartSeat()
        {
            Recording rec = SeatedLander(endPodCrew: null, endCabinCrew: Jeb);
            rec.PartEvents.Add(new PartEvent { ut = 300, partPersistentId = 100u, eventType = PartEventType.Destroyed });

            ParsekScenario.ApplyTerminalEventStamp(rec, TerminalState.Recovered, 500.0, "test");
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Null(rec.RecoveredCrewNames);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
        }

        // The committed Q13 path carries the crew too: a predicted crash of a vessel whose
        // kerbal moved out of a part that later died, recovered by the player.
        [Fact]
        public void CommittedPredictedCrash_Recovered_CarriesTheRecoveredCrew()
        {
            Recording stage = ChutedStagePredictedToCrash();
            stage.GhostVisualSnapshot = Seats(Jeb, null);
            stage.VesselSnapshot = Seats(null, Jeb);
            stage.PartEvents.Add(new PartEvent { ut = 940, partPersistentId = 100u, eventType = PartEventType.Destroyed });
            stage.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            F.CommitTreeWith(F.Core(), stage);

            Assert.Equal(1, ApplyRecovery(StageGuid, 1100.0, new[] { Jeb }));

            Recording after = RecordingStore.FindRecordingById(StageId);
            Assert.Equal(TerminalState.Recovered, after.TerminalStateValue);
            Assert.Equal(KerbalEndState.Recovered, after.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Recovered, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
        }

        // The flight-scene Recover carries the live crew read when the request armed.
        [Fact]
        public void InFlightRecoveryRequest_CarriesTheCrewOntoTheStamp()
        {
            Recording rec = SeatedLander(endPodCrew: null, endCabinCrew: Jeb);
            rec.RecordedVesselGuid = StageGuid;
            rec.PartEvents.Add(new PartEvent { ut = 300, partPersistentId = 100u, eventType = PartEventType.Destroyed });
            var tree = new RecordingTree { Id = "t-seat", TreeName = "Seat", RootRecordingId = rec.RecordingId };
            tree.AddOrReplaceRecording(rec);
            try
            {
                InFlightRecoveryRequest.Arm(rec.VesselPersistentId, StageGuid, rec.VesselName, 500.0,
                    rec.RecordingId, 0f, new[] { Jeb, "" });
                Assert.Equal(new List<string> { Jeb }, InFlightRecoveryRequest.Armed.CrewNames);

                Assert.Equal(1, InFlightRecoveryRequest.StampRecovered(tree, InFlightRecoveryRequest.Armed, "test"));
                KerbalsModule.PopulateCrewEndStates(rec);

                Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
                Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            }
            finally
            {
                InFlightRecoveryRequest.ResetForTesting();
            }
        }

        // The field survives a save / load and a deep clone, and leaves with the Recovered
        // terminal (another stamp drops it; a non-Recovered recording never reads it).
        [Fact]
        public void RecoveredCrew_RoundTripsAndLeavesWithTheTerminal()
        {
            Recording rec = SeatedLander(endPodCrew: null, endCabinCrew: Jeb);
            ParsekScenario.ApplyTerminalEventStamp(rec, TerminalState.Recovered, 500.0, "test", new[] { Jeb, Bob });

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            ConfigNode crewNode = node.GetNode(RecordingTreeRecordCodec.RecoveredCrewNodeName);
            Assert.NotNull(crewNode);
            Assert.Equal(new[] { Jeb, Bob }, crewNode.GetValues(RecordingTreeRecordCodec.RecoveredCrewValueName));
            var loaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.Equal(new List<string> { Jeb, Bob }, loaded.RecoveredCrewNames);

            Recording clone = Recording.DeepClone(rec);
            Assert.Equal(new List<string> { Jeb, Bob }, clone.RecoveredCrewNames);
            Assert.NotSame(rec.RecoveredCrewNames, clone.RecoveredCrewNames);

            // an empty list (stock recovered nobody aboard) is known, not unknown
            Recording empty = SeatedLander(endPodCrew: null, endCabinCrew: Jeb);
            ParsekScenario.ApplyTerminalEventStamp(empty, TerminalState.Recovered, 500.0, "test", new string[0]);
            var emptyNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(emptyNode, empty);
            var emptyLoaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(emptyNode, emptyLoaded);
            Assert.NotNull(emptyLoaded.RecoveredCrewNames);
            Assert.Empty(emptyLoaded.RecoveredCrewNames);

            rec.StampTerminalState(TerminalState.Landed, "test");
            Assert.Null(rec.RecoveredCrewNames);
            var landedNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(landedNode, rec);
            Assert.Null(landedNode.GetNode(RecordingTreeRecordCodec.RecoveredCrewNodeName));

            // a recording without the key reads unknown
            var plainNode = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(plainNode, SeatedLander(null, Jeb));
            var plainLoaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(plainNode, plainLoaded);
            Assert.Null(plainLoaded.RecoveredCrewNames);
        }

        // Jeb starts in the pod (100); the end snapshot says where he and Bob ended up.
        private static Recording SeatedLander(
            string endPodCrew, string endCabinCrew, string startPodCrew = Jeb, string startCabinCrew = null)
        {
            var rec = new Recording
            {
                RecordingId = "rec-lander", TreeId = "t-seat", VesselName = "Lander", VesselPersistentId = 9u,
                ExplicitStartUT = 0, ExplicitEndUT = 400,
                GhostVisualSnapshot = Seats(startPodCrew, startCabinCrew),
                VesselSnapshot = Seats(endPodCrew, endCabinCrew),
            };
            rec.Points.Add(new TrajectoryPoint { ut = 0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 400, bodyName = "Kerbin" });
            return rec;
        }

        // ================================================================== 3. Q12b, SubOrbital

        // Q12b negative: Parsek never invents a reputation number. A predicted crash with no
        // stock VesselLoss reputation event books the deaths but no reputation penalty row.
        [Fact]
        public void PredictedCrash_WithNoStockVesselLossEvent_BooksNoReputationPenalty()
        {
            Recording stage = ChutedStagePredictedToCrash();
            F.CommitTreeWith(F.Core(), stage);

            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(1400.0, "test");
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
        }

        // Owner ruling 2026-11-22: a crewed stage whose low point sits among the peaks and whose
        // ground scan found no crash ends Orbiting where the scan stopped checking (predicted,
        // groundScanEnd) and spawns there with its crew aboard; from then on stock decides. The
        // crew stay reserved open-ended as on any orbit ending, because they are aboard the
        // spawned vessel. Before the ruling the same stage ended SubOrbital at the horizon and
        // never spawned, so its crew were held aboard nothing.
        [Fact]
        public void CrewedGroundScanEndOrbit_CrewAboard_SpawnsOnItsOrbit()
        {
            var rec = new Recording
            {
                RecordingId = StageId, TreeId = F.TreeId, VesselName = "Mun Skimmer",
                VesselPersistentId = StagePid, RecordedVesselGuid = StageGuid,
                VesselSnapshot = F.CrewSnapshot(Jeb),
                GhostVisualSnapshot = F.CrewSnapshot(Jeb),
                ExplicitStartUT = 900.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Mun", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 960.0, bodyName = "Mun", altitude = 12000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 960.0, endUT = 960.0 + 0.002 * BallisticExtrapolator.SecondsPerYear, bodyName = "Mun",
                semiMajorAxis = 212500, eccentricity = 0.035, isPredicted = true,
            });
            rec.StampTerminalState(TerminalState.Orbiting, "test-ground-scan-end",
                predicted: true, groundScanEnd: true);
            rec.ExplicitEndUT = 960.0 + 0.002 * BallisticExtrapolator.SecondsPerYear;
            Assert.True(rec.TerminalPredicted);
            Assert.True(rec.GroundScanEndOrbit);
            F.CommitTreeWith(F.Core(), rec);

            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates[Jeb]);
            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
            double clock = rec.EndUT + 1000.0;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;
            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(clock, "test");
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));
            Assert.True(Kerbals.IsReservedNow(Jeb));
        }
    }
}
