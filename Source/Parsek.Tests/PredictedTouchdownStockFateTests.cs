using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings P-STOCK / Q4c / Q4d: a chute touchdown Parsek predicted while the craft
    /// was still in the game is a forecast. When stock later deletes the craft in the air
    /// (Vessel.CheckKill: no terminal event reaches the committed recording) the recording
    /// ends as stock decided (Destroyed, crew dead, a quiet end after the descent, the slot
    /// open as for a crash) and nothing spawns it again; a craft stock keeps in the air past
    /// the touchdown drops the prediction; a craft that came down agrees and is left alone.
    /// A rewind to before the flight still spawns the craft at its touchdown.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedTouchdownStockFateTests : IDisposable
    {
        internal const string Jeb = "Jebediah Kerman";
        internal const string TreeId = "tree-touchdown";
        internal const string CoreId = "rec-core";
        internal const string BoosterId = "rec-booster";
        internal const uint BoosterPid = 4242u;
        internal const string BoosterGuid = "0123456789abcdef0123456789abcdef";
        private readonly List<string> logLines = new List<string>();

        public PredictedTouchdownStockFateTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.ResetRateLimitsForTesting();
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
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            PredictedTouchdownStockFate.ResetForTesting();
        }

        public void Dispose()
        {
            PredictedTouchdownStockFate.ResetForTesting();
            RecordingFinalizationCacheProducer.ResetForTesting();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
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

        // ------------------------------------------------------------------ fixtures

        internal static ConfigNode CrewSnapshot(params string[] crew)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "FLYING");
            snapshot.AddValue("landed", "False");
            snapshot.AddValue("splashed", "False");
            snapshot.AddValue("lat", "-0.1");
            snapshot.AddValue("lon", "-74.5");
            snapshot.AddValue("alt", "1850");
            var part = snapshot.AddNode("PART");
            for (int i = 0; i < crew.Length; i++)
                part.AddValue("crew", crew[i]);
            return snapshot;
        }

        // A crewed booster recorded down to 770 m under open chutes, its touchdown predicted
        // through the production applier (Landed at ~1100, a Predicted descent section).
        internal static Recording PredictedTouchdownBooster(string id = BoosterId, string guid = BoosterGuid)
        {
            var rec = new Recording
            {
                RecordingId = id,
                TreeId = TreeId,
                VesselName = "Crewed Booster",
                VesselPersistentId = BoosterPid,
                RecordedVesselGuid = guid,
                ExplicitStartUT = 960.0,
            };
            var frames = new List<TrajectoryPoint>();
            for (int i = 0; i <= 4; i++)
            {
                frames.Add(new TrajectoryPoint
                {
                    ut = 960.0 + i * 10.0,
                    latitude = -0.1,
                    longitude = -74.5,
                    altitude = 1050.0 - i * 70.0,
                    rotation = Quaternion.identity,
                    bodyName = "Kerbin",
                    recordedGroundClearance = double.NaN,
                });
            }
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = 960.0,
                endUT = 1000.0,
                frames = new List<TrajectoryPoint>(frames),
                source = TrackSectionSource.Background,
                sampleRateHz = 0.1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
            });
            rec.Points.AddRange(frames);
            rec.VesselSnapshot = CrewSnapshot(Jeb);
            rec.GhostVisualSnapshot = CrewSnapshot(Jeb);

            var input = new ParachuteDescentInput
            {
                BodyName = "Kerbin",
                StartUT = 1000.0,
                Latitude = -0.1,
                Longitude = -74.5,
                Altitude = 770.0,
                VerticalSpeed = -7.0,
                Canopy = ParachuteCanopyState.Deployed,
                FullDeployAltitudeAgl = double.NaN,
                TerrainAltitude = 70.0,
                BodyHasOcean = true,
                InAtmosphere = true,
                RootHeightAboveLowestPoint = double.NaN,
                SurfaceRelativeRotation = Quaternion.identity,
                WorldUp = new Vector3(0f, 1f, 0f),
            };
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryBuildParachuteDescentFinalizationResult(
                id, BoosterPid, input, out IncompleteBallisticFinalizationResult result));
            var cache = new RecordingFinalizationCache
            {
                RecordingId = id,
                VesselPersistentId = BoosterPid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                RefreshReason = "test",
                TerminalState = result.terminalState,
                TerminalUT = result.terminalUT,
                TerminalBodyName = "Kerbin",
                TerminalPosition = result.terminalPosition,
                TerrainHeightAtEnd = result.terrainHeightAtEnd,
                PredictedDescentPoints = result.predictedDescentPoints,
                TailStartsAtUT = 1000.0,
            };
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, cache,
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out _));
            Assert.True(PredictedTouchdownStockFate.IsPredictedTouchdown(rec));
            return rec;
        }

        internal static Recording Core()
        {
            var rec = new Recording
            {
                RecordingId = CoreId, TreeId = TreeId, VesselName = "Core", VesselPersistentId = 5u,
                ExplicitStartUT = 900.0, ExplicitEndUT = 1000.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 1000.0, bodyName = "Kerbin", altitude = 80000 });
            rec.StampTerminalState(TerminalState.Orbiting, "test");
            return rec;
        }

        internal static RecordingTree CommitTreeWith(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Touchdown", RootRecordingId = CoreId, ActiveRecordingId = CoreId,
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

        private static PredictedTouchdownCandidateFacts Candidate(double touchdownUT = 1100.0, double stockHeld = 1000.0)
        {
            return new PredictedTouchdownCandidateFacts
            {
                IsPredictedTouchdown = true, HasLaunchGuid = true, TouchdownUT = touchdownUT, StockHeldUT = stockHeld,
            };
        }

        private static PredictedCrashLiveFacts LiveAirborneOnRails()
        {
            // Kerbin-like: a conic well clear of the 1 kPa line (stock keeps it on rails).
            return new PredictedCrashLiveFacts
            {
                Found = true, Eccentricity = 0.1, PeriapsisAltitude = 80000.0, StockRemovalAltitude = 37000.0,
            };
        }

        private static PredictedTouchdownOutcome DecideConfirmed(
            PredictedTouchdownCandidateFacts c, PredictedCrashLiveFacts live, double nowUT = 1050.0)
        {
            return PredictedTouchdownStockFate.Decide(c, live, nowUT, sceneSettled: true,
                observedBefore: true, secondsSinceFirstObservation: 5.0);
        }

        // ------------------------------------------------------------------ the decision table

        // (b) catches: a stock-deleted chuted craft left Landed (respawned later, crew alive),
        // and the deletion only acted on after the touchdown UT (stock decides at deletion).
        [Theory]
        [InlineData(1050.0)]   // before the touchdown
        [InlineData(1500.0)]   // after it
        public void Decide_GoneAfterStockHeldIt_IsStockDeleted(double nowUT)
        {
            Assert.Equal(PredictedTouchdownOutcome.StockDeleted,
                DecideConfirmed(Candidate(), default(PredictedCrashLiveFacts), nowUT));
        }

        // catches: a single empty vessel list (a scene transient) read as a deletion
        [Fact]
        public void Decide_GoneFirstSighting_AwaitsConfirmation()
        {
            Assert.Equal(PredictedTouchdownOutcome.AwaitingConfirmation,
                PredictedTouchdownStockFate.Decide(Candidate(), default(PredictedCrashLiveFacts), 1050.0,
                    sceneSettled: true, observedBefore: false, secondsSinceFirstObservation: double.NaN));
        }

        // The rewind discriminator: catches every committed touchdown read as stock-deleted after
        // a rewind to before the flight (the vessel is not in that timeline yet).
        [Fact]
        public void Decide_GoneButNeverHeldInThisTimeline_IsLeftForTheReplaySpawn()
        {
            Assert.Equal(PredictedTouchdownOutcome.NotInThisTimeline,
                DecideConfirmed(Candidate(stockHeld: double.NaN), default(PredictedCrashLiveFacts)));
        }

        // Q13 catches: a recovered or terminated craft read as a stock kill (crew dead)
        [Fact]
        public void Decide_GoneAfterARecoveryRow_IsThePlayersRemoval()
        {
            var c = Candidate();
            c.RemovedByPlayer = true;
            Assert.Equal(PredictedTouchdownOutcome.RemovedByPlayer, DecideConfirmed(c, default(PredictedCrashLiveFacts)));
        }

        // (a) catches: a craft that came down being reconciled (its recorded touchdown moved)
        [Theory]
        [InlineData(1050.0)]
        [InlineData(1500.0)]
        public void Decide_VesselDown_IsConsistent(double nowUT)
        {
            var live = LiveAirborneOnRails();
            live.LandedOrSplashed = true;
            Assert.Equal(PredictedTouchdownOutcome.Consistent, DecideConfirmed(Candidate(), live, nowUT));
        }

        [Fact]
        public void Decide_StillInTheAirBeforeTheTouchdown_StockDecides()
        {
            Assert.Equal(PredictedTouchdownOutcome.StockStillDeciding,
                DecideConfirmed(Candidate(), LiveAirborneOnRails(), 1105.0));
        }

        // (c) catches: a craft stock kept in the air past the touchdown keeping the prediction
        [Fact]
        public void Decide_StillInTheAirPastTheTouchdown_OnRails_IsContradicted()
        {
            Assert.Equal(PredictedTouchdownOutcome.Contradicted,
                DecideConfirmed(Candidate(), LiveAirborneOnRails(), 1200.0));
        }

        [Fact]
        public void Decide_PastTheTouchdown_InPhysicsOrFalling_Waits()
        {
            var physics = LiveAirborneOnRails();
            physics.InPhysics = true;
            Assert.Equal(PredictedTouchdownOutcome.StockStillSimulating, DecideConfirmed(Candidate(), physics, 1200.0));
            var falling = LiveAirborneOnRails();
            falling.PeriapsisAltitude = -500000.0;
            falling.Eccentricity = 0.99;
            Assert.Equal(PredictedTouchdownOutcome.StockMayStillRemove, DecideConfirmed(Candidate(), falling, 1200.0));
        }

        [Fact]
        public void Decide_GuardRows()
        {
            var c = Candidate();
            c.IsPredictedTouchdown = false;
            Assert.Equal(PredictedTouchdownOutcome.NotPredictedTouchdown, DecideConfirmed(c, default(PredictedCrashLiveFacts)));
            c = Candidate();
            c.HasLaunchGuid = false;
            Assert.Equal(PredictedTouchdownOutcome.NoLaunchGuid, DecideConfirmed(c, default(PredictedCrashLiveFacts)));
            c = Candidate();
            c.OwnedElsewhere = true;
            Assert.Equal(PredictedTouchdownOutcome.OwnedElsewhere, DecideConfirmed(c, default(PredictedCrashLiveFacts)));
            c = Candidate();
            c.MaterializedBySpawn = true;
            Assert.Equal(PredictedTouchdownOutcome.MaterializedBySpawn, DecideConfirmed(c, default(PredictedCrashLiveFacts)));
            Assert.Equal(PredictedTouchdownOutcome.SceneNotSettled,
                PredictedTouchdownStockFate.Decide(Candidate(), default(PredictedCrashLiveFacts), 1050.0,
                    sceneSettled: false, observedBefore: true, secondsSinceFirstObservation: 5.0));
            var active = LiveAirborneOnRails();
            active.IsActiveVessel = true;
            Assert.Equal(PredictedTouchdownOutcome.VesselIsActive, DecideConfirmed(Candidate(), active));
        }

        // catches: the adoption of the recorded vessel read as a Parsek spawn (its later
        // deletion by stock then never reconciled)
        [Fact]
        public void MaterializedBySpawn_AdoptionIsNotASpawn()
        {
            Recording rec = PredictedTouchdownBooster();
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = rec.VesselPersistentId;
            Assert.False(PredictedTouchdownStockFate.IsMaterializedBySpawn(rec));
            rec.SpawnedVesselPersistentId = 99887766u;
            Assert.True(PredictedTouchdownStockFate.IsMaterializedBySpawn(rec));
        }

        [Fact]
        public void EvaluatePass_GoneActsOnTheSecondSightingAndSightingsAreStamped()
        {
            Recording rec = PredictedTouchdownBooster();
            rec.StockHeldVesselUT = 1000.0;
            var recs = new List<Recording> { rec };
            var firstObserved = new Dictionary<string, double>();

            var alive = PredictedTouchdownStockFate.EvaluatePass(recs, null, null,
                r => LiveAirborneOnRails(), 1010.0, true, 1.0, firstObserved);
            Assert.Single(alive.Sighted);
            Assert.True(alive.NeedsRecheck);
            Assert.Equal(1, alive.Count(PredictedTouchdownOutcome.StockStillDeciding));

            var first = PredictedTouchdownStockFate.EvaluatePass(recs, null, null,
                r => default(PredictedCrashLiveFacts), 1020.0, true, 10.0, firstObserved);
            Assert.Empty(first.StockDeleted);
            Assert.True(first.AwaitingConfirmation);
            var second = PredictedTouchdownStockFate.EvaluatePass(recs, null, null,
                r => default(PredictedCrashLiveFacts), 1021.0, true, 13.0, firstObserved);
            Assert.Single(second.StockDeleted);
            Assert.Contains("stockDeleted=1",
                PredictedTouchdownStockFate.FormatPassSummary(second, "TRACKSTATION", 1021.0, 1, 0, 0));
        }

        // ------------------------------------------------------------------ the committed change

        // (b) catches: the stock-deleted craft kept Landed with its crew Aboard, the conversion
        // mutating the committed copy in place, the explosion played, the slot closed.
        [Fact]
        public void ApplyStockDeletion_EndsDestroyed_CrewDead_QuietEnd_SlotOpen()
        {
            Recording booster = PredictedTouchdownBooster();
            double touchdownUT = booster.EndUT;
            RecordingTree committed = CommitTreeWith(Core(), booster);
            List<GameAction> before = Rows(GameActionType.KerbalAssignment, BoosterId);
            Assert.Single(before);
            Assert.Equal(KerbalEndState.Aboard, before[0].KerbalEndStateField);

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyStockDeletion(
                committed, BoosterId, 1050.0, "test");

            Assert.True(result.Applied, result.Reason);
            Recording after = RecordingStore.FindRecordingById(BoosterId);
            Assert.NotSame(booster, after);
            Assert.Equal(TerminalState.Destroyed, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.True(after.QuietDestroyedEnd);
            Assert.True(GhostPlaybackLogic.IsQuietDestroyedEnd(after));
            Assert.Equal(touchdownUT, after.EndUT, 6);
            Assert.True(PredictedTouchdownStockFate.HasPredictedDescentSection(after));
            Assert.Null(after.TerminalPosition);
            Assert.Equal(TerminalKind.Crashed, TerminalKindClassifier.Classify(after));
            Assert.Equal(KerbalEndState.Dead, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, BoosterId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);
            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(after, false);
            Assert.False(needsSpawn);
            // the committed copy the pass read was never written
            Assert.Equal(TerminalState.Landed, booster.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("[" + PredictedTouchdownStockFate.LogTag + "]")
                && l.Contains("Predicted touchdown overruled by stock") && l.Contains(BoosterId));
            Assert.Contains(logLines, l => l.Contains("predicted crash taken over")
                && l.Contains("committed ending Landed"));
        }

        [Fact]
        public void ApplyStockDeletion_ConfirmedLanding_IsRefusedAndUntouched()
        {
            Recording booster = PredictedTouchdownBooster();
            booster.StampTerminalState(TerminalState.Landed, "test-flown");
            RecordingTree committed = CommitTreeWith(Core(), booster);

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyStockDeletion(
                committed, BoosterId, 1050.0, "test");

            Assert.False(result.Applied);
            Assert.Equal("not-a-predicted-touchdown", result.Reason);
            Assert.Same(booster, RecordingStore.FindRecordingById(BoosterId));
            Assert.Equal(TerminalState.Landed, booster.TerminalStateValue);
        }

        // (c) catches: a touchdown stock contradicted keeping its descent (the takeover-after-end
        // keep), or the crew rows not following the live ending
        [Fact]
        public void ApplyContradiction_PredictedTouchdown_DropsTheDescent()
        {
            Recording booster = PredictedTouchdownBooster();
            RecordingTree committed = CommitTreeWith(Core(), booster);

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                committed, BoosterId, 1200.0, coastEvidence: null,
                stampLiveEnding: rec => rec.StampTerminalState(TerminalState.SubOrbital, "test-live"),
                context: "test");

            Assert.True(result.Applied, result.Reason);
            Recording after = RecordingStore.FindRecordingById(BoosterId);
            Assert.Equal(TerminalState.SubOrbital, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(PredictedTouchdownStockFate.HasPredictedDescentSection(after));
            Assert.Equal(1000.0, after.EndUT, 6);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, BoosterId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Aboard, rows[0].KerbalEndStateField);
            Assert.Contains(logLines, l => l.Contains("Predicted touchdown contradicted by stock"));
        }

        // (a): a craft that came down is left exactly as committed.
        [Fact]
        public void EvaluatePass_VesselDown_LeavesTheRecording()
        {
            Recording booster = PredictedTouchdownBooster();
            booster.StockHeldVesselUT = 1000.0;
            CommitTreeWith(Core(), booster);
            var live = LiveAirborneOnRails();
            live.LandedOrSplashed = true;

            var pass = PredictedTouchdownStockFate.EvaluatePass(
                EffectiveState.ComputeERS(), null, null, r => live, 1500.0, true, 1.0,
                new Dictionary<string, double>());

            Assert.Empty(pass.StockDeleted);
            Assert.Empty(pass.Contradicted);
            Assert.Equal(1, pass.Count(PredictedTouchdownOutcome.Consistent));
            Assert.False(pass.NeedsRecheck);
            Assert.Same(booster, RecordingStore.FindRecordingById(BoosterId));
            Assert.Equal(TerminalState.Landed, booster.TerminalStateValue);
            Assert.True(booster.TerminalPredicted);
        }

        // ------------------------------------------------------------------ the spawn gate

        // catches: the end spawn re-creating a craft stock deleted (the review's blocker), and
        // the guard blocking the normal replay spawn after a rewind to before the flight
        [Fact]
        public void SpawnGate_RefusesAStockDeletedTouchdown_AndStillSpawnsAfterARewind()
        {
            Recording booster = PredictedTouchdownBooster();
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = guid => false;
            booster.StockHeldVesselUT = 1000.0;

            Assert.True(GhostPlaybackLogic.TryRejectSpawnForTerminalAndSituation(booster, out string reason));
            Assert.Contains("stock deleted", reason);
            var (needsSpawn, spawnReason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(booster, false);
            Assert.False(needsSpawn);
            Assert.Contains("stock deleted", spawnReason);
            Assert.Contains(logLines, l => l.Contains("Spawn refused") && l.Contains(BoosterId));

            // A rewind to before the flight forgets the stamp: the craft is not in that timeline
            // yet, and the replay spawns it at its touchdown.
            Assert.Equal(1, PredictedTouchdownStockFate.ForgetStockHeldAfterTimeTravel(
                new[] { booster }, 900.0, "PlainRewind"));
            Assert.True(double.IsNaN(booster.StockHeldVesselUT));
            Assert.False(GhostPlaybackLogic.TryRejectSpawnForTerminalAndSituation(booster, out string afterRewind),
                afterRewind);
            (needsSpawn, spawnReason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(booster, false);
            Assert.True(needsSpawn, spawnReason);
        }

        [Fact]
        public void SpawnGate_VesselStillThereOrUnknown_DoesNotRefuse()
        {
            Recording booster = PredictedTouchdownBooster();
            booster.StockHeldVesselUT = 1000.0;
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = guid => true;
            Assert.False(PredictedTouchdownStockFate.ShouldRefuseSpawnAsStockDeleted(booster, out _));
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = guid => null;
            Assert.False(PredictedTouchdownStockFate.ShouldRefuseSpawnAsStockDeleted(booster, out _));
        }

        // ------------------------------------------------------------------ the stamp

        // catches: the commit not recording that stock held the craft (the exit-to-TS case is
        // then never reconciled), and a commit after a revert stamping a craft not in the game
        [Fact]
        public void CommitTree_StampsOnlyWhenStockHoldsTheCraftNow()
        {
            PredictedTouchdownStockFate.NowUTOverrideForTesting = () => 1003.5;
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting =
                guid => string.Equals(guid, BoosterGuid, StringComparison.OrdinalIgnoreCase);

            Recording held = PredictedTouchdownBooster();
            Recording reverted = PredictedTouchdownBooster("rec-other", "fedcba9876543210fedcba9876543210");
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Touchdown", RootRecordingId = CoreId, ActiveRecordingId = CoreId,
            };
            tree.AddOrReplaceRecording(Core());
            tree.AddOrReplaceRecording(held);
            tree.AddOrReplaceRecording(reverted);
            Assert.True(TreeCommitService.CommitTree(tree));

            Assert.Equal(1003.5, held.StockHeldVesselUT, 6);
            Assert.True(double.IsNaN(reverted.StockHeldVesselUT));
            Assert.Contains(logLines, l => l.Contains("committed while stock holds the vessel"));
        }

        [Fact]
        public void TimeTravel_ClearsOnlyStampsNewerThanTheLoadedState()
        {
            var newer = new Recording { RecordingId = "a", StockHeldVesselUT = 2000.0 };
            var older = new Recording { RecordingId = "b", StockHeldVesselUT = 500.0 };
            var none = new Recording { RecordingId = "c" };
            Assert.Equal(1, PredictedTouchdownStockFate.ForgetStockHeldAfterTimeTravel(
                new[] { newer, older, none, newer }, 1000.0, "StockRevert"));
            Assert.True(double.IsNaN(newer.StockHeldVesselUT));
            Assert.Equal(500.0, older.StockHeldVesselUT, 6);
            Assert.True(double.IsNaN(none.StockHeldVesselUT));
            Assert.Contains(logLines, l => l.Contains("Time-travel load kind=StockRevert"));
        }

        [Fact]
        public void NoteStockHeld_KeepsTheLaterUT()
        {
            var rec = new Recording { RecordingId = "a" };
            Assert.True(PredictedTouchdownStockFate.NoteStockHeld(rec, 100.0));
            Assert.False(PredictedTouchdownStockFate.NoteStockHeld(rec, 50.0));
            Assert.True(PredictedTouchdownStockFate.NoteStockHeld(rec, 150.0));
            Assert.Equal(150.0, rec.StockHeldVesselUT, 6);
        }

        // catches: old recordings changed on disk (the key is sparse) and the stamp lost on save
        [Fact]
        public void Codec_StampIsSparseAndRoundTrips()
        {
            var plain = new Recording { RecordingId = "plain" };
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, plain);
            Assert.Null(node.GetValue("stockHeldUT"));

            Recording booster = PredictedTouchdownBooster();
            booster.StockHeldVesselUT = 1003.25;
            var stamped = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(stamped, booster);
            Assert.Equal("1003.25", stamped.GetValue("stockHeldUT"));
            var loaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(stamped, loaded);
            Assert.Equal(1003.25, loaded.StockHeldVesselUT, 9);
            Assert.Equal(1003.25, Recording.DeepClone(booster).StockHeldVesselUT, 9);
        }

        // catches: a time-travel load (rewind, revert, quickload, Re-Fly) leaving a stamp from a
        // stock state the game no longer holds, so a rewound craft reads as stock-deleted
        [Fact]
        public void OnLoad_EveryTimeTravelBreakSiteForgetsTheStamps()
        {
            string text = ParsekScenarioSourceFiles.ReadAll(
                ParsekScenarioOnLoadCharacterizationTests.ParsekSourceRoot());
            int breaks = 0, forgets = 0, at = 0;
            while ((at = text.IndexOf("StockShadowTakeBack.NoteLoad(", at, StringComparison.Ordinal)) >= 0)
            {
                breaks++;
                int next = text.IndexOf("ForgetStockHeldVesselsAfterTimeTravel(", at, StringComparison.Ordinal);
                int nextBreak = text.IndexOf("StockShadowTakeBack.NoteLoad(", at + 1, StringComparison.Ordinal);
                if (next > at && (nextBreak < 0 || next < nextBreak))
                    forgets++;
                at++;
            }
            Assert.Equal(2, breaks);
            Assert.Equal(breaks, forgets);
            Assert.Contains("PredictedTouchdownStockFate.ForgetStockHeldAfterTimeTravel(", text);
        }

        // catches: a live tree's dock / claw / boarding with the craft read as stock's deletion
        // (the commit takes the predicted ending over, owner ruling Q1b)
        [Fact]
        public void MergePartnerOfLiveTree_DockNamingTheCraft_OwnsIt()
        {
            Recording booster = PredictedTouchdownBooster();
            var live = new RecordingTree { Id = "tree-live" };
            Assert.False(PredictedCrashReconcilerHost.IsMergePartnerOfLiveTree(booster, live));
            live.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-dock", Type = BranchPointType.Dock, UT = 1200.0,
                TargetVesselPersistentId = BoosterPid, TargetVesselGuid = BoosterGuid,
            });
            Assert.True(PredictedCrashReconcilerHost.IsMergePartnerOfLiveTree(booster, live));
            live.BranchPoints[0].TargetVesselGuid = "fedcba9876543210fedcba9876543210";
            Assert.False(PredictedCrashReconcilerHost.IsMergePartnerOfLiveTree(booster, live));
        }

        // ------------------------------------------------------------------ recovery evidence

        // Q13 catches: a recovery of the craft before its predicted touchdown writing no
        // recovery row (the recording "ends" after it), so the reconciler then reads the
        // recovered craft as a stock kill
        [Fact]
        public void RecoveryOwner_PredictedTouchdownRecoveredBeforeTheTouchdown_IsTheOwner()
        {
            Recording booster = PredictedTouchdownBooster();
            Assert.Equal(1000.0, CrewRecoveryReservationClose.OwnerEndUT(booster), 6);
            List<Recording> owners = CrewRecoveryReservationClose.SelectOwnerRecordings(
                new List<Recording> { booster }, BoosterPid, BoosterGuid, 1040.0);
            Assert.Single(owners);

            booster.StampTerminalState(TerminalState.Landed, "test-flown");
            Assert.Equal(booster.EndUT, CrewRecoveryReservationClose.OwnerEndUT(booster), 6);
        }

        [Theory]
        [InlineData(TerminalState.Destroyed, true)]
        [InlineData(TerminalState.SubOrbital, true)]
        [InlineData(TerminalState.Landed, false)]   // untouched copy
        public void ShouldRefreshCrewRowsOnReplace_PredictedTouchdown(TerminalState incomingTerminal, bool expected)
        {
            var existing = new Recording { RecordingId = BoosterId, ExplicitStartUT = 100.0, ExplicitEndUT = 400.0 };
            existing.StampTerminalState(TerminalState.Landed, "test", predicted: true);
            var incoming = new Recording { RecordingId = BoosterId, ExplicitStartUT = 100.0, ExplicitEndUT = 400.0 };
            incoming.StampTerminalState(incomingTerminal, "test",
                predicted: incomingTerminal == TerminalState.Landed);
            Assert.Equal(expected, LedgerOrchestrator.ShouldRefreshCrewRowsOnReplace(existing, incoming));
        }
    }
}
