using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Owner ruling Q6c (replaces Q6): "If it's destroyed, the crew is dead." A crash Parsek
    /// only predicted (Recording.TerminalPredicted) kills the crew like one KSP carried out;
    /// the flag stays for the takeover and the playback. Ruling Q1b: a stage KSP
    /// still has when its recording ends keeps its predicted crash. Covers the inference, the
    /// persisted key, the copy sites, the stamp sites that decide the flag, the ledger rows it
    /// drives, and the in-flight commit decision that now runs the lifetime finalizer.
    /// </summary>
    [Collection("Sequential")]
    public class PredictedCrashCrewTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private readonly List<string> logLines = new List<string>();

        public PredictedCrashCrewTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
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

        private static Recording CrewedFlight(string id, bool predicted)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = "Flea " + id,
                TreeId = "tree-" + id,
                VesselPersistentId = 4242,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
            };
            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: predicted);
            return rec;
        }

        // ---------------------------------------------------------------- inference

        // catches: a predicted crash (Q6, retired) still sparing the crew Q6c kills.
        [Fact]
        public void InferCrewEndState_Destroyed_IsDead_PredictedOrNot()
        {
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, new HashSet<string> { Jeb }, remnantSurvives: false));
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, null));
            // The old three-argument shape (every recording written before the key) is Dead.
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Destroyed, new HashSet<string> { Jeb }));
            Assert.DoesNotContain(logLines, l => l.Contains("-> Aboard"));
        }

        // catches: a predicted crash leaving its kerbal alive with no death hold (Q6c).
        [Fact]
        public void PopulateCrewEndStates_PredictedCrash_IsADeathWithTheRespawnStamp()
        {
            KerbalsModule.CrewRespawnPolicyProviderForTesting =
                (out bool r, out double t) => { r = true; t = 7200.0; return true; };
            Recording rec = CrewedFlight("rec-pred-pop", predicted: true);

            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.True(rec.TerminalPredicted);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.True(rec.CrewDeathRespawns.HasValue);
        }

        [Fact]
        public void GhostOnlyChainSegment_PredictedCrash_TakesTheHandoffDeath()
        {
            Recording rec = CrewedFlight("rec-pred-chain", predicted: true);
            rec.ChainId = "chain-1";
            rec.ChainIndex = 1;

            Assert.True(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(rec));
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);

            // A surviving remnant (Q6b) is decided per part, never by the handoff.
            Recording remnant = CrewedFlight("rec-remnant-chain", predicted: true);
            remnant.StampTerminalState(TerminalState.Destroyed, "test", predicted: true, remnantSurvives: true);
            remnant.ChainId = "chain-3";
            remnant.ChainIndex = 1;
            Assert.False(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(remnant));

            Recording confirmed = CrewedFlight("rec-conf-chain", predicted: false);
            confirmed.ChainId = "chain-2";
            confirmed.ChainIndex = 1;
            Assert.True(KerbalsModule.ShouldUseGhostOnlyChainHandoffEndState(confirmed));
            KerbalsModule.PopulateCrewEndStates(confirmed);
            Assert.Equal(KerbalEndState.Dead, confirmed.CrewEndStates[Jeb]);
        }

        // ---------------------------------------------------------------- the stamp seam

        [Fact]
        public void Stamp_PredictedKeptOnlyWithDestroyed_AndEveryOtherStampClearsIt()
        {
            var rec = new Recording { RecordingId = "rec-seam" };
            // Recovered is never a prediction (Destroyed, a touchdown and a Q8 final orbit are).
            rec.StampTerminalState(TerminalState.Recovered, "test", predicted: true);
            Assert.False(rec.TerminalPredicted);

            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            Assert.True(rec.TerminalPredicted);

            rec.StampTerminalState(TerminalState.Destroyed, "confirm");
            Assert.False(rec.TerminalPredicted);

            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true);
            rec.StampTerminalState(null, "retract");
            Assert.False(rec.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("[Recording]")
                && l.Contains("terminal predicted flag true->false") && l.Contains("context=retract"));
        }

        [Fact]
        public void ConfirmingAPredictedCrash_KeepsTheCrewDead()
        {
            Recording rec = CrewedFlight("rec-confirm", predicted: true);
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);

            bool confirmed = ParsekFlight.ConfirmObservedDestruction(
                rec, destroyedDuringRecording: true, vesselMissingOnInFlightCommit: false, "test");

            Assert.True(confirmed);
            Assert.False(rec.TerminalPredicted);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("predicted crash of 'rec-confirm' confirmed"));
        }

        [Fact]
        public void ConfirmObservedDestruction_NoObservation_KeepsThePrediction()
        {
            Recording rec = CrewedFlight("rec-unobserved", predicted: true);
            Assert.False(ParsekFlight.ConfirmObservedDestruction(
                rec, destroyedDuringRecording: false, vesselMissingOnInFlightCommit: false, "test"));
            Assert.True(rec.TerminalPredicted);

            Assert.True(ParsekFlight.ConfirmObservedDestruction(
                rec, destroyedDuringRecording: false, vesselMissingOnInFlightCommit: true, "test"));
            Assert.False(rec.TerminalPredicted);
        }

        [Fact]
        public void MarkLifetimeFinalizerCrashPredicted_KeepsTheDeath()
        {
            Recording rec = CrewedFlight("rec-finalizer", predicted: false);
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);

            ParsekFlight.MarkLifetimeFinalizerCrashPredicted(rec, "test");

            Assert.True(rec.TerminalPredicted);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);

            var landed = new Recording { RecordingId = "rec-landed" };
            landed.StampTerminalState(TerminalState.Landed, "test");
            ParsekFlight.MarkLifetimeFinalizerCrashPredicted(landed, "test");
            Assert.False(landed.TerminalPredicted);
        }

        // ---------------------------------------------------------------- persistence

        [Fact]
        public void Codec_RoundTripsThePredictedFlag()
        {
            Recording rec = CrewedFlight("rec-codec", predicted: true);
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Equal("True", node.GetValue("terminalPredicted"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.Equal(TerminalState.Destroyed, restored.TerminalStateValue);
            Assert.True(restored.TerminalPredicted);
        }

        [Fact]
        public void Codec_RoundTripsTheRemnantFlag_SparseAndOnlyBesideDestroyed()
        {
            Recording rec = CrewedFlight("rec-codec-remnant", predicted: true);
            var plain = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(plain, rec);
            Assert.Null(plain.GetValue("terminalRemnantSurvives"));

            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true, remnantSurvives: true);
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Equal("True", node.GetValue("terminalRemnantSurvives"));
            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.True(restored.TerminalRemnantSurvives);

            var orbiting = new Recording { RecordingId = "rec-codec-remnant-orb" };
            orbiting.StampTerminalState(TerminalState.Orbiting, "test", remnantSurvives: true);
            Assert.False(orbiting.TerminalRemnantSurvives);
            var orbNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(orbNode, orbiting);
            orbNode.AddValue("terminalRemnantSurvives", "True");
            var orbRestored = new Recording();
            RecordingTree.LoadRecordingFrom(orbNode, orbRestored);
            Assert.False(orbRestored.TerminalRemnantSurvives);
        }

        [Fact]
        public void Codec_ConfirmedCrashWritesNoKey_AndAnAbsentKeyReadsConfirmed()
        {
            Recording rec = CrewedFlight("rec-codec-conf", predicted: false);
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            Assert.Null(node.GetValue("terminalPredicted"));

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.False(restored.TerminalPredicted);
        }

        [Fact]
        public void Codec_KeyBesideAnotherEnding_ReadsFalse()
        {
            // An ending no prediction writes (an Orbiting one may be a Q8 final orbit).
            var rec = new Recording { RecordingId = "rec-codec-orb" };
            rec.StampTerminalState(TerminalState.Docked, "test");
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            node.AddValue("terminalPredicted", "True");

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.False(restored.TerminalPredicted);
        }

        [Fact]
        public void RecordingBuilder_AuthorsThePredictedFlag()
        {
            ConfigNode node = new RecordingBuilder("Doomed Flea")
                .WithRecordingId("rec-builder-pred")
                .WithPredictedDestroyedTerminal()
                .BuildV3Metadata();
            Assert.Equal("True", node.GetValue("terminalPredicted"));
            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);
            Assert.True(restored.TerminalPredicted);
        }

        [Fact]
        public void CopySites_CarryTheFlag()
        {
            Recording rec = CrewedFlight("rec-copy", predicted: true);

            rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: true, remnantSurvives: true);

            Assert.True(Recording.DeepClone(rec).TerminalPredicted);
            Assert.True(Recording.DeepClone(rec).TerminalRemnantSurvives);

            var artifacts = new Recording();
            artifacts.ApplyPersistenceArtifactsFrom(rec);
            Assert.True(artifacts.TerminalPredicted);
            Assert.True(artifacts.TerminalRemnantSurvives);
        }

        // ---------------------------------------------------------------- cache applier

        private static Recording CacheTarget(string id)
        {
            var rec = new Recording { RecordingId = id, VesselName = id, VesselPersistentId = 77u };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            return rec;
        }

        private static RecordingFinalizationCache DestroyedCache(string id, string reason)
        {
            return new RecordingFinalizationCache
            {
                RecordingId = id,
                VesselPersistentId = 77u,
                Owner = FinalizationCacheOwner.BackgroundLoaded,
                Status = FinalizationCacheStatus.Fresh,
                CachedAtUT = 110.0,
                RefreshReason = reason,
                LastObservedUT = 110.0,
                TailStartsAtUT = 100.0,
                TerminalUT = 150.0,
                TerminalState = TerminalState.Destroyed,
                TerminalBodyName = "Kerbin",
            };
        }

        [Fact]
        public void Applier_PeriodicDestroyedCache_StampsAPrediction()
        {
            Recording rec = CacheTarget("rec-apply-pred");
            Assert.True(RecordingFinalizationCacheApplier.TryApply(
                rec, DestroyedCache("rec-apply-pred", "periodic"),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test" }, out _));
            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.True(rec.TerminalPredicted);
        }

        [Fact]
        public void Applier_DestroyEventCacheOrConfirmedCaller_StampsAConfirmedCrash()
        {
            Recording byEvent = CacheTarget("rec-apply-event");
            Assert.True(RecordingFinalizationCacheApplier.TryApply(
                byEvent, DestroyedCache("rec-apply-event", "background_destroy"),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test" }, out _));
            Assert.False(byEvent.TerminalPredicted);

            Recording byCaller = CacheTarget("rec-apply-caller");
            Assert.True(RecordingFinalizationCacheApplier.TryApply(
                byCaller, DestroyedCache("rec-apply-caller", "periodic"),
                new RecordingFinalizationCacheApplyOptions { ConsumerPath = "test", DestructionConfirmed = true },
                out _));
            Assert.False(byCaller.TerminalPredicted);
        }

        [Theory]
        [InlineData("destroy_event")]
        [InlineData("background_destroy")]
        [InlineData("periodic")]
        [InlineData("part_die")]
        [InlineData("")]
        [InlineData(null)]
        public void Applier_DestroyReasons_MatchTheProducers(string reason)
        {
            Assert.Equal(
                RecordingFinalizationCacheProducer.IsDestroyRefreshReason(reason),
                RecordingFinalizationCacheApplier.IsDestroyEventReason(reason));
        }

        [Fact]
        public void Applier_NonDestroyedCache_IsNeverAPrediction()
        {
            RecordingFinalizationCache cache = DestroyedCache("x", "periodic");
            cache.TerminalState = TerminalState.SubOrbital;
            Assert.False(RecordingFinalizationCacheApplier.IsPredictedDestroyedTerminal(
                cache, new RecordingFinalizationCacheApplyOptions()));
        }

        // ---------------------------------------------------------------- ledger rows

        // catches: a predicted crewed crash writing no Dead row and no reputation penalty (Q6c).
        [Fact]
        public void LedgerRows_PredictedCrewedCrash_DeadRowAndRepPenalty()
        {
            Recording rec = CrewedFlight("rec-ledger-pred", predicted: true);
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var evt = new GameStateEvent
            {
                ut = 300.0,
                eventType = GameStateEventType.ReputationChanged,
                key = KerbalDeathRepPenalty.VesselLossEventKey,
                valueBefore = 0.0,
                valueAfter = -10.0,
                recordingId = "rec-ledger-pred"
            };
            GameStateStore.AddEvent(ref evt);

            List<GameAction> rows = LedgerOrchestrator.CreateKerbalAssignmentActions(
                "rec-ledger-pred", 100.0, 300.0);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);

            List<GameAction> penalty = LedgerOrchestrator.CreateKerbalDeathRepPenaltyActions(
                "rec-ledger-pred", 100.0, 300.0, ReputationSeedOrigin.PreExisting);
            Assert.NotEmpty(penalty);
        }

        [Fact]
        public void LedgerRows_ConfirmedCrewedCrash_KeepsTheDeadRow()
        {
            Recording rec = CrewedFlight("rec-ledger-conf", predicted: false);
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            List<GameAction> rows = LedgerOrchestrator.CreateKerbalAssignmentActions(
                "rec-ledger-conf", 100.0, 300.0);
            Assert.Single(rows);
            Assert.Equal(KerbalEndState.Dead, rows[0].KerbalEndStateField);
        }

        // ---------------------------------------------------------------- B4 commit decision

        [Theory]
        // isSceneExit, vesselFound, isActive, abandon, expected
        [InlineData(true, false, true, false, true)]   // scene exit always runs it
        [InlineData(true, true, false, false, true)]
        [InlineData(false, false, false, true, false)] // in flight with no live vessel: never
        [InlineData(false, true, false, false, true)]  // non-active live leaf: the player is not flying it
        [InlineData(false, true, true, false, false)]  // plain commit keeps the active situation stamp
        [InlineData(false, true, true, true, true)]    // the pre-switch Merge abandons the active vessel
        public void ShouldRunLifetimeFinalizerForLeaf_TruthTable(
            bool isSceneExit, bool vesselFound, bool isActive, bool abandon, bool expected)
        {
            Assert.Equal(expected, ParsekFlight.ShouldRunLifetimeFinalizerForLeaf(
                isSceneExit, vesselFound, isActive, abandon));
        }

        [Fact]
        public void IsPredictedCrashCacheForLiveVessel_OnlyAFreshDestroyedCache()
        {
            RecordingFinalizationCache cache = DestroyedCache("x", "periodic");
            Assert.True(ParsekFlight.IsPredictedCrashCacheForLiveVessel(cache));
            cache.Status = FinalizationCacheStatus.Stale;
            Assert.False(ParsekFlight.IsPredictedCrashCacheForLiveVessel(cache));
            cache.Status = FinalizationCacheStatus.Fresh;
            cache.TerminalState = TerminalState.Orbiting;
            Assert.False(ParsekFlight.IsPredictedCrashCacheForLiveVessel(cache));
            Assert.False(ParsekFlight.IsPredictedCrashCacheForLiveVessel(null));
        }

        private static Vessel Stub(uint pid)
        {
            var vessel = (Vessel)FormatterServices.GetUninitializedObject(typeof(Vessel));
            vessel.persistentId = pid;
            vessel.situation = Vessel.Situations.SUB_ORBITAL;
            return vessel;
        }

        private sealed class Seams
        {
            internal int SituationStamps;
            internal int FinalizerRuns;
        }

        private static ParsekFlight.FinalizationLiveVesselAccess Access(Seams seams) =>
            new ParsekFlight.FinalizationLiveVesselAccess(
                isFound: v => !ReferenceEquals(v, null),
                determineTerminalState: v => { seams.SituationStamps++; return TerminalState.SubOrbital; },
                captureTerminalOrbit: (r, v) => { },
                captureTerminalPosition: (r, v) => { },
                tryBackupSnapshot: v => null,
                tryRefreshStableTerminalSnapshot: (r, v, s, p) => false,
                findLaterSpawnOwner: (r, v) => null,
                isRecordedIdentityLost: (r, v) => false);

        private static void InstallCrashingFinalizer(Seams seams)
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT,
                    out IncompleteBallisticFinalizationResult result) =>
                {
                    seams.FinalizerRuns++;
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = 260.0,
                    };
                    return true;
                };
        }

        private static Recording LiveLeaf(string id, uint pid, RecordingTree tree)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = id,
                VesselPersistentId = pid,
                TreeId = tree.Id,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, altitude = 5000.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 120.0, altitude = 4000.0, bodyName = "Kerbin" });
            tree.Recordings[id] = rec;
            return rec;
        }

        [UnityEntryFact]
        public void InFlightCommit_NonActiveLiveLeaf_GetsThePredictedCrash()
        {
            var seams = new Seams();
            InstallCrashingFinalizer(seams);
            var tree = new RecordingTree { Id = "b4-tree", TreeName = "B4", ActiveRecordingId = "b4-active" };
            LiveLeaf("b4-active", 9001, tree);
            Recording stage = LiveLeaf("b4-stage", 9002, tree);
            Vessel live = Stub(9002);

            ParsekFlight.FinalizeIndividualRecording(
                stage, 130.0, isSceneExit: false, treeContext: tree,
                findVesselByPid: pid => pid == 9002 ? live : null,
                liveVesselAccess: Access(seams));

            Assert.Equal(1, seams.FinalizerRuns);
            Assert.Equal(0, seams.SituationStamps);
            Assert.Equal(TerminalState.Destroyed, stage.TerminalStateValue);
            Assert.True(stage.TerminalPredicted);
            Assert.Contains(logLines, l => l.Contains("in-flight commit leaves live vessel pid=9002"));
        }

        [UnityEntryFact]
        public void PlainInFlightCommit_ActiveLiveLeaf_KeepsTheSituationStamp()
        {
            var seams = new Seams();
            InstallCrashingFinalizer(seams);
            var tree = new RecordingTree { Id = "b4p-tree", TreeName = "B4p", ActiveRecordingId = "b4p-active" };
            Recording active = LiveLeaf("b4p-active", 9101, tree);
            Vessel live = Stub(9101);

            ParsekFlight.FinalizeIndividualRecording(
                active, 130.0, isSceneExit: false, treeContext: tree,
                findVesselByPid: pid => pid == 9101 ? live : null,
                liveVesselAccess: Access(seams));

            Assert.Equal(0, seams.FinalizerRuns);
            Assert.Equal(1, seams.SituationStamps);
            Assert.Equal(TerminalState.SubOrbital, active.TerminalStateValue);
        }

        [UnityEntryFact]
        public void AbandoningInFlightCommit_ActiveLiveLeaf_GetsThePredictedCrash()
        {
            var seams = new Seams();
            InstallCrashingFinalizer(seams);
            var tree = new RecordingTree { Id = "b4a-tree", TreeName = "B4a", ActiveRecordingId = "b4a-active" };
            Recording active = LiveLeaf("b4a-active", 9201, tree);
            Vessel live = Stub(9201);

            ParsekFlight.FinalizeIndividualRecording(
                active, 130.0, isSceneExit: false, treeContext: tree,
                findVesselByPid: pid => pid == 9201 ? live : null,
                liveVesselAccess: Access(seams),
                abandonActiveVessel: true);

            Assert.Equal(1, seams.FinalizerRuns);
            Assert.Equal(0, seams.SituationStamps);
            Assert.True(active.TerminalPredicted);
        }

        // The flag reaches the active leaf through the tree finalize the commit runs.
        [UnityEntryFact]
        public void FinalizeTreeRecordingsAfterFlush_ThreadsTheAbandonFlagToTheActiveLeaf()
        {
            var seams = new Seams();
            InstallCrashingFinalizer(seams);
            var tree = new RecordingTree { Id = "b4t-tree", TreeName = "B4t", ActiveRecordingId = "b4t-active" };
            Recording active = LiveLeaf("b4t-active", 9501, tree);
            Vessel live = Stub(9501);

            ParsekFlight.FinalizeTreeRecordingsAfterFlush(
                tree, 130.0, isSceneExit: false,
                findVesselByPid: pid => pid == 9501 ? live : null,
                liveVesselAccess: Access(seams),
                abandonActiveVessel: true);

            Assert.Equal(1, seams.FinalizerRuns);
            Assert.True(active.TerminalPredicted);
        }

        [Fact]
        public void InFlightCommit_MissingVesselWithDestroyedCache_IsConfirmed()
        {
            var tree = new RecordingTree { Id = "b4m-tree", TreeName = "B4m", ActiveRecordingId = "other" };
            Recording gone = LiveLeaf("b4m-gone", 9301, tree);
            RecordingFinalizationCache cache = DestroyedCache("b4m-gone", "periodic");
            cache.VesselPersistentId = 9301;
            cache.TailStartsAtUT = 120.0;

            ParsekFlight.FinalizeIndividualRecording(
                gone, 160.0, isSceneExit: false, cache, tree,
                findVesselByPid: pid => null,
                liveVesselAccess: Access(new Seams()));

            Assert.Equal(TerminalState.Destroyed, gone.TerminalStateValue);
            Assert.False(gone.TerminalPredicted);
        }

        [Fact]
        public void SceneExit_MissingVesselWithDestroyedCache_StaysPredicted()
        {
            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT,
                    out IncompleteBallisticFinalizationResult result) =>
                {
                    result = default(IncompleteBallisticFinalizationResult);
                    return false;
                };
            var tree = new RecordingTree { Id = "b4s-tree", TreeName = "B4s", ActiveRecordingId = "other" };
            Recording unloaded = LiveLeaf("b4s-gone", 9401, tree);
            RecordingFinalizationCache cache = DestroyedCache("b4s-gone", "periodic");
            cache.VesselPersistentId = 9401;
            cache.TailStartsAtUT = 120.0;

            ParsekFlight.FinalizeIndividualRecording(
                unloaded, 160.0, isSceneExit: true, cache, tree,
                findVesselByPid: pid => null,
                liveVesselAccess: Access(new Seams()));

            Assert.Equal(TerminalState.Destroyed, unloaded.TerminalStateValue);
            Assert.True(unloaded.TerminalPredicted);
        }
    }
}
