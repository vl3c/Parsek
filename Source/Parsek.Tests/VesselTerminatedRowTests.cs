using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SPAWNED-VESSEL-TERMINATED-RESPAWNS: a player Terminate / Delete (Tracking Station) of a
    /// vessel that continues a committed recording writes a non-economic
    /// <see cref="GameActionType.VesselTerminated"/> row, the termination twin of the
    /// <see cref="GameActionType.VesselRecovered"/> row. The next flight scene's
    /// <see cref="ParsekPlaybackPolicy.RunSpawnDeathChecks"/> reads the vessel's absence as a
    /// removal, not a death, and the end-spawn gate refuses, so the vessel is never spawned
    /// back. A vessel that vanished with no evidence is still a spawn death, and the death cap
    /// is persisted (<c>spawnDeathCount</c>) so it binds across scene loads.
    /// </summary>
    [Collection("Sequential")]
    public class VesselTerminatedRowTests : IDisposable
    {
        private const string Name = "Verify Rover";
        private const uint CraftPid = 5151u;
        private const uint SpawnPid = 4242u;
        private const string LaunchGuid = "5d1f0b6a2c7e4e0f9a3b8c1d2e4f6a7b";
        private const string SpawnGuid = "abcdefabcdefabcdefabcdefabcdefab";
        private const double TerminateUT = 2500.0;

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppressCrewEvents;

        public VesselTerminatedRowTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            priorSuppressCrewEvents = GameStateRecorder.SuppressCrewEvents;
            GameStateRecorder.SuppressCrewEvents = false;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            KspStatePatcher.SuppressUnityCallsForTesting = true;
            GameStateStore.SuppressLogging = true;
            GameStateStore.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            EffectiveState.ResetRecoveredIndexForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.SuppressCrewEvents = priorSuppressCrewEvents;
            EffectiveState.ResetRecoveredIndexForTesting();
            EffectiveState.ResetCachesForTesting();
            Ledger.ResetForTesting();
            LedgerOrchestrator.ResetForTesting();
            KspStatePatcher.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingStore.SuppressLogging = false;
            GameStateStore.ResetForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static Recording SpawnedLandedEnd(string id)
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("name", Name);
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Probe");
            return new Recording
            {
                RecordingId = id,
                VesselName = Name,
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 1000,
                ExplicitEndUT = 2000,
                VesselSnapshot = snapshot,
                TerminalStateValue = TerminalState.Landed,
                PlaybackEnabled = true,
                VesselSpawned = true,
                SpawnedVesselPersistentId = SpawnPid,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 1000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.4, altitude = 70 },
                    new TrajectoryPoint { ut = 2000, bodyName = "Kerbin", latitude = -0.09, longitude = -74.4, altitude = 70 }
                }
            };
        }

        private static List<GameAction> RowsOfType(GameActionType type)
        {
            var result = new List<GameAction>();
            var actions = Ledger.Actions;
            for (int i = 0; i < actions.Count; i++)
                if (actions[i].Type == type) result.Add(actions[i]);
            return result;
        }

        private static ParsekPlaybackPolicy NewPolicy()
        {
            var host = (ParsekFlight)FormatterServices.GetUninitializedObject(typeof(ParsekFlight));
            return new ParsekPlaybackPolicy(new GhostPlaybackEngine(null), host);
        }

        // The filed chain: the player terminates the vessel Parsek spawned at the recording's
        // end in the Tracking Station, then enters flight. Before the row the death check
        // reset the claim and the end spawn put the vessel back.
        [Fact]
        public void TerminatedSpawnedEndVessel_IsNotRespawnedInTheNextFlightScene()
        {
            var rec = SpawnedLandedEnd("rec-term");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselTerminated(TerminateUT, SpawnPid, SpawnGuid, Name));
            var rows = RowsOfType(GameActionType.VesselTerminated);
            Assert.Single(rows);
            Assert.Equal("rec-term", rows[0].RecordingId);
            Assert.Equal(SpawnPid, rows[0].RecoveredVesselPid);
            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Vessel terminated recorded: vessel='Verify Rover' pid=4242")
                && l.Contains("recordingId=rec-term"));

            NewPolicy().RunSpawnDeathChecks();

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn-death detected"));
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("Spawned vessel terminated by the player: #0 \"Verify Rover\" pid=4242"));

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.False(needsSpawn);
            Assert.Contains("terminated by the player after commit", reason);
        }

        // A termination is not a recovery: nothing that reads recovery evidence sees it.
        [Fact]
        public void TerminatedRow_IsNotRecoveryEvidence()
        {
            var rec = SpawnedLandedEnd("rec-term");
            var row = new GameAction
            {
                UT = TerminateUT, Type = GameActionType.VesselTerminated,
                RecordingId = rec.RecordingId, RecoveredVesselName = Name, RecoveredVesselPid = SpawnPid,
            };
            var ledger = new List<GameAction> { row };

            Assert.False(RecoveredRecordingEvidence.IsRecoveryRow(row, rec.RecordingId, double.NegativeInfinity));
            Assert.False(RecoveredRecordingEvidence.IsRecovered(rec, ledger, out _));
            Assert.True(RecoveredRecordingEvidence.IsTerminatedByLedgerRow(rec, ledger));

            // A non-spawnable terminal already refuses on its own; the row adds nothing there.
            rec.TerminalStateValue = TerminalState.Destroyed;
            Assert.False(RecoveredRecordingEvidence.IsTerminatedByLedgerRow(rec, ledger));
        }

        [Fact]
        public void ProgrammaticRemoval_UnderCrewSuppression_WritesNothing()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedLandedEnd("rec-term"));
            GameStateRecorder.SuppressCrewEvents = true;

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselTerminated(TerminateUT, SpawnPid, SpawnGuid, Name));
            Assert.Empty(RowsOfType(GameActionType.VesselTerminated));
        }

        [Fact]
        public void SameTerminationDeliveredTwice_WritesOneRow()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedLandedEnd("rec-term"));

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselTerminated(TerminateUT, SpawnPid, SpawnGuid, Name));
            Assert.Equal(0, LedgerOrchestrator.OnRealVesselTerminated(TerminateUT, SpawnPid, SpawnGuid, Name));
            Assert.Single(RowsOfType(GameActionType.VesselTerminated));
        }

        // A genuine spawn death (no evidence: the vessel blew up on arrival) still retries
        // within the cap, and the count is persisted so the cap binds across loads.
        [Fact]
        public void GenuineSpawnDeath_StillRetries_AndTheCountSurvivesASaveLoad()
        {
            var rec = SpawnedLandedEnd("rec-death");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NewPolicy().RunSpawnDeathChecks();
            Assert.Equal(1, rec.SpawnDeathCount);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("Spawn-death detected: #0 \"Verify Rover\""));

            var node = new ConfigNode("RECORDING");
            RecordingTreeRecordCodec.SaveRecordingInto(node, rec);
            Assert.Equal("1", node.GetValue("spawnDeathCount"));

            // In-session load: the reset zeroes it, the restore puts the saved value back.
            rec.ClearTransientSpawnState();
            ParsekScenario.RestorePersistedSpawnDeathCount(rec, node);
            Assert.Equal(1, rec.SpawnDeathCount);
        }

        // An abandon at the cap is not lost on a reload: the restored count keeps the
        // recording abandoned without counting the same loss again, and nothing spawns.
        [Fact]
        public void PersistedDeathCapReached_KeepsTheRecordingAbandonedAfterReload()
        {
            var rec = SpawnedLandedEnd("rec-capped");
            rec.SpawnDeathCount = VesselSpawner.MaxSpawnDeathCycles;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NewPolicy().RunSpawnDeathChecks();

            Assert.True(rec.SpawnAbandoned);
            Assert.True(rec.VesselSpawned);
            Assert.Equal(VesselSpawner.MaxSpawnDeathCycles, rec.SpawnDeathCount);
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("Spawn-death cap already reached: #0 \"Verify Rover\""));
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
        }

        [Fact]
        public void TerminatedRow_RetiredBySupersede_SkippedByTimeline_AndNamedByTheDisplay()
        {
            var row = new GameAction
            {
                UT = TerminateUT, Type = GameActionType.VesselTerminated,
                RecordingId = "rec-term", RecoveredVesselName = Name, RecoveredVesselPid = SpawnPid,
            };
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(row));
            Assert.False(SupersedeCommit.IsWorldStateChangingRecordingAction(row, new List<GameAction>()));
            Assert.False(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.VesselTerminated));
            Assert.True(TimelineBuilder.IsSpawnEvidenceOnlyActionType(GameActionType.VesselTerminated));
            Assert.Equal("Vessel terminated: Verify Rover", GameActionDisplay.GetDescription(row, null));
        }
    }
}
