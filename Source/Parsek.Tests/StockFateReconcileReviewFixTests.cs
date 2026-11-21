using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Final-review fixes to the stock-fate reconciler (owner principle P-STOCK):
    /// the live lookup in FLIGHT reads only the live vessel list (a craft stock just deleted
    /// is gone at once, never "stored" until the next save), a surviving breakup remnant is
    /// no prediction to reconcile or take over (Q6b), and a predicted final orbit (Q8) is
    /// squared with stock: gone from stock in this timeline with no recovery, termination or
    /// merge, it ends Destroyed with its crew dead; still held, it is left alone.
    /// </summary>
    [Collection("Sequential")]
    public class StockFateReconcileReviewFixTests : IDisposable
    {
        private const string Jeb = PredictedTouchdownStockFateTests.Jeb;
        private const string TreeId = PredictedTouchdownStockFateTests.TreeId;
        private const string OrbiterId = "rec-orbiter";
        private const uint OrbiterPid = 5151u;
        private const string OrbiterGuid = "aaaabbbbccccddddeeeeffff00001111";
        private readonly List<string> logLines = new List<string>();

        public StockFateReconcileReviewFixTests()
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

        private static OrbitSegment Segment(double startUT, double endUT, bool predicted, string body = "Kerbin")
        {
            return new OrbitSegment
            {
                startUT = startUT,
                endUT = endUT,
                bodyName = body,
                semiMajorAxis = 700000.0,
                eccentricity = 0.01,
                inclination = 0.0,
                isPredicted = predicted,
            };
        }

        // A crewed stage recorded to UT 1000 on an escape, its tail predicted to a final orbit
        // around the Mun through the production applier (Orbiting at 3000, predicted segments).
        private static Recording PredictedFinalOrbitStage(bool predictedTail = true)
        {
            var rec = new Recording
            {
                RecordingId = OrbiterId,
                TreeId = TreeId,
                VesselName = "Escaping Stage",
                VesselPersistentId = OrbiterPid,
                RecordedVesselGuid = OrbiterGuid,
                ExplicitStartUT = 900.0,
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 900.0, bodyName = "Kerbin", altitude = 90000.0, rotation = Quaternion.identity,
                recordedGroundClearance = double.NaN,
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 1000.0, bodyName = "Kerbin", altitude = 150000.0, rotation = Quaternion.identity,
                recordedGroundClearance = double.NaN,
            });
            rec.VesselSnapshot = PredictedTouchdownStockFateTests.CrewSnapshot(Jeb);
            rec.GhostVisualSnapshot = PredictedTouchdownStockFateTests.CrewSnapshot(Jeb);

            var cache = new RecordingFinalizationCache
            {
                RecordingId = OrbiterId,
                VesselPersistentId = OrbiterPid,
                Owner = FinalizationCacheOwner.BackgroundOnRails,
                Status = FinalizationCacheStatus.Fresh,
                RefreshReason = "test",
                TerminalState = TerminalState.Orbiting,
                TerminalUT = predictedTail ? 3000.0 : 1000.0,
                TerminalBodyName = predictedTail ? "Mun" : "Kerbin",
                PredictedSegments = predictedTail
                    ? new List<OrbitSegment> { Segment(1000.0, 2500.0, true), Segment(2500.0, 3000.0, true, "Mun") }
                    : new List<OrbitSegment>(),
                TailStartsAtUT = 1000.0,
            };
            Assert.True(RecordingFinalizationCacheApplier.TryApply(rec, cache,
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", AllowStale = true },
                out _));
            return rec;
        }

        private static List<GameAction> Rows(GameActionType type, string recordingId)
        {
            var rows = new List<GameAction>();
            foreach (var a in Ledger.Actions)
                if (a.Type == type && a.RecordingId == recordingId)
                    rows.Add(a);
            return rows;
        }

        private static PredictedTouchdownOutcome DecideConfirmed(
            PredictedTouchdownCandidateFacts c, PredictedCrashLiveFacts live, double nowUT = 2000.0)
        {
            return PredictedTouchdownStockFate.Decide(c, live, nowUT, sceneSettled: true,
                observedBefore: true, secondsSinceFirstObservation: 5.0);
        }

        // ------------------------------------------------------------------ 1. the live lookup

        // catches (review C1): a craft Vessel.Die just removed in FLIGHT read as live from
        // flightState.protoVessels, which stock rebuilds only at a save, so the spawn gate and
        // the spawn-death check failed open right after a stock deletion.
        [Fact]
        public void LiveLookup_InFlight_AStoredOnlyVesselIsGone()
        {
            var live = new List<string> { "11112222333344445555666677778888" };
            var stored = new List<string> { OrbiterGuid, "11112222333344445555666677778888" };
            Assert.Equal(false, PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, flightScene: true, liveVesselGuids: live, storedVesselGuids: stored));
            live.Add(OrbiterGuid.ToUpperInvariant());
            Assert.Equal(true, PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, true, live, stored));
        }

        [Fact]
        public void LiveLookup_InFlight_AnEmptyOrUnreadableListIsUnknown()
        {
            var stored = new List<string> { OrbiterGuid };
            Assert.Null(PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, true, new List<string>(), stored));
            Assert.Null(PredictedTouchdownStockFate.DecideVesselWithGuidLive(OrbiterGuid, true, null, stored));
            Assert.Null(PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                null, true, new List<string> { OrbiterGuid }, stored));
        }

        // Outside FLIGHT stock deletes nothing on its own: a stored vessel is live.
        [Fact]
        public void LiveLookup_OutsideFlight_EitherListCounts()
        {
            Assert.Equal(true, PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, false, new List<string>(), new List<string> { OrbiterGuid }));
            Assert.Equal(true, PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, false, new List<string> { OrbiterGuid }, null));
            Assert.Equal(false, PredictedTouchdownStockFate.DecideVesselWithGuidLive(
                OrbiterGuid, false, new List<string>(), new List<string>()));
            Assert.Null(PredictedTouchdownStockFate.DecideVesselWithGuidLive(OrbiterGuid, false, null, null));
        }

        // ------------------------------------------------------------------ 2. remnants

        private static Recording SurvivingRemnant()
        {
            var rec = new Recording
            {
                RecordingId = "rec-remnant", TreeId = TreeId, VesselName = "Wreck", VesselPersistentId = 777u,
                RecordedVesselGuid = OrbiterGuid, ExplicitStartUT = 100.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 5000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 200.0, bodyName = "Kerbin", altitude = 4000.0 });
            rec.MarkDestroyedAtTerminal(260.0, "test", vesselSurvives: true);
            Assert.True(rec.TerminalPredicted);
            Assert.True(rec.TerminalRemnantSurvives);
            return rec;
        }

        // catches (both reviews C2): every FLIGHT entry reading a surviving remnant as a
        // contradicted crash, re-stamping the same identity loss, committing an unchanged tree
        // and then warning commit-refused on the WRN surface.
        [Fact]
        public void Remnant_IsNeverAReconcilerCandidate()
        {
            Recording remnant = SurvivingRemnant();
            Assert.False(PredictedCrashReconciler.BuildCandidate(remnant, null).IsPredictedCrash);

            var live = new PredictedCrashLiveFacts
            {
                Found = true, LandedOrSplashed = true, Eccentricity = 0.99, PeriapsisAltitude = -500000.0,
            };
            var firstObserved = new Dictionary<string, double>();
            for (int i = 0; i < 2; i++)
            {
                PredictedCrashReconcilePassResult pass = PredictedCrashReconciler.EvaluatePass(
                    new List<Recording> { remnant }, null, r => live, 1000.0, true, 10.0 + i * 5.0, firstObserved);
                Assert.Equal(0, pass.Candidates);
                Assert.Empty(pass.Contradicted);
            }
            PredictedTouchdownPassResult td = PredictedTouchdownStockFate.EvaluatePass(
                new List<Recording> { remnant }, null, null, r => live, 1000.0, true, 1.0,
                new Dictionary<string, double>());
            Assert.Equal(0, td.Candidates);

            // control: a plain predicted crash is still a candidate
            var crash = new Recording { RecordingId = "rec-crash", RecordedVesselGuid = OrbiterGuid };
            crash.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            Assert.True(PredictedCrashReconciler.BuildCandidate(crash, null).IsPredictedCrash);
        }

        // catches: a takeover or merge partner dropping a remnant's identity-loss ending as if it
        // were a forecast (its crew are decided per part, owner ruling Q6b)
        [Fact]
        public void Remnant_IsNotAPredictedEnding()
        {
            Assert.False(PredictedEndingTakeover.HasPredictedEnding(SurvivingRemnant()));
            Assert.True(PredictedEndingTakeover.IsSurvivingBreakupRemnant(SurvivingRemnant()));
            var crash = new Recording { RecordingId = "rec-crash" };
            crash.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            Assert.True(PredictedEndingTakeover.HasPredictedEnding(crash));
        }

        // ------------------------------------------------------------------ 4. Q8 final orbits

        // catches (review D4): a predicted final orbit carrying no prediction flag, so nothing
        // ever squares it with stock, and a stable orbit wrongly read as a prediction
        [Fact]
        public void Applier_MarksAFinalOrbitAPredictedTailReached_NotAStableOrbit()
        {
            Recording predicted = PredictedFinalOrbitStage();
            Assert.Equal(TerminalState.Orbiting, predicted.TerminalStateValue);
            Assert.True(predicted.TerminalPredicted);
            Assert.True(PredictedTouchdownStockFate.IsPredictedFinalOrbit(predicted));
            Assert.Contains(logLines, l => l.Contains("final orbit marked predicted") && l.Contains(OrbiterId));

            Recording stable = PredictedFinalOrbitStage(predictedTail: false);
            Assert.Equal(TerminalState.Orbiting, stable.TerminalStateValue);
            Assert.False(stable.TerminalPredicted);
            Assert.False(PredictedTouchdownStockFate.IsPredictedFinalOrbit(stable));
        }

        // The scene-exit finalizer's own apply (not only the cache applier) marks the final orbit.
        [Theory]
        [InlineData(TerminalState.Orbiting, true)]
        [InlineData(TerminalState.SubOrbital, true)]
        [InlineData(TerminalState.Landed, false)]
        public void SceneExitApply_MarksAFinalOrbitAPredictedTailReached(TerminalState terminal, bool expected)
        {
            var rec = new Recording
            {
                RecordingId = "rec-exit-orbit", VesselPersistentId = OrbiterPid, RecordedVesselGuid = OrbiterGuid,
                ExplicitStartUT = 900.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 900.0, bodyName = "Kerbin", altitude = 90000.0 });
            rec.Points.Add(new TrajectoryPoint { ut = 1000.0, bodyName = "Kerbin", altitude = 150000.0 });
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording r, Vessel v, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        appendedOrbitSegments = new List<OrbitSegment> { Segment(1000.0, 3000.0, true, "Sun") },
                        terminalState = terminal,
                        terminalUT = 3000.0,
                        extrapolatedSegmentCount = 1,
                    };
                    return true;
                };
            Assert.True(IncompleteBallisticSceneExitFinalizer.TryApply(rec, null, 1000.0, "FinalizeIndividualRecording"));
            Assert.Equal(terminal, rec.TerminalStateValue);
            Assert.Equal(expected, PredictedTouchdownStockFate.IsPredictedFinalOrbit(rec));
        }

        [Fact]
        public void MarkPredictedFinalOrbit_OnlyOrbitEndingsWithAPredictedTail()
        {
            var landed = new Recording { RecordingId = "a" };
            landed.StampTerminalState(TerminalState.Landed, "test");
            Assert.False(RecordingFinalizationCacheApplier.MarkPredictedFinalOrbit(landed, 3, "test"));
            Assert.False(landed.TerminalPredicted);
            var sub = new Recording { RecordingId = "b" };
            sub.StampTerminalState(TerminalState.SubOrbital, "test");
            Assert.False(RecordingFinalizationCacheApplier.MarkPredictedFinalOrbit(sub, 0, "test"));
            Assert.True(RecordingFinalizationCacheApplier.MarkPredictedFinalOrbit(sub, 1, "test"));
            Assert.True(sub.TerminalPredicted);
            Assert.True(PredictedTouchdownStockFate.IsPredictedFinalOrbit(sub));
        }

        // catches: the flag lost on save (the codec reads it only with a predictable terminal)
        [Fact]
        public void Codec_PredictedFinalOrbitRoundTrips()
        {
            Recording rec = PredictedFinalOrbitStage();
            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.Equal("True", node.GetValue("terminalPredicted"));
            var loaded = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, loaded);
            Assert.Equal(TerminalState.Orbiting, loaded.TerminalStateValue);
            Assert.True(loaded.TerminalPredicted);
        }

        private static PredictedTouchdownCandidateFacts OrbitCandidate(double stockHeld = 1000.0)
        {
            return new PredictedTouchdownCandidateFacts
            {
                IsPredictedFinalOrbit = true, HasLaunchGuid = true, TouchdownUT = 3000.0, StockHeldUT = stockHeld,
            };
        }

        [Fact]
        public void Decide_FinalOrbit_HeldIsLeft_GoneAfterHeldIsStockDeleted()
        {
            var held = new PredictedCrashLiveFacts
            {
                Found = true, Eccentricity = 1.2, PeriapsisAltitude = 50000.0, StockRemovalAltitude = 37000.0,
            };
            // still held, before or after the predicted end: never contradicted (orbits are not compared)
            Assert.Equal(PredictedTouchdownOutcome.FinalOrbitHeld, DecideConfirmed(OrbitCandidate(), held, 2000.0));
            Assert.Equal(PredictedTouchdownOutcome.FinalOrbitHeld, DecideConfirmed(OrbitCandidate(), held, 9000.0));
            var active = held;
            active.IsActiveVessel = true;
            Assert.Equal(PredictedTouchdownOutcome.VesselIsActive, DecideConfirmed(OrbitCandidate(), active));

            Assert.Equal(PredictedTouchdownOutcome.StockDeleted,
                DecideConfirmed(OrbitCandidate(), default(PredictedCrashLiveFacts)));
            Assert.Equal(PredictedTouchdownOutcome.AwaitingConfirmation,
                PredictedTouchdownStockFate.Decide(OrbitCandidate(), default(PredictedCrashLiveFacts), 2000.0,
                    true, observedBefore: false, secondsSinceFirstObservation: double.NaN));
            Assert.Equal(PredictedTouchdownOutcome.NotInThisTimeline,
                DecideConfirmed(OrbitCandidate(stockHeld: double.NaN), default(PredictedCrashLiveFacts)));
            var removed = OrbitCandidate();
            removed.RemovedByPlayer = true;
            Assert.Equal(PredictedTouchdownOutcome.RemovedByPlayer,
                DecideConfirmed(removed, default(PredictedCrashLiveFacts)));
        }

        [Fact]
        public void EvaluatePass_FinalOrbitIsACandidate_HeldIsStampedAndLeft()
        {
            Recording rec = PredictedFinalOrbitStage();
            var recs = new List<Recording> { rec };
            var held = new PredictedCrashLiveFacts { Found = true, Eccentricity = 1.2, PeriapsisAltitude = 50000.0 };
            PredictedTouchdownPassResult pass = PredictedTouchdownStockFate.EvaluatePass(
                recs, null, null, r => held, 1500.0, true, 1.0, new Dictionary<string, double>());
            Assert.Equal(1, pass.Candidates);
            Assert.Equal(1, pass.Count(PredictedTouchdownOutcome.FinalOrbitHeld));
            Assert.Single(pass.Sighted);
            Assert.False(pass.NeedsRecheck);
            Assert.Empty(pass.StockDeleted);
            Assert.Empty(pass.Contradicted);
            Assert.Contains("finalOrbitHeld=1",
                PredictedTouchdownStockFate.FormatPassSummary(pass, "FLIGHT", 1500.0, 0, 0, 0));
        }

        // catches (review D4): stock destroying the real vessel of a predicted final orbit (an
        // encounter the predictor missed) while the replay still spawns it in its predicted
        // orbit with its crew aboard
        [Fact]
        public void ApplyStockDeletion_FinalOrbit_EndsDestroyed_CrewDead_NoSpawn_SlotOpen()
        {
            Recording stage = PredictedFinalOrbitStage();
            double endUT = stage.EndUT;
            RecordingTree committed = PredictedTouchdownStockFateTests.CommitTreeWith(
                PredictedTouchdownStockFateTests.Core(), stage);
            List<GameAction> before = Rows(GameActionType.KerbalAssignment, OrbiterId);
            Assert.Single(before);
            Assert.Equal(KerbalEndState.Aboard, before[0].KerbalEndStateField);

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyStockDeletion(
                committed, OrbiterId, 2000.0, "test");

            Assert.True(result.Applied, result.Reason);
            Recording after = RecordingStore.FindRecordingById(OrbiterId);
            Assert.NotSame(stage, after);
            Assert.Equal(TerminalState.Destroyed, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.True(GhostPlaybackLogic.IsQuietDestroyedEnd(after));
            Assert.Null(after.TerminalOrbitBody);
            Assert.Equal(endUT, after.EndUT, 6);
            Assert.Equal(TerminalKind.Crashed, TerminalKindClassifier.Classify(after));
            Assert.Equal(KerbalEndState.Dead, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, OrbiterId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);
            var (needsSpawn, _) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(after, false);
            Assert.False(needsSpawn);
            Assert.Equal(TerminalState.Orbiting, stage.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("Predicted final orbit overruled by stock") && l.Contains(OrbiterId));
        }

        [Fact]
        public void ApplyStockDeletion_StableOrbit_IsRefused()
        {
            Recording stable = PredictedFinalOrbitStage(predictedTail: false);
            RecordingTree committed = PredictedTouchdownStockFateTests.CommitTreeWith(
                PredictedTouchdownStockFateTests.Core(), stable);
            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyStockDeletion(
                committed, OrbiterId, 2000.0, "test");
            Assert.False(result.Applied);
            Assert.Same(stable, RecordingStore.FindRecordingById(OrbiterId));
        }

        // catches: the end spawn re-creating the vessel of a final orbit stock destroyed before
        // the reconciler converted it; a rewind to before the flight still spawns it
        [Fact]
        public void SpawnGate_RefusesAStockDeletedFinalOrbit_UntilARewindForgetsTheStamp()
        {
            Recording stage = PredictedFinalOrbitStage();
            stage.StockHeldVesselUT = 1500.0;
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting = guid => false;
            Assert.True(PredictedTouchdownStockFate.ShouldRefuseSpawnAsStockDeleted(stage, out string reason));
            Assert.Contains("final orbit", reason);
            Assert.True(PredictedTouchdownStockFate.IsStockDeletedTouchdown(stage, out _));

            PredictedTouchdownStockFate.ForgetStockHeldAfterTimeTravel(new[] { stage }, 900.0, "PlainRewind");
            Assert.False(PredictedTouchdownStockFate.ShouldRefuseSpawnAsStockDeleted(stage, out _));

            Recording stable = PredictedFinalOrbitStage(predictedTail: false);
            stable.StockHeldVesselUT = 1500.0;
            Assert.False(PredictedTouchdownStockFate.ShouldRefuseSpawnAsStockDeleted(stable, out _));
        }

        [Fact]
        public void CommitTree_StampsAPredictedFinalOrbitStockHolds()
        {
            PredictedTouchdownStockFate.NowUTOverrideForTesting = () => 1004.0;
            PredictedTouchdownStockFate.LiveVesselByGuidOverrideForTesting =
                guid => string.Equals(guid, OrbiterGuid, StringComparison.OrdinalIgnoreCase);
            Recording stage = PredictedFinalOrbitStage();
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Escape", RootRecordingId = PredictedTouchdownStockFateTests.CoreId,
                ActiveRecordingId = PredictedTouchdownStockFateTests.CoreId,
            };
            tree.AddOrReplaceRecording(PredictedTouchdownStockFateTests.Core());
            tree.AddOrReplaceRecording(stage);
            Assert.True(TreeCommitService.CommitTree(tree));
            Assert.Equal(1004.0, stage.StockHeldVesselUT, 6);
            Assert.Contains(logLines, l => l.Contains("predicted final orbit") && l.Contains("committed while stock holds"));
        }

        [Theory]
        [InlineData(TerminalState.Orbiting, TerminalState.Destroyed, true)]
        [InlineData(TerminalState.SubOrbital, TerminalState.Landed, true)]
        [InlineData(TerminalState.Orbiting, TerminalState.Orbiting, false)]   // untouched copy
        public void ShouldRefreshCrewRowsOnReplace_PredictedFinalOrbit(
            TerminalState existingTerminal, TerminalState incomingTerminal, bool expected)
        {
            var existing = new Recording { RecordingId = OrbiterId, ExplicitStartUT = 100.0, ExplicitEndUT = 400.0 };
            existing.StampTerminalState(existingTerminal, "test", predicted: true);
            var incoming = new Recording { RecordingId = OrbiterId, ExplicitStartUT = 100.0, ExplicitEndUT = 400.0 };
            incoming.StampTerminalState(incomingTerminal, "test", predicted: incomingTerminal == existingTerminal);
            Assert.Equal(expected, LedgerOrchestrator.ShouldRefreshCrewRowsOnReplace(existing, incoming));
        }
    }
}
