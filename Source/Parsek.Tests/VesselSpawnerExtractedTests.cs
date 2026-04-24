using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for methods extracted from VesselSpawner:
    /// DetermineSituation (pure decision logic).
    /// Also verifies logging added to spawn paths.
    /// </summary>
    [Collection("Sequential")]
    public class VesselSpawnerExtractedTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public VesselSpawnerExtractedTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
        }

        public void Dispose()
        {
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
        }

        #region Source vessel materialization guard

        [Fact]
        public void TryAdoptExistingSourceVesselForSpawn_SourceExists_AdoptsSourcePid()
        {
            var rec = new Recording
            {
                RecordingId = "rec-source",
                VesselName = "Rover",
                VesselPersistentId = 777
            };

            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec,
                sourceVesselExists: true,
                logTag: "Spawner",
                logContext: "unit-test source guard");

            Assert.True(adopted);
            Assert.True(rec.VesselSpawned);
            Assert.Equal(777u, rec.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") &&
                l.Contains("adopting instead of spawning duplicate"));
        }

        [Fact]
        public void TryAdoptExistingSourceVesselForSpawn_AllowDuplicate_DoesNotAdopt()
        {
            var rec = new Recording
            {
                RecordingId = "rec-replay",
                VesselName = "ReplayVessel",
                VesselPersistentId = 888
            };

            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                rec,
                sourceVesselExists: true,
                logTag: "Spawner",
                logContext: "unit-test replay bypass",
                allowExistingSourceDuplicate: true);

            Assert.False(adopted);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void RespawnValidatedRecording_SourceExists_AdoptsBeforeSnapshotValidation()
        {
            var rec = new Recording
            {
                RecordingId = "rec-existing",
                VesselName = "ExistingVessel",
                VesselPersistentId = 999,
                VesselSnapshot = null
            };
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 999);

            uint pid = VesselSpawner.RespawnValidatedRecording(
                rec,
                "unit-test existing source",
                currentUT: 42.0);

            Assert.Equal(999u, pid);
            Assert.True(rec.VesselSpawned);
            Assert.Equal(999u, rec.SpawnedVesselPersistentId);
            Assert.DoesNotContain(logLines, l => l.Contains("missing VesselSnapshot"));
        }

        [Fact]
        public void SpawnOrRecoverIfTooClose_SourceExists_AdoptsBeforeAttemptLimit()
        {
            var rec = new Recording
            {
                RecordingId = "rec-max-attempts",
                VesselName = "MaxAttemptsVessel",
                VesselPersistentId = 1001,
                SpawnAttempts = 3
            };
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == 1001);

            VesselSpawner.SpawnOrRecoverIfTooClose(rec, 12);

            Assert.True(rec.VesselSpawned);
            Assert.Equal(1001u, rec.SpawnedVesselPersistentId);
            Assert.DoesNotContain(logLines, l => l.Contains("max attempts"));
        }

        [Theory]
        [InlineData(0u, 0u, 0u, false)]
        [InlineData(777u, 777u, 0u, true)]
        [InlineData(777u, 0u, 777u, true)]
        [InlineData(777u, 1u, 2u, false)]
        public void ShouldAllowExistingSourceDuplicateForReplay_MatchesOnlyExplicitReplayPids(
            uint sourcePid,
            uint sceneEntryPid,
            uint activeVesselPid,
            bool expected)
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid,
                sceneEntryPid,
                activeVesselPid);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForReplay_RewindScopeRejectsNonTargetActiveSource()
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid: 777u,
                sceneEntryActiveVesselPid: 777u,
                activeVesselPid: 777u,
                replayTargetSourcePid: 123u);

            Assert.False(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("ShouldAllowExistingSourceDuplicate=false")
                && l.Contains("outside rewind replay target sourcePid=123"));
        }

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForReplay_RewindScopeAllowsTargetActiveSource()
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid: 777u,
                sceneEntryActiveVesselPid: 777u,
                activeVesselPid: 777u,
                replayTargetSourcePid: 777u);

            Assert.True(result);
        }

        // Follow-up to PR #505 review: the two bypass helpers used to return
        // true silently, so #226 replay diagnostics were opaque in KSP.log.
        // Each true branch now emits a Verbose Spawner line identifying
        // which PID match triggered the bypass. The tests below pin that
        // contract for both branches of the replay helper and the
        // CurrentFlight wrapper.

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForReplay_SceneEntryPidMatch_LogsVerboseBypass()
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid: 777u,
                sceneEntryActiveVesselPid: 777u,
                activeVesselPid: 0u);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("ShouldAllowExistingSourceDuplicate=true")
                && l.Contains("sourcePid=777")
                && l.Contains("matched sceneEntryActiveVesselPid=777")
                && l.Contains("#226 replay/revert bypass"));
        }

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForReplay_ActiveVesselPidMatch_LogsVerboseBypass()
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid: 888u,
                sceneEntryActiveVesselPid: 0u,
                activeVesselPid: 888u);

            Assert.True(result);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("ShouldAllowExistingSourceDuplicate=true")
                && l.Contains("sourcePid=888")
                && l.Contains("matched activeVesselPid=888")
                && l.Contains("#226 replay/revert bypass"));
        }

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForReplay_NoMatch_DoesNotLogBypass()
        {
            bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForReplay(
                sourcePid: 777u,
                sceneEntryActiveVesselPid: 1u,
                activeVesselPid: 2u);

            Assert.False(result);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("ShouldAllowExistingSourceDuplicate=true"));
        }

        [Fact]
        public void ShouldAllowExistingSourceDuplicateForCurrentFlight_Match_LogsVerboseBypass()
        {
            // Drive the scene-entry path without touching FlightGlobals
            // (headless xUnit); the wrapper delegates to the replay helper
            // and only logs its own Verbose line when that returns true.
            uint priorPid = RecordingStore.SceneEntryActiveVesselPid;
            try
            {
                RecordingStore.SceneEntryActiveVesselPid = 4242u;

                bool result = VesselSpawner.ShouldAllowExistingSourceDuplicateForCurrentFlight(4242u);

                Assert.True(result);
                Assert.Contains(logLines, l =>
                    l.Contains("[Spawner]")
                    && l.Contains("ShouldAllowExistingSourceDuplicateForCurrentFlight=true")
                    && l.Contains("sourcePid=4242")
                    && l.Contains("sceneEntryActiveVesselPid=4242"));
            }
            finally
            {
                RecordingStore.SceneEntryActiveVesselPid = priorPid;
            }
        }

        #endregion

        #region Spawn path routing

        [Fact]
        public void ShouldRouteThroughSpawnAtPosition_EvaRecording_ReturnsTrue()
        {
            var rec = new Recording
            {
                EvaCrewName = "Val",
                TerminalStateValue = TerminalState.Landed
            };

            Assert.True(VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec));
        }

        [Fact]
        public void ShouldRouteThroughSpawnAtPosition_BreakupRecording_ReturnsTrue()
        {
            var rec = new Recording
            {
                ChildBranchPointId = "bp-1",
                TerminalStateValue = TerminalState.Landed
            };

            Assert.True(VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec));
        }

        [Fact]
        public void ShouldRouteThroughSpawnAtPosition_PlainLandedVessel_ReturnsFalse()
        {
            var rec = new Recording
            {
                TerminalStateValue = TerminalState.Landed
            };

            Assert.False(VesselSpawner.ShouldRouteThroughSpawnAtPosition(rec));
        }

        [Fact]
        public void BuildValidatedRespawnSnapshot_PreparedSnapshotNull_Rejects()
        {
            var rec = new Recording
            {
                VesselName = "Prepared Snapshot",
                VesselSnapshot = new ConfigNode("VESSEL")
            };

            ConfigNode snapshot = VesselSpawner.BuildValidatedRespawnSnapshot(
                (ConfigNode)null,
                rec,
                42.0,
                "unit-test prepared snapshot");

            Assert.Null(snapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("missing prepared snapshot"));
        }

        #endregion

        #region DetermineSituation

        [Fact]
        public void DetermineSituation_NegativeAlt_OverWater_ReturnsSplashed()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: -5.0, overWater: true, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_ZeroAlt_OverWater_ReturnsSplashed()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: true, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_NegativeAlt_NotOverWater_ReturnsLanded()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: -2.0, overWater: false, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void DetermineSituation_ZeroAlt_NotOverWater_ReturnsLanded()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: false, speed: 10, orbitalSpeed: 2200);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_HighSpeed_ReturnsOrbiting()
        {
            // speed > orbitalSpeed * 0.9 -> ORBITING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.95;
            string result = VesselSpawner.DetermineSituation(
                alt: 70000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_ExactThreshold_ReturnsOrbiting()
        {
            // speed == orbitalSpeed * 0.9 + epsilon -> ORBITING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.9 + 0.001;
            string result = VesselSpawner.DetermineSituation(
                alt: 70000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_LowSpeed_ReturnsFlying()
        {
            // speed < orbitalSpeed * 0.9 -> FLYING
            double orbitalSpeed = 2200;
            double speed = orbitalSpeed * 0.5;
            string result = VesselSpawner.DetermineSituation(
                alt: 5000, overWater: false, speed: speed, orbitalSpeed: orbitalSpeed);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void DetermineSituation_HighAlt_ZeroSpeed_ReturnsFlying()
        {
            string result = VesselSpawner.DetermineSituation(
                alt: 100, overWater: false, speed: 0, orbitalSpeed: 2200);
            Assert.Equal("FLYING", result);
        }

        [Fact]
        public void DetermineSituation_SplashedTakesPriorityOverLanded()
        {
            // When alt <= 0 AND overWater, SPLASHED wins over LANDED
            string result = VesselSpawner.DetermineSituation(
                alt: 0, overWater: true, speed: 100, orbitalSpeed: 2200);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void DetermineSituation_BoundaryAlt_PositiveSmall_Flying()
        {
            // alt > 0, slow speed -> FLYING (not LANDED)
            string result = VesselSpawner.DetermineSituation(
                alt: 0.1, overWater: false, speed: 5, orbitalSpeed: 2200);
            Assert.Equal("FLYING", result);
        }

        #endregion

        #region ComputeCorrectedSituation

        [Fact]
        public void ComputeCorrectedSituation_FlyingToLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingToSplashed()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Splashed);
            Assert.Equal("SPLASHED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_FlyingToOrbiting()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalToOrbiting()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Orbiting);
            Assert.Equal("ORBITING", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_SubOrbitalToLanded()
        {
            string result = VesselSpawner.ComputeCorrectedSituation("SUB_ORBITAL", TerminalState.Landed);
            Assert.Equal("LANDED", result);
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_SafeSituation()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("LANDED", TerminalState.Landed));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("ORBITING", TerminalState.Orbiting));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("SPLASHED", TerminalState.Splashed));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("PRELAUNCH", TerminalState.Landed));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_DestroyedTerminal()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("FLYING", TerminalState.Destroyed));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_NullTerminal()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("FLYING", null));
        }

        [Fact]
        public void ComputeCorrectedSituation_NoCorrection_NullOrEmptySit()
        {
            Assert.Null(VesselSpawner.ComputeCorrectedSituation(null, TerminalState.Orbiting));
            Assert.Null(VesselSpawner.ComputeCorrectedSituation("", TerminalState.Orbiting));
        }

        [Fact]
        public void ComputeCorrectedSituation_CaseInsensitive()
        {
            Assert.Equal("ORBITING", VesselSpawner.ComputeCorrectedSituation("flying", TerminalState.Orbiting));
            Assert.Equal("LANDED", VesselSpawner.ComputeCorrectedSituation("sub_orbital", TerminalState.Landed));
        }

        #endregion

        #region ShouldZeroVelocityAfterSpawn (#239)

        [Theory]
        [InlineData("LANDED", true)]
        [InlineData("SPLASHED", true)]
        [InlineData("PRELAUNCH", true)]
        [InlineData("ORBITING", false)]
        [InlineData("FLYING", false)]
        [InlineData("SUB_ORBITAL", false)]
        [InlineData("ESCAPING", false)]
        [InlineData(null, false)]
        [InlineData("", false)]
        public void ShouldZeroVelocityAfterSpawn_CorrectForSituation(string sit, bool expected)
        {
            Assert.Equal(expected, VesselSpawner.ShouldZeroVelocityAfterSpawn(sit));
        }

        [Fact]
        public void ShouldZeroVelocityAfterSpawn_CaseInsensitive()
        {
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("landed"));
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("Splashed"));
            Assert.True(VesselSpawner.ShouldZeroVelocityAfterSpawn("prelaunch"));
        }

        #endregion
    }
}
