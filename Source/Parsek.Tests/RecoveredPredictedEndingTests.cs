using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// A recording that ended in a Destroyed Parsek only predicted (a crash forecast, or an
    /// identity loss over a remnant KSP keeps) whose vessel the player then RECOVERS before
    /// the commit (Tracking Station / KSC recover, or the in-flight Recover) ends Recovered
    /// with its crew recovered: the prediction was wrong, the recovery is what happened. A
    /// destruction KSP carried out stays final.
    /// </summary>
    [Collection("Sequential")]
    public class RecoveredPredictedEndingTests : IDisposable
    {
        private const string Jeb = "Jebediah Kerman";
        private const string Name = "Crewed Stage";
        private const uint Pid = 2905720181u;
        private const string LaunchGuid = "0123456789abcdef0123456789abcdef";
        private readonly List<string> logLines = new List<string>();

        public RecoveredPredictedEndingTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            InFlightRecoveryRequest.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            CrewReservationManager.ResetReplacementsForTesting();
            KerbalsModule.CrewRespawnPolicyProviderForTesting = null;
            KerbalsModule.EnsureTerminalStampObserverInstalled();
        }

        public void Dispose()
        {
            InFlightRecoveryRequest.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            GameStateStore.ResetForTesting();
            RecordingStore.ResetForTesting();
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

        private static Recording CrewedStage(bool predicted, bool remnant = false)
        {
            var rec = new Recording
            {
                RecordingId = "rec-stage",
                TreeId = "tree-stage",
                VesselName = Name,
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                GhostVisualSnapshot = CrewSnapshot(Jeb),
                ExplicitStartUT = 100.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin" });
            if (remnant)
                rec.MarkDestroyedAtTerminal(160.0, "test.identityLost", vesselSurvives: true);
            else
            {
                rec.StampTerminalState(TerminalState.Destroyed, "test", predicted: predicted);
                rec.VesselDestroyed = true;
                rec.ExplicitEndUT = 400.0;
            }
            KerbalsModule.PopulateCrewEndStates(rec);
            return rec;
        }

        private static RecordingTree TreeOf(Recording rec)
        {
            var tree = new RecordingTree
            {
                Id = rec.TreeId, TreeName = Name, RootRecordingId = rec.RecordingId,
                ActiveRecordingId = rec.RecordingId,
            };
            tree.Recordings[rec.RecordingId] = rec;
            return tree;
        }

        [Fact]
        public void CanOverwriteTerminalState_APredictedDestroyedYieldsToARecovery()
        {
            Assert.True(ParsekScenario.CanOverwriteTerminalState(
                TerminalState.Destroyed, TerminalState.Recovered, existingPredicted: true));
            Assert.False(ParsekScenario.CanOverwriteTerminalState(
                TerminalState.Destroyed, TerminalState.Recovered, existingPredicted: false));
            Assert.False(ParsekScenario.CanOverwriteTerminalState(
                TerminalState.Destroyed, TerminalState.Recovered));
            // A predicted crash is not re-stamped by another destruction event here.
            Assert.False(ParsekScenario.CanOverwriteTerminalState(
                TerminalState.Destroyed, TerminalState.Destroyed, existingPredicted: true));
        }

        // catches: a Tracking Station recovery of a stage whose pending recording ended in a
        // predicted crash leaving the crash standing, so the commit kills the recovered crew.
        [Fact]
        public void TrackingStationRecovery_PendingPredictedCrash_EndsRecovered_CrewRecovered()
        {
            Recording rec = CrewedStage(predicted: true);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
            PendingTreeSlot.StashPendingTree(TreeOf(rec));

            bool updated = ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(Name, LaunchGuid, Pid), TerminalState.Recovered, 900.0, Pid);

            Assert.True(updated);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.False(rec.TerminalPredicted);
            Assert.False(rec.VesselDestroyed);
            Assert.Equal(900.0, rec.ExplicitEndUT);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            Assert.Contains(logLines, l => l.Contains("[Scenario]")
                && l.Contains("predicted Destroyed of 'rec-stage' overwritten by Recovered"));
        }

        // The identity-loss remnant (Q6b) the player recovers: its crew come home too.
        [Fact]
        public void Recovery_IdentityLossRemnant_EndsRecovered()
        {
            Recording rec = CrewedStage(predicted: true, remnant: true);
            PendingTreeSlot.StashPendingTree(TreeOf(rec));

            Assert.True(ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(Name, LaunchGuid, Pid), TerminalState.Recovered, 900.0, Pid));

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.False(rec.TerminalRemnantSurvives);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
        }

        // catches (review B2): a recovered breakup remnant bringing home every start-crew
        // kerbal, including the one whose part stock destroyed (stock killed him; the recovery
        // overwrite dropped the remnant's per-part answer and the Recovered branch recovered
        // everyone). The kerbal in the surviving pod comes home, the one in the destroyed
        // cabin stays dead.
        [Fact]
        public void Recovery_IdentityLossRemnant_KerbalInADestroyedPartStaysDead()
        {
            const string Bob = "Bob Kerman";
            const uint PodPid = 1001, CabinPid = 2002;
            var start = new ConfigNode("VESSEL");
            ConfigNode pod = start.AddNode("PART");
            pod.AddValue("persistentId", PodPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            pod.AddValue("crew", Jeb);
            ConfigNode cabin = start.AddNode("PART");
            cabin.AddValue("persistentId", CabinPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            cabin.AddValue("crew", Bob);
            var rec = new Recording
            {
                RecordingId = "rec-stage",
                TreeId = "tree-stage",
                VesselName = Name,
                VesselPersistentId = Pid,
                RecordedVesselGuid = LaunchGuid,
                GhostVisualSnapshot = start,
                ExplicitStartUT = 100.0,
            };
            rec.Points.Add(new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin" });
            rec.Points.Add(new TrajectoryPoint { ut = 160.0, bodyName = "Kerbin" });
            rec.PartEvents.Add(new PartEvent
            {
                ut = 150.0, partPersistentId = CabinPid, eventType = PartEventType.Destroyed, partName = "cabin",
            });
            rec.MarkDestroyedAtTerminal(160.0, "test.identityLost", vesselSurvives: true);
            KerbalsModule.PopulateCrewEndStates(rec);
            Assert.Equal(KerbalEndState.Aboard, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Bob]);
            PendingTreeSlot.StashPendingTree(TreeOf(rec));

            Assert.True(ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(Name, LaunchGuid, Pid), TerminalState.Recovered, 900.0, Pid));
            KerbalsModule.PopulateCrewEndStates(rec);

            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Bob]);
            Assert.Contains(logLines, l => l.Contains("[KerbalsModule]")
                && l.Contains("crew='Bob Kerman'") && l.Contains("terminalState=Recovered")
                && l.Contains("part destroyed -> Dead"));
        }

        // The per-part answer under Recovered: a kerbal whose part KSP destroyed is dead
        // whatever happened to the rest of the vessel.
        [Fact]
        public void InferCrewEndState_Recovered_PartDestroyed_IsDead()
        {
            Assert.Equal(KerbalEndState.Dead, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Recovered, null, partDestroyed: true));
            Assert.Equal(KerbalEndState.Recovered, KerbalsModule.InferCrewEndState(
                Jeb, TerminalState.Recovered, null));
        }

        // A destruction KSP carried out is final: a same-name recovery never revives it.
        [Fact]
        public void Recovery_ConfirmedCrash_StaysDestroyed()
        {
            Recording rec = CrewedStage(predicted: false);
            PendingTreeSlot.StashPendingTree(TreeOf(rec));

            Assert.False(ParsekScenario.UpdateRecordingsForTerminalEvent(
                RecoveredVesselIdentity.FromRawName(Name, LaunchGuid, Pid), TerminalState.Recovered, 900.0, Pid));

            Assert.Equal(TerminalState.Destroyed, rec.TerminalStateValue);
            Assert.Equal(KerbalEndState.Dead, rec.CrewEndStates[Jeb]);
        }

        // catches: the in-flight Recover refusing a recording whose ending is a prediction.
        [Fact]
        public void InFlightRecover_PredictedCrash_StampsRecovered()
        {
            Recording rec = CrewedStage(predicted: true);
            RecordingTree tree = TreeOf(rec);

            InFlightRecoveryRequest.Arm(Pid, LaunchGuid, Name, 900.0);
            int stamped = InFlightRecoveryRequest.ApplyAtSceneExit(tree, GameScenes.SPACECENTER, "test");

            Assert.Equal(1, stamped);
            Assert.Equal(TerminalState.Recovered, rec.TerminalStateValue);
            Assert.False(rec.VesselDestroyed);
            Assert.Equal(KerbalEndState.Recovered, rec.CrewEndStates[Jeb]);
        }
    }
}
