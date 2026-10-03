using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// SPAWNED-VESSEL-RECOVERED-OUTSIDE-FLIGHT-RESPAWNS-ON-SANDBOX (owner rulings 2026-10-03):
    /// a player recovery of a vessel that continues a committed recording writes a
    /// non-economic <see cref="GameActionType.VesselRecovered"/> row in every game mode,
    /// <see cref="RecoveredRecordingEvidence"/> reads it beside the funds and crew-close rows,
    /// and <see cref="ParsekPlaybackPolicy.RunSpawnDeathChecks"/> reads the recovered
    /// vessel's absence as a recovery rather than a death.
    ///
    /// <para>The shape these pin is the sandbox one CI-6 measured: Real Spawn of a chain tip,
    /// the player recovers it outside flight with no crew aboard, so stock raises no
    /// <c>FundsChanged(VesselRecovery)</c> (no funds row) and nothing closes a crew hold (no
    /// crew-close row). Before the row existed the next flight scene reset the tip and
    /// spawned it again.</para>
    /// </summary>
    [Collection("Sequential")]
    public class VesselRecoveredRowTests : IDisposable
    {
        private const string Name = "CTR Lander";
        private const uint CraftPid = 1344998135u;
        private const uint SpawnPid = 2718281828u;
        private const string LaunchGuid = "5d1f0b6a2c7e4e0f9a3b8c1d2e4f6a7b";
        private const string SpawnGuid = "abcdefabcdefabcdefabcdefabcdefab";
        private const string OtherLaunchGuid = "0f0e0d0c0b0a09080706050403020100";
        private const double RecoveryUT = 400.0;

        private readonly List<string> logLines = new List<string>();
        private readonly bool priorSuppressCrewEvents;

        public VesselRecoveredRowTests()
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
            RecoveredRecordingEvidence.ResetForTesting();
            RewindContext.ResetForTesting();
            GhostMapPresence.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekScenario.SetOnLoadInProgressForTesting(false);
            CrewReservationManager.ResetReplacementsForTesting();
        }

        public void Dispose()
        {
            GameStateRecorder.SuppressCrewEvents = priorSuppressCrewEvents;
            RecoveredRecordingEvidence.ResetForTesting();
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

        // ------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------

        private static ConfigNode LandedSnapshot()
        {
            var snapshot = new ConfigNode("VESSEL");
            snapshot.AddValue("sit", "LANDED");
            snapshot.AddValue("type", "Ship");
            snapshot.AddValue("name", Name);
            return snapshot;
        }

        /// <summary>A committed, uncrewed, landed chain tip ending at UT 300.</summary>
        private static Recording LandedTip(string id, string treeId = null)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = treeId,
                VesselName = Name,
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = LaunchGuid,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 300.0,
                VesselSnapshot = LandedSnapshot(),
                TerminalStateValue = TerminalState.Landed,
                PlaybackEnabled = true,
                Points = new List<TrajectoryPoint>
                {
                    new TrajectoryPoint { ut = 100.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120 },
                    new TrajectoryPoint { ut = 300.0, bodyName = "Kerbin", latitude = 1.5, longitude = -70.0, altitude = 120 },
                },
            };
        }

        /// <summary>The tip after Real Spawn: a genuine (KSP-unique) spawn pid claimed.</summary>
        private static Recording SpawnedTip(string id)
        {
            var rec = LandedTip(id);
            rec.VesselSpawned = true;
            rec.SpawnedVesselPersistentId = SpawnPid;
            return rec;
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

        private static GameAction VesselRecoveredRow(string recordingId, double ut = RecoveryUT)
        {
            return new GameAction
            {
                UT = ut,
                Type = GameActionType.VesselRecovered,
                RecordingId = recordingId,
                RecoveredVesselName = Name,
                RecoveredVesselPid = SpawnPid,
            };
        }

        // ------------------------------------------------------------------
        // Writer
        // ------------------------------------------------------------------

        // catches: the filed defect - a sandbox, uncrewed Tracking Station recovery of a
        // genuine Parsek spawn leaving no ledger trace at all.
        [Fact]
        public void SandboxUncrewedRecoveryOfAParsekSpawn_WritesOneRowTaggedToTheTip()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));

            int written = LedgerOrchestrator.OnRealVesselRecovered(
                RecoveryUT, SpawnPid, SpawnGuid, Name);

            Assert.Equal(1, written);
            var rows = RowsOfType(GameActionType.VesselRecovered);
            Assert.Single(rows);
            Assert.Equal("rec-tip", rows[0].RecordingId);
            Assert.Equal(RecoveryUT, rows[0].UT);
            Assert.Equal(Name, rows[0].RecoveredVesselName);
            Assert.Equal(SpawnPid, rows[0].RecoveredVesselPid);
            Assert.NotEqual(0, rows[0].Sequence);
            Assert.Empty(RowsOfType(GameActionType.FundsEarning));
            Assert.Empty(RowsOfType(GameActionType.KerbalRecovered));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Vessel recovered recorded: vessel='CTR Lander' pid=2718281828")
                && l.Contains("recoveryUT=400.0 recordingId=rec-tip"));
            Assert.Contains(logLines, l => l.Contains("[LedgerOrchestrator]")
                && l.Contains("Vessel recovery rows:")
                && l.Contains("owners=1 written=1 deduped=0"));
        }

        [Fact]
        public void PositiveLaunchGuidMatch_WritesARow()
        {
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-landed"));

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselRecovered(
                RecoveryUT, CraftPid, LaunchGuid, Name));

            Assert.Equal("rec-landed", RowsOfType(GameActionType.VesselRecovered)[0].RecordingId);
        }

        // catches: the craft-baked pid matching a DIFFERENT launch of the same craft.
        [Theory]
        [InlineData(OtherLaunchGuid)]
        [InlineData(null)]
        public void CraftPidWithoutAPositiveLaunchMatch_WritesNothing(string liveGuid)
        {
            RecordingStore.AddRecordingWithTreeForTesting(LandedTip("rec-landed"));

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselRecovered(
                RecoveryUT, CraftPid, liveGuid, Name));

            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
            Assert.Contains(logLines, l => l.Contains("no committed recording continues this vessel"));
        }

        [Fact]
        public void ZeroPid_WritesNothing()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT, 0u, LaunchGuid, Name));

            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
            Assert.Contains(logLines, l => l.Contains("Vessel recovery row skipped") && l.Contains("no vessel pid"));
        }

        // catches: the row tagging a recording the pending tree still owns (its own terminal
        // stamp records the recovery; ERS excludes it).
        [Fact]
        public void PendingTreeOnlyOwner_WritesNothing()
        {
            var pending = LandedTip("rec-pending", "tree-pending");
            var tree = new RecordingTree { Id = "tree-pending", TreeName = Name, RootRecordingId = pending.RecordingId };
            tree.AddOrReplaceRecording(pending);
            RecordingStore.StashPendingTree(tree);

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselRecovered(
                RecoveryUT, CraftPid, LaunchGuid, Name));

            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
        }

        // catches: a recording that ends AFTER the recovery (a later flight of the vessel)
        // being read as recovered.
        [Fact]
        public void OwnerEndingAfterTheRecovery_WritesNothing()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));

            Assert.Equal(0, LedgerOrchestrator.OnRealVesselRecovered(250.0, SpawnPid, SpawnGuid, Name));

            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
        }

        // catches: the same recovery event (or a replayed delivery of it) writing twice.
        [Fact]
        public void SameRecoveryDeliveredTwice_WritesOneRow()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));

            Assert.Equal(1, LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT, SpawnPid, SpawnGuid, Name));
            Assert.Equal(0, LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT + 0.05, SpawnPid, SpawnGuid, Name));

            Assert.Single(RowsOfType(GameActionType.VesselRecovered));
            Assert.Contains(logLines, l => l.Contains("recordingId=rec-tip") && l.Contains("already in the ledger"));
            Assert.Contains(logLines, l => l.Contains("owners=1 written=0 deduped=1"));
        }

        // catches: Parsek's own programmatic recoveries (SuppressionGuard.Crew()) writing
        // evidence for a recovery the player never performed (ruling 3).
        [Fact]
        public void ProgrammaticRecovery_UnderCrewSuppression_WritesNothing()
        {
            RecordingStore.AddRecordingWithTreeForTesting(SpawnedTip("rec-tip"));

            int written;
            using (SuppressionGuard.Crew())
            {
                written = LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT, SpawnPid, SpawnGuid, Name);
            }

            Assert.Equal(0, written);
            Assert.Empty(RowsOfType(GameActionType.VesselRecovered));
            Assert.Contains(logLines, l => l.Contains("Vessel recovery row skipped")
                && l.Contains("crew events suppressed (programmatic recovery)"));

            // Mirror: the same call outside the guard is a player recovery and writes.
            Assert.Equal(1, LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT, SpawnPid, SpawnGuid, Name));
        }

        // ------------------------------------------------------------------
        // Evidence + spawn gate
        // ------------------------------------------------------------------

        [Fact]
        public void Evidence_VesselRecoveredRowOnly_ReadsRecovered()
        {
            var rec = LandedTip("rec-tip");
            var ledger = new List<GameAction> { VesselRecoveredRow("rec-tip") };

            Assert.True(RecoveredRecordingEvidence.IsRecovered(rec, ledger, out string evidence));
            Assert.Equal(RecoveredRecordingEvidence.EvidenceLedgerRow, evidence);
            Assert.True(RecoveredRecordingEvidence.IsRecoveredByLedgerRow(rec, ledger));
            Assert.False(RecoveredRecordingEvidence.IsRecoveredByLedgerRow(
                LandedTip("rec-other"), ledger));
        }

        // catches: the sandbox respawn - with only the VesselRecovered row the end-of-recording
        // spawn must refuse.
        [Fact]
        public void Spawn_VesselRecoveredRowOnly_DoesNotSpawn()
        {
            var rec = LandedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);

            Ledger.AddAction(VesselRecoveredRow("rec-tip"));

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.False(needsSpawn);
            Assert.Contains("recovered after commit", reason);
        }

        // Ruling 2: no UT filter. A rewind to before the recovery still refuses the spawn.
        [Fact]
        public void Spawn_RowLaterThanTheCurrentClock_StillRefuses()
        {
            var rec = LandedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(VesselRecoveredRow("rec-tip", ut: 1.0e9));

            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
        }

        // catches: a Re-Fly-retired (tombstoned) row still refusing the spawn.
        [Fact]
        public void Spawn_TombstonedVesselRecoveredRow_NoLongerCounts()
        {
            var rec = LandedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            var row = VesselRecoveredRow("rec-tip");
            Ledger.AddAction(row);
            Assert.False(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);

            var scenario = new ParsekScenario
            {
                LedgerTombstones = new List<LedgerTombstone>
                {
                    new LedgerTombstone { TombstoneId = "tomb-1", ActionId = row.ActionId, UT = 500.0 },
                },
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpTombstoneStateVersion();

            Assert.True(GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false).needsSpawn);
        }

        // ------------------------------------------------------------------
        // RunSpawnDeathChecks: a recovery is not a death
        // ------------------------------------------------------------------

        // The whole filed chain: Real Spawn, sandbox uncrewed recovery outside flight, the
        // next flight scene. The spawned pid is gone (no FlightGlobals in a unit test), the
        // row says it was recovered, so the claim clears without a death and the gate refuses.
        [Fact]
        public void EndToEnd_RecoveryRow_ThenFlightScene_ClearsClaimAndNeverRespawns()
        {
            var rec = SpawnedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Assert.Equal(1, LedgerOrchestrator.OnRealVesselRecovered(RecoveryUT, SpawnPid, SpawnGuid, Name));

            NewPolicy().RunSpawnDeathChecks();

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.False(rec.SpawnAbandoned);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn-death detected"));
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("Spawned vessel recovered: #0 \"CTR Lander\" pid=2718281828")
                && l.Contains("evidence=recovery-row - spawn claim cleared, no respawn"));
            Assert.Contains(logLines, l => l.Contains("[Policy]")
                && l.Contains("RunSpawnDeathChecks: 0 death(s) detected, 0 abandoned, 1 recovered"));

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(rec, false);
            Assert.False(needsSpawn);
            Assert.Contains("recovered after commit", reason);

            // A second pass has nothing left to check.
            logLines.Clear();
            NewPolicy().RunSpawnDeathChecks();
            Assert.DoesNotContain(logLines, l => l.Contains("Spawned vessel recovered"));
        }

        // Mirror direction: with no recovery evidence the vanished vessel is still a death.
        [Fact]
        public void NoRecoveryEvidence_VanishedVessel_IsStillADeath()
        {
            var rec = SpawnedTip("rec-tip");
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            NewPolicy().RunSpawnDeathChecks();

            Assert.Equal(1, rec.SpawnDeathCount);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("Spawn-death detected: #0 \"CTR Lander\""));
            Assert.DoesNotContain(logLines, l => l.Contains("Spawned vessel recovered"));
        }

        // Recovered wins over the terminal-orbit branch: an Orbiting tip whose spawn was
        // recovered must not be marked cannot-spawn-safely or counted as a death.
        [Fact]
        public void OrbitingTip_RecoveredByRow_TakesTheRecoveredBranch()
        {
            var rec = SpawnedTip("rec-orbit");
            rec.TerminalStateValue = TerminalState.Orbiting;
            rec.TerminalOrbitBody = "Kerbin";
            rec.TerminalOrbitSemiMajorAxis = 700000.0;
            RecordingStore.AddRecordingWithTreeForTesting(rec);
            Ledger.AddAction(VesselRecoveredRow("rec-orbit"));

            NewPolicy().RunSpawnDeathChecks();

            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.False(rec.TerminalSpawnCannotSpawnSafely);
            Assert.False(rec.VesselSpawned);
            Assert.DoesNotContain(logLines, l => l.Contains("Spawn-death detected for terminal orbit"));
            Assert.Contains(logLines, l => l.Contains("Spawned vessel recovered: #0"));
        }

        [Fact]
        public void TryClearRecoveredSpawnClaim_RecoveredByRow_ClearsClaimOnly()
        {
            var rec = SpawnedTip("rec-tip");
            rec.SpawnAttempts = 2;
            rec.SpawnDeathCount = 1;

            Assert.True(ParsekPlaybackPolicy.TryClearRecoveredSpawnClaim(
                rec, r => true, out uint priorPid, out string evidence));

            Assert.Equal(SpawnPid, priorPid);
            Assert.Equal(RecoveredRecordingEvidence.EvidenceLedgerRow, evidence);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(2, rec.SpawnAttempts);
            Assert.Equal(1, rec.SpawnDeathCount);
            Assert.False(rec.SpawnAbandoned);
        }

        [Fact]
        public void TryClearRecoveredSpawnClaim_RecoveredTerminal_ClearsWithoutReadingTheLedger()
        {
            var rec = SpawnedTip("rec-tip");
            rec.TerminalStateValue = TerminalState.Recovered;
            bool ledgerRead = false;

            Assert.True(ParsekPlaybackPolicy.TryClearRecoveredSpawnClaim(
                rec, r => { ledgerRead = true; return false; }, out _, out string evidence));

            Assert.Equal(RecoveredRecordingEvidence.EvidenceTerminal, evidence);
            Assert.False(ledgerRead);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void TryClearRecoveredSpawnClaim_NotRecovered_TouchesNothing()
        {
            var rec = SpawnedTip("rec-tip");

            Assert.False(ParsekPlaybackPolicy.TryClearRecoveredSpawnClaim(
                rec, r => false, out uint priorPid, out string evidence));

            Assert.Equal(SpawnPid, priorPid);
            Assert.Null(evidence);
            Assert.True(rec.VesselSpawned);
            Assert.Equal(SpawnPid, rec.SpawnedVesselPersistentId);
            Assert.False(ParsekPlaybackPolicy.TryClearRecoveredSpawnClaim(null, r => true, out _, out _));
        }

        // ------------------------------------------------------------------
        // Re-Fly supersede + strict-block classification
        // ------------------------------------------------------------------

        [Fact]
        public void SupersedeTombstoneEligible_AndNeverStrictBlocks()
        {
            var row = VesselRecoveredRow("rec-tip");
            Assert.True(TombstoneEligibility.IsSupersedeTombstoneEligible(row));
            Assert.False(SupersedeCommit.IsWorldStateChangingRecordingAction(row, new List<GameAction>()));
            Assert.False(LedgerOrchestrator.IsResourceImpactingAction(GameActionType.VesselRecovered));
        }

        [Fact]
        public void TimelineSkipsTheRow_AndTheDisplayNamesTheVessel()
        {
            Assert.True(TimelineBuilder.IsSpawnEvidenceOnlyActionType(GameActionType.VesselRecovered));
            Assert.False(TimelineBuilder.IsSpawnEvidenceOnlyActionType(GameActionType.KerbalRecovered));
            Assert.Equal("Vessel recovered: CTR Lander",
                GameActionDisplay.GetDescription(VesselRecoveredRow("rec-tip"), null));
        }
    }
}
