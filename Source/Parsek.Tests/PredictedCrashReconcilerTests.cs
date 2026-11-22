using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner rulings P-STOCK / P-LEAVE / Q1b: a committed predicted crash whose vessel stock
    /// still holds past the predicted crash, where stock's deletion check runs, was a wrong
    /// prediction and is dropped as a takeover drops it; a vessel stock removed confirms it and
    /// nothing changes.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedCrashReconcilerTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private const string TreeId = "tree-reconcile";
        private const string CoreId = "rec-core";
        private const string StageId = "rec-stage";
        private const uint StagePid = 4242;
        private const string StageGuid = "0123456789abcdef0123456789abcdef";
        private readonly List<string> logLines = new List<string>();

        public PredictedCrashReconcilerTests()
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

        // ---- the decision table ----

        private static PredictedCrashCandidateFacts Candidate(double crashUT = 400.0)
        {
            return new PredictedCrashCandidateFacts
            {
                IsPredictedCrash = true, HasLaunchGuid = true, OwnedElsewhere = false,
                PredictedCrashUT = crashUT,
            };
        }

        // Kerbin-like: periapsis well above the 1 kPa line, bound orbit.
        private static PredictedCrashLiveFacts LiveInOrbit()
        {
            return new PredictedCrashLiveFacts
            {
                Found = true, Eccentricity = 0.1, PeriapsisAltitude = 80000.0,
                Descending = false, StockRemovalAltitude = 37000.0,
            };
        }

        private static PredictedCrashReconcileOutcome DecideConfirmed(
            PredictedCrashCandidateFacts c, PredictedCrashLiveFacts live, double nowUT = 1000.0)
        {
            return PredictedCrashReconciler.Decide(c, live, nowUT, sceneSettled: true,
                observedBefore: true, secondsSinceFirstObservation: 5.0);
        }

        [Fact]
        public void Decide_NotAPredictedCrash_IsLeftAlone()
        {
            var c = Candidate();
            c.IsPredictedCrash = false;
            Assert.Equal(PredictedCrashReconcileOutcome.NotPredictedCrash, DecideConfirmed(c, LiveInOrbit()));
        }

        // catches: a bare-pid match being read as the vessel (never: AGENTS persistentId rule)
        [Fact]
        public void Decide_NoLaunchGuid_NeverDecides()
        {
            var c = Candidate();
            c.HasLaunchGuid = false;
            Assert.Equal(PredictedCrashReconcileOutcome.NoLaunchGuid, DecideConfirmed(c, LiveInOrbit()));
        }

        [Fact]
        public void Decide_OwnedByALiveSession_IsLeftToItsCommit()
        {
            var c = Candidate();
            c.OwnedElsewhere = true;
            Assert.Equal(PredictedCrashReconcileOutcome.OwnedElsewhere, DecideConfirmed(c, LiveInOrbit()));
        }

        // catches: acting at scene entry before stock's deletion check ran once
        [Fact]
        public void Decide_SceneNotSettled_Waits()
        {
            Assert.Equal(PredictedCrashReconcileOutcome.SceneNotSettled,
                PredictedCrashReconciler.Decide(Candidate(), LiveInOrbit(), 1000.0,
                    sceneSettled: false, observedBefore: true, secondsSinceFirstObservation: 5.0));
        }

        // catches: a vessel still flying before its predicted crash being read as a contradiction
        [Theory]
        [InlineData(300.0)]
        [InlineData(400.0)]
        [InlineData(409.0)]
        public void Decide_BeforeTheCrashPlusMargin_IsNotDue(double nowUT)
        {
            Assert.Equal(PredictedCrashReconcileOutcome.NotDueYet,
                DecideConfirmed(Candidate(), LiveInOrbit(), nowUT));
        }

        // (b): stock removed it, which is what the prediction said.
        [Fact]
        public void Decide_VesselGone_ConfirmsThePrediction()
        {
            Assert.Equal(PredictedCrashReconcileOutcome.VesselGone,
                DecideConfirmed(Candidate(), default(PredictedCrashLiveFacts)));
        }

        [Fact]
        public void Decide_ActiveVessel_IsTheTakeoversCase()
        {
            var live = LiveInOrbit();
            live.IsActiveVessel = true;
            Assert.Equal(PredictedCrashReconcileOutcome.VesselIsActive, DecideConfirmed(Candidate(), live));
        }

        // catches: dropping the prediction of a vessel stock physics is still flying down
        [Fact]
        public void Decide_InPhysicsInTheAir_StockIsStillDeciding()
        {
            var live = LiveInOrbit();
            live.InPhysics = true;
            live.PeriapsisAltitude = -500000.0;
            Assert.Equal(PredictedCrashReconcileOutcome.StockStillSimulating, DecideConfirmed(Candidate(), live));
        }

        // catches: an airless body, where stock deletes an unloaded vessel only below the datum:
        // past the predicted terrain impact the vessel still exists for a few seconds
        [Fact]
        public void Decide_StillFallingTowardTheDeletionLine_StockMayStillRemoveIt()
        {
            var live = new PredictedCrashLiveFacts
            {
                Found = true, Eccentricity = 0.4, PeriapsisAltitude = -150000.0,
                Descending = true, StockRemovalAltitude = 0.0,
            };
            Assert.Equal(PredictedCrashReconcileOutcome.StockMayStillRemove, DecideConfirmed(Candidate(), live));
        }

        // A landed vessel is never deleted by stock's check: the crash did not happen.
        [Fact]
        public void Decide_LandedPastTheCrash_IsContradicted()
        {
            var live = new PredictedCrashLiveFacts
            {
                Found = true, LandedOrSplashed = true, InPhysics = true,
                Eccentricity = 0.99, PeriapsisAltitude = -590000.0,
            };
            Assert.Equal(PredictedCrashReconcileOutcome.Contradicted, DecideConfirmed(Candidate(), live));
        }

        [Fact]
        public void Decide_ClearOrbitPastTheCrash_IsContradicted()
        {
            Assert.Equal(PredictedCrashReconcileOutcome.Contradicted, DecideConfirmed(Candidate(), LiveInOrbit()));
        }

        // catches: acting on a single sighting, where stock's check may not have run on the
        // vessel's new position yet (a high-warp frame)
        [Theory]
        [InlineData(false, double.NaN)]
        [InlineData(true, 0.5)]
        public void Decide_FirstSighting_AwaitsConfirmation(bool observedBefore, double since)
        {
            Assert.Equal(PredictedCrashReconcileOutcome.AwaitingConfirmation,
                PredictedCrashReconciler.Decide(Candidate(), LiveInOrbit(), 1000.0, true, observedBefore, since));
        }

        [Theory]
        // e, PeA, descending, line, expected
        [InlineData(0.1, 80000.0, false, 37000.0, false)]   // clear bound orbit
        [InlineData(0.1, 30000.0, false, 37000.0, true)]    // bound, low point under the 1 kPa line
        [InlineData(1.2, 30000.0, true, 37000.0, true)]     // unbound, still before its periapsis
        [InlineData(1.2, 30000.0, false, 37000.0, false)]   // unbound, past its periapsis: leaves
        [InlineData(0.3, 1000.0, false, 0.0, false)]        // airless, above the datum: stock keeps it
        [InlineData(0.3, -10.0, false, 0.0, true)]          // airless, under the datum
        [InlineData(double.NaN, 80000.0, false, 0.0, true)] // unreadable orbit: keep the prediction
        public void TrajectoryReachesStockRemoval_TruthTable(
            double e, double pe, bool descending, double line, bool expected)
        {
            Assert.Equal(expected,
                PredictedCrashReconciler.TrajectoryReachesStockRemoval(e, pe, descending, line));
        }

        // ---- the pass ----

        private static Recording PredictedStage(string id = StageId, string guid = StageGuid, double crashUT = 400.0)
        {
            var rec = new Recording
            {
                RecordingId = id, TreeId = TreeId, VesselName = "Crewed Stage",
                VesselPersistentId = StagePid, RecordedVesselGuid = guid,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", altitude = 20000 });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin", altitude = 35000 });
            rec.OrbitSegments.Add(new OrbitSegment
            {
                startUT = 160.0, endUT = crashUT, bodyName = "Kerbin",
                semiMajorAxis = 500000, eccentricity = 0.3, isPredicted = true,
            });
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            rec.VesselDestroyed = true;
            rec.ExplicitEndUT = crashUT;
            return rec;
        }

        // catches: a pass acting on a first sighting, or forgetting a sighting between passes
        [Fact]
        public void EvaluatePass_ContradictionActsOnTheSecondSightingOnly()
        {
            var recs = new List<Recording> { PredictedStage() };
            var firstObserved = new Dictionary<string, double>();
            int lookups = 0;
            Func<Recording, PredictedCrashLiveFacts> lookup = r => { lookups++; return LiveInOrbit(); };

            var first = PredictedCrashReconciler.EvaluatePass(recs, null, lookup, 1000.0, true, 10.0, firstObserved);
            Assert.Empty(first.Contradicted);
            Assert.Equal(1, first.Count(PredictedCrashReconcileOutcome.AwaitingConfirmation));
            Assert.True(first.NeedsRecheck);
            Assert.True(firstObserved.ContainsKey(StageId));

            var second = PredictedCrashReconciler.EvaluatePass(recs, null, lookup, 1001.0, true, 13.0, firstObserved);
            Assert.Single(second.Contradicted);
            Assert.Equal(2, lookups);
        }

        // catches: a sighting surviving a pass where the vessel went back to "stock may still
        // remove it", so a later single sighting would act at once
        [Fact]
        public void EvaluatePass_SightingIsForgottenWhenNoLongerContradicted()
        {
            var recs = new List<Recording> { PredictedStage() };
            var firstObserved = new Dictionary<string, double>();
            PredictedCrashLiveFacts live = LiveInOrbit();
            PredictedCrashReconciler.EvaluatePass(recs, null, r => live, 1000.0, true, 10.0, firstObserved);
            live.InPhysics = true;
            PredictedCrashReconciler.EvaluatePass(recs, null, r => live, 1001.0, true, 13.0, firstObserved);
            Assert.Empty(firstObserved);
        }

        // catches: the live lookup running for every candidate every pass (bounded cost), and
        // the next due UT not following the earliest predicted crash
        [Fact]
        public void EvaluatePass_NotDue_SkipsTheLiveLookupAndSchedulesTheEarliestCrash()
        {
            var recs = new List<Recording>
            {
                PredictedStage("a", StageGuid, 2000.0),
                PredictedStage("b", "fedcba9876543210fedcba9876543210", 1500.0),
                PredictedStage("c", null, 1200.0),
            };
            int lookups = 0;
            var pass = PredictedCrashReconciler.EvaluatePass(
                recs, null, r => { lookups++; return LiveInOrbit(); }, 1000.0, true, 1.0,
                new Dictionary<string, double>());
            Assert.Equal(0, lookups);
            Assert.Equal(3, pass.Candidates);
            Assert.Equal(2, pass.Count(PredictedCrashReconcileOutcome.NotDueYet));
            // the unidentified one never schedules a pass
            Assert.Equal(1, pass.Count(PredictedCrashReconcileOutcome.NoLaunchGuid));
            Assert.Equal(1500.0 + PredictedCrashReconciler.CrashMarginSeconds, pass.NextDueUT, 6);
            Assert.False(pass.NeedsRecheck);
        }

        [Fact]
        public void EvaluatePass_GoneAndUnidentified_AreCountedNotActed()
        {
            var recs = new List<Recording> { PredictedStage("a", StageGuid), PredictedStage("b", null) };
            var pass = PredictedCrashReconciler.EvaluatePass(
                recs, null, r => default(PredictedCrashLiveFacts), 1000.0, true, 1.0,
                new Dictionary<string, double>());
            Assert.Empty(pass.Contradicted);
            Assert.Single(pass.Gone);
            Assert.Equal(1, pass.Count(PredictedCrashReconcileOutcome.NoLaunchGuid));
            string summary = PredictedCrashReconciler.FormatPassSummary(pass, "TRACKSTATION", 1000.0, 0, 0);
            Assert.Contains("vesselGone=1", summary);
            Assert.Contains("noLaunchGuid=1", summary);
        }

        // ---- the committed-history change ----

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

        private static RecordingTree CommitTreeWith(params Recording[] recs)
        {
            var tree = new RecordingTree
            {
                Id = TreeId, TreeName = "Reconcile", RootRecordingId = CoreId, ActiveRecordingId = CoreId,
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

        // catches: a contradicted prediction mutated in place on the committed copy (no
        // replace, the Dead crew row and the death penalty standing), or the replace not
        // re-deriving the crew from the live ending
        [Fact]
        public void ApplyContradiction_ReplacesTheCommittedCopy_CrewLiveAgain()
        {
            AddVesselLossEvent(StageId, 400.0);
            RecordingTree committed = CommitTreeWith(Core(), PredictedStage());
            Recording before = committed.Recordings[StageId];
            Assert.Equal(KerbalEndState.Dead, Rows(GameActionType.KerbalAssignment, StageId)[0].KerbalEndStateField);
            Assert.Single(Rows(GameActionType.ReputationPenalty, StageId));

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                committed, StageId, 1000.0, coastEvidence: null,
                stampLiveEnding: rec =>
                {
                    rec.VesselSnapshot = CrewSnapshot(Jeb);
                    rec.StampTerminalState(TerminalState.Orbiting, "test-live");
                },
                context: "test");

            Assert.True(result.Applied, result.Reason);
            Assert.Equal(TerminalState.Orbiting, result.NewTerminal);
            Recording after = RecordingStore.FindRecordingById(StageId);
            Assert.NotSame(before, after);
            Assert.Equal(TerminalState.Orbiting, after.TerminalStateValue);
            Assert.False(after.TerminalPredicted);
            Assert.False(after.VesselDestroyed);
            Assert.DoesNotContain(after.OrbitSegments, s => s.isPredicted);
            Assert.Equal(160.0, after.EndUT, 6);
            Assert.Equal(KerbalEndState.Aboard, after.CrewEndStates[Jeb]);
            List<GameAction> rows = Rows(GameActionType.KerbalAssignment, StageId);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Aboard, rows[0].KerbalEndStateField);
            Assert.Empty(Rows(GameActionType.ReputationPenalty, StageId));
            // the committed copy the pass read was never written
            Assert.True(before.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("[" + PredictedCrashReconciler.LogTag + "]")
                && l.Contains("Predicted crash contradicted by stock") && l.Contains(StageId));
            Assert.Contains(logLines, l => l.Contains("[PredictedTakeover]") && l.Contains("context=test"));
        }

        // catches: a confirmed crash (history) being reopened by the reconciler
        [Fact]
        public void ApplyContradiction_ConfirmedCrash_IsRefusedAndUntouched()
        {
            Recording stage = PredictedStage();
            stage.StampTerminalState(TerminalState.Destroyed, "test-confirmed", predicted: false);
            RecordingTree committed = CommitTreeWith(Core(), stage);

            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                committed, StageId, 1000.0, null, rec => rec.StampTerminalState(TerminalState.Orbiting, "t"), "test");

            Assert.False(result.Applied);
            Assert.Equal("not-a-predicted-crash", result.Reason);
            Assert.Same(stage, RecordingStore.FindRecordingById(StageId));
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.Contains(logLines, l => l.Contains("contradiction not applied") && l.Contains("not-a-predicted-crash"));
        }

        [Fact]
        public void ApplyContradiction_UnknownRecording_IsRefused()
        {
            RecordingTree committed = CommitTreeWith(Core(), PredictedStage());
            PredictedCrashContradictionResult result = PredictedCrashReconciler.ApplyContradiction(
                committed, "nope", 1000.0, null, null, "test");
            Assert.False(result.Applied);
            Assert.Equal("recording-not-in-tree", result.Reason);
        }
    }
}
