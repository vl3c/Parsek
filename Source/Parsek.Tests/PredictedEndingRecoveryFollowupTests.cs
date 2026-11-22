using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using Xunit;
using F = Parsek.Tests.PredictedTouchdownStockFateTests;

namespace Parsek.Tests
{
    /// <summary>
    /// Three follow-ups to the predicted touchdown / predicted crash work (owner rulings
    /// P-STOCK, Q6c, Q13):
    /// <list type="number">
    /// <item>A recovery of a predicted-touchdown craft before its predicted touchdown ends
    /// the crew hold at the recovery (the hold is measured from the predicted descent's start,
    /// as the recovery owner pick already is), for predicted endings only.</item>
    /// <item>A recovery, after the commit and before the predicted UT, of a stage whose crash
    /// (or chute touchdown) Parsek only predicted is stock's real outcome: the committed
    /// recording ends Recovered, its crew Recovered, the death rows and penalty retired. A
    /// confirmed crash stays final, and a recovery after the predicted UT changes nothing.</item>
    /// <item>The spawn-death check does not count a death (or reset for a re-spawn) for an
    /// adopted predicted touchdown stock deleted: the end-spawn gate refuses it, so the check
    /// asks the same gate first and clears the claim with the reason, once.</item>
    /// </list>
    /// </summary>
    [Collection("Sequential")]
    public class PredictedEndingRecoveryFollowupTests : IDisposable
    {
        private const string Jeb = F.Jeb;
        private const string StageId = "rec-stage";
        private const uint StagePid = 4343u;
        private const string StageGuid = "fedcba9876543210fedcba9876543210";
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppressCrewEvents;

        public PredictedEndingRecoveryFollowupTests()
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

        // A crewed stage Parsek stopped watching at UT 960, predicted to crash at UT 1300
        // (Q6c: Jeb dies with it).
        private static Recording CrewedPredictedCrashStage(string guid = StageGuid)
        {
            var rec = new Recording
            {
                RecordingId = StageId, TreeId = F.TreeId, VesselName = "Crewed Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = guid,
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

        private static int ApplyRecovery(string guid, double recoveryUT, Func<Recording, bool> ownedElsewhere = null)
        {
            return ParsekScenario.ApplyRecoveryOfCommittedPredictions(
                EffectiveState.ComputeERS(), guid, recoveryUT, ownedElsewhere, "test");
        }

        // ================================================================== 1. crew hold

        // catches: the recovery before the predicted touchdown writing no KerbalRecovered row
        // (BuildClosureRows judged the hold by the touchdown UT), so Jeb stays reserved
        // open-ended after stock gave him back.
        [Fact]
        public void HoldOfAPredictedTouchdown_RecoveredBeforeTheTouchdown_ClosesAtTheRecovery()
        {
            Recording booster = F.PredictedTouchdownBooster();
            Assert.True(booster.EndUT > 1090.0);
            F.CommitTreeWith(F.Core(), booster);
            double clock = 1040.0;
            KerbalsModule.LiveClockUTProviderForTesting = () => clock;
            LedgerOrchestrator.RecalculateAndPatch();
            Assert.True(double.IsPositiveInfinity(Kerbals.Reservations[Jeb].ReservedUntilUT));

            int written = LedgerOrchestrator.OnRealVesselCrewRecovered(
                1040.0, F.BoosterPid, F.BoosterGuid, "Crewed Booster", new List<string> { Jeb });

            Assert.Equal(1, written);
            Assert.Single(Rows(GameActionType.KerbalRecovered, F.BoosterId));
            Assert.Equal(1040.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }

        // catches: the walk still measuring the hold against the touchdown UT, so the
        // recovery row (written by an earlier build or another path) leaves the hold open.
        [Fact]
        public void Walk_RecoveryRowBeforeThePredictedTouchdown_EndsTheHold()
        {
            Recording booster = F.PredictedTouchdownBooster();
            F.CommitTreeWith(F.Core(), booster);
            Ledger.AddAction(new GameAction
            {
                UT = 1040.0, Type = GameActionType.KerbalRecovered, RecordingId = F.BoosterId,
                KerbalName = Jeb, KerbalRole = "Pilot", Sequence = 99,
            });

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(1050.0, "test");

            Assert.Equal(1040.0, Kerbals.Reservations[Jeb].ReservedUntilUT);
            Assert.False(Kerbals.IsReservedNow(Jeb));
        }

        // Real endings unchanged: a flown landing that ends at 1100 is not ended by a
        // recovery at 1040 (that recovery is of an earlier, other vessel state).
        [Fact]
        public void RealLanding_RecoveryBeforeItsEnd_KeepsTheHold()
        {
            Recording booster = F.PredictedTouchdownBooster();
            booster.StampTerminalState(TerminalState.Landed, "test-flown");
            Assert.False(booster.TerminalPredicted);
            F.CommitTreeWith(F.Core(), booster);

            int written = LedgerOrchestrator.OnRealVesselCrewRecovered(
                1040.0, F.BoosterPid, F.BoosterGuid, "Crewed Booster", new List<string> { Jeb });

            Assert.Equal(0, written);
            Assert.Equal(booster.EndUT, KerbalsModule.RecoveryHoldEndUT(booster), 6);
        }

        [Fact]
        public void RecoveryHoldEndUT_PredictedTouchdownReadsTheDescentStart_OthersTheirEnd()
        {
            Recording booster = F.PredictedTouchdownBooster();
            Assert.Equal(1000.0, KerbalsModule.RecoveryHoldEndUT(booster), 6);
            Assert.Equal(KerbalsModule.RecoveryHoldEndUT(booster),
                CrewRecoveryReservationClose.OwnerEndUT(booster), 6);

            Recording crash = CrewedPredictedCrashStage();
            Assert.Equal(crash.EndUT, KerbalsModule.RecoveryHoldEndUT(crash), 6);
            Assert.True(double.IsNaN(KerbalsModule.RecoveryHoldEndUT(null)));
        }

        // ================================================================== 2. Q13 committed

        // catches: a committed predicted crash whose stage the player recovers before the
        // crash UT keeping the crash (Jeb dead, the death penalty booked) and writing no
        // recovery row (the recording "ends" after the recovery).
        [Fact]
        public void CommittedPredictedCrash_RecoveredBeforeTheCrash_EndsRecovered_CrewLive()
        {
            AddVesselLossEvent(StageId, 1300.0);
            Recording stage = CrewedPredictedCrashStage();
            F.CommitTreeWith(F.Core(), stage);
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Single(Rows(GameActionType.ReputationPenalty, StageId));

            Assert.Equal(1, ApplyRecovery(StageGuid, 1100.0));

            Recording after = RecordingStore.FindRecordingById(StageId);
            Assert.NotSame(stage, after);
            Assert.Equal(TerminalState.Recovered, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(after.VesselDestroyed);
            Assert.Equal(1100.0, after.EndUT, 6);
            Assert.Equal(KerbalEndState.Recovered, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Recovered, rows[0].KerbalEndStateField);
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
            // the committed copy the pass read was never written
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(after, false).needsSpawn);

            // the recovery row writer that runs next now finds the recording as the owner
            Assert.Equal(1, LedgerOrchestrator.OnRealVesselRecovered(1100.0, StagePid, StageGuid, "Crewed Stage"));
            Assert.Single(Rows(GameActionType.VesselRecovered, StageId));

            LedgerOrchestrator.RecalculateAndPatchForCurrentTimelineUT(1200.0, "test");
            Assert.False(Kerbals.IsReservedNow(Jeb));
            Assert.Contains(logLines, l => l.Contains("[" + PredictedEndingRecovery.LogTag + "]")
                && l.Contains("Predicted crash recovered by the player") && l.Contains(StageId));
        }

        // catches: the touchdown kept (crew Aboard until the touchdown, the replay spawning a
        // craft the player already recovered).
        [Fact]
        public void CommittedPredictedTouchdown_RecoveredBeforeTheTouchdown_EndsRecovered()
        {
            Recording booster = F.PredictedTouchdownBooster();
            F.CommitTreeWith(F.Core(), booster);

            Assert.Equal(1, ApplyRecovery(F.BoosterGuid, 1040.0));

            Recording after = RecordingStore.FindRecordingById(F.BoosterId);
            Assert.Equal(TerminalState.Recovered, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(PredictedTouchdownStockFate.HasPredictedDescentSection(after));
            Assert.Equal(KerbalEndState.Recovered, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, F.BoosterId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Recovered, rows[0].KerbalEndStateField);
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(after, false).needsSpawn);
            Assert.Contains(logLines, l => l.Contains("Predicted touchdown recovered by the player"));
        }

        // A confirmed crash stays final.
        [Fact]
        public void ConfirmedCrash_IsNeverRecovered()
        {
            Recording stage = CrewedPredictedCrashStage();
            stage.StampTerminalState(TerminalState.Destroyed, "test-confirmed");
            Assert.False(stage.TerminalPredicted);
            F.CommitTreeWith(F.Core(), stage);

            Assert.Equal(0, ApplyRecovery(StageGuid, 1100.0));

            Assert.Same(stage, RecordingStore.FindRecordingById(StageId));
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Equal(PredictedEndingRecoveryOutcome.ConfirmedEnding,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, 1100.0, ownedElsewhere: false));
        }

        // Owner ruling Q13b: a recovery at or after the predicted crash UT is a vessel that
        // survived (stock landed it after the drag-free crash UT): it overrides the predicted
        // Destroyed like a recovery before it. (A predicted touchdown recovered after its
        // touchdown still changes nothing: RecoveryOverridesPredictedCrashTests.)
        [Theory]
        [InlineData(1300.0)]
        [InlineData(1500.0)]
        public void RecoveryAtOrAfterThePredictedCrash_EndsRecovered(double recoveryUT)
        {
            Recording stage = CrewedPredictedCrashStage();
            F.CommitTreeWith(F.Core(), stage);
            Assert.Equal(PredictedEndingRecoveryOutcome.Recover,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, recoveryUT, ownedElsewhere: false));

            Assert.Equal(1, ApplyRecovery(StageGuid, recoveryUT));

            Recording after = RecordingStore.FindRecordingById(StageId);
            Assert.NotSame(stage, after);
            Assert.Equal(TerminalState.Recovered, after.TerminalStateValue);
            Assert.Equal(recoveryUT, after.EndUT, 6);
            Assert.Equal(KerbalEndState.Recovered, after.CrewEndStates[Jeb]);
            Assert.DoesNotContain(logLines, l => l.Contains("reason=AfterPredictedEnd"));
        }

        [Fact]
        public void DecideRecovery_GuardRows()
        {
            Recording stage = CrewedPredictedCrashStage();
            Assert.Equal(PredictedEndingRecoveryOutcome.Recover,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, 1100.0, false));
            // launch guid only: another launch, an unknown live guid, an unknown recorded guid
            Assert.Equal(PredictedEndingRecoveryOutcome.NotMatchingLaunch,
                PredictedEndingRecovery.DecideRecovery(stage, F.BoosterGuid, 1100.0, false));
            Assert.Equal(PredictedEndingRecoveryOutcome.NotMatchingLaunch,
                PredictedEndingRecovery.DecideRecovery(stage, null, 1100.0, false));
            Assert.Equal(PredictedEndingRecoveryOutcome.NoLaunchGuid,
                PredictedEndingRecovery.DecideRecovery(CrewedPredictedCrashStage(guid: null), StageGuid, 1100.0, false));
            // a live session owns the tree
            Assert.Equal(PredictedEndingRecoveryOutcome.OwnedElsewhere,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, 1100.0, true));
            // before the recorded flight's last sample: not this timeline's vessel
            Assert.Equal(PredictedEndingRecoveryOutcome.BeforeRecordedFlight,
                PredictedEndingRecovery.DecideRecovery(stage, StageGuid, 920.0, false));
            Assert.Equal(PredictedEndingRecoveryOutcome.Recover,
                PredictedEndingRecovery.DecideRecovery(StageWithGuidCase(), StageGuid, 1100.0, false));

            // a predicted final orbit (Q8) is not a crash or touchdown
            Recording orbit = CrewedPredictedCrashStage();
            orbit.StampTerminalState(TerminalState.Orbiting, "test", predicted: true);
            Assert.Equal(PredictedEndingRecoveryOutcome.NotPredictedEnding,
                PredictedEndingRecovery.DecideRecovery(orbit, StageGuid, 1100.0, false));
            Assert.Equal(PredictedEndingRecoveryOutcome.NotPredictedEnding,
                PredictedEndingRecovery.DecideRecovery(null, StageGuid, 1100.0, false));
        }

        private static Recording StageWithGuidCase()
        {
            return CrewedPredictedCrashStage(guid: StageGuid.ToUpperInvariant());
        }

        [Fact]
        public void OwnedElsewhere_IsLeftToTheSession()
        {
            Recording stage = CrewedPredictedCrashStage();
            F.CommitTreeWith(F.Core(), stage);

            Assert.Equal(0, ApplyRecovery(StageGuid, 1100.0, rec => rec.TreeId == F.TreeId));
            Assert.Same(stage, RecordingStore.FindRecordingById(StageId));
        }

        // The live hook: the recovery handler converts a committed prediction BEFORE the
        // ledger's recovery rows are written, so the row writers see the Recovered ending.
        [Fact]
        public void OnVesselRecovered_ConvertsCommittedPredictionsBeforeTheLedgerRows()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot, "Source", "Parsek", "Controllers",
                "ParsekScenario.VesselLifecycle.cs");
            string text = File.ReadAllText(path);
            int handler = text.IndexOf("private void OnVesselRecovered(ProtoVessel pv, bool quick)", StringComparison.Ordinal);
            Assert.True(handler >= 0);
            int convert = text.IndexOf("ReconcileRecoveredCommittedPredictions(", handler, StringComparison.Ordinal);
            int funds = text.IndexOf("LedgerOrchestrator.OnVesselRecoveryFunds(", handler, StringComparison.Ordinal);
            int crewClose = text.IndexOf("CloseCrewReservationsForRecoveredVessel(pv, identity, now)", handler, StringComparison.Ordinal);
            int vesselRow = text.IndexOf("LedgerOrchestrator.OnRealVesselRecovered(", handler, StringComparison.Ordinal);
            Assert.True(convert > handler, "the handler converts committed predictions");
            Assert.True(convert < funds && convert < crewClose && convert < vesselRow,
                "the conversion runs before the ledger rows");
        }

        // ================================================================== 3. spawn-death noise

        private static ParsekPlaybackPolicy NewPolicy()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            return new ParsekPlaybackPolicy(new GhostPlaybackEngine(null), host);
        }

        // catches: the adopted craft stock deleted counted as a spawn death and "reset for
        // re-spawn" every scene (the gate then refuses the very spawn the reset asked for).
        [Fact]
        public void SpawnDeathCheck_AdoptedPredictedTouchdownStockDeleted_CountsNoDeath()
        {
            Recording booster = F.PredictedTouchdownBooster();
            booster.StockHeldVesselUT = 1000.0;
            booster.VesselSpawned = true;
            booster.SpawnedVesselPersistentId = booster.VesselPersistentId;
            booster.PlaybackEnabled = true;
            F.CommitTreeWith(F.Core(), booster);
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = _ => false;

            ParsekPlaybackPolicy policy = NewPolicy();
            policy.RunSpawnDeathChecks();
            policy.RunSpawnDeathChecks();

            Assert.Equal(0, booster.SpawnDeathCount);
            Assert.False(booster.VesselSpawned);
            Assert.Equal(0u, booster.SpawnedVesselPersistentId);
            Assert.False(booster.SpawnAbandoned);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn-death detected"));
            Assert.Single(logLines.FindAll(l => l.Contains("[Policy]")
                && l.Contains("Spawned vessel deleted by stock") && l.Contains("predicted touchdown")));
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(booster, false).needsSpawn);
        }

        // A genuine spawn death of the same shape without the stock stamp still counts.
        [Fact]
        public void SpawnDeathCheck_PredictedTouchdownNeverHeldByStock_StillCountsADeath()
        {
            Recording booster = F.PredictedTouchdownBooster();
            booster.VesselSpawned = true;
            booster.SpawnedVesselPersistentId = 777777u;
            booster.PlaybackEnabled = true;
            F.CommitTreeWith(F.Core(), booster);
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = _ => false;

            NewPolicy().RunSpawnDeathChecks();

            Assert.Equal(1, booster.SpawnDeathCount);
            Assert.Contains(logLines, l => l.Contains("Spawn-death detected"));
        }
    }
}
