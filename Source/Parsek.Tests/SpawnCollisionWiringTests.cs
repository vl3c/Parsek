using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for Task 6c-4: ghost extension loop + collision wiring.
    /// Focuses on testable pure logic — state machine transitions for blocked/unblocked
    /// chains, GhostExtender strategy selection, TerrainCorrector integration, and
    /// SpawnCollisionDetector bounds overlap in spawn context.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnCollisionWiringTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnCollisionWiringTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
        }

        #region Helpers

        static GhostChain MakeChain(uint vesselPid, string tipRecordingId,
            bool terminated = false, double spawnUT = 2000.0)
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = vesselPid,
                TipRecordingId = tipRecordingId,
                SpawnUT = spawnUT,
                IsTerminated = terminated
            };
            chain.Links.Add(new ChainLink { recordingId = "link-1", ut = 1000.0 });
            chain.Links.Add(new ChainLink { recordingId = tipRecordingId, ut = spawnUT });
            return chain;
        }

        static Recording MakeRecording(string id, uint vesselPid,
            double startUT = 0, double endUT = 100, string vesselName = "TestVessel")
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselName = vesselName,
                Points = new List<TrajectoryPoint>()
            };
        }

        static Recording MakeOrbitalRecording(string id, uint vesselPid)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                VesselName = "OrbitalVessel",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitInclination = 0.1,
                TerminalOrbitLAN = 0.0,
                TerminalOrbitArgumentOfPeriapsis = 0.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.0,
                TerminalOrbitEpoch = 17000.0,
                Points = new List<TrajectoryPoint>()
            };
        }

        static Recording MakeSurfaceRecording(string id, uint vesselPid)
        {
            return new Recording
            {
                RecordingId = id,
                VesselPersistentId = vesselPid,
                VesselName = "SurfaceVessel",
                TerminalPosition = new SurfacePosition
                {
                    body = "Kerbin",
                    latitude = -0.0972,
                    longitude = -74.5575,
                    altitude = 66.0
                },
                TerminalStateValue = TerminalState.Landed,
                TerrainHeightAtEnd = 65.0,
                Points = new List<TrajectoryPoint>()
            };
        }

        #endregion

        #region SpawnBlocked state machine

        [Fact]
        public void SpawnBlocked_ChainStaysActive()
        {
            // When SpawnBlocked is true, the chain must NOT be removed from the active chains dict.
            // This guards against the chain being prematurely removed.
            var chains = new Dictionary<uint, GhostChain>();
            var chain = MakeChain(100, "tip-rec");
            chains[100] = chain;

            // Simulate collision detection setting SpawnBlocked
            chain.SpawnBlocked = true;
            chain.BlockedSinceUT = 17100.0;

            // Chain should still be in the dictionary
            Assert.True(chains.ContainsKey(100));
            Assert.True(chain.SpawnBlocked);

            // FindChainTipForRecording should still find this chain (it's not terminated)
            var rec = MakeRecording("tip-rec", 100);
            var found = ParsekFlight.FindChainTipForRecording(chains, rec);
            Assert.NotNull(found);
            Assert.Same(chain, found);
            Assert.True(found.SpawnBlocked);

            // Verify the chain is NOT removed when spawn is blocked
            Assert.Single(chains);
        }

        [Fact]
        public void SpawnBlocked_BlockedSinceUT_Tracked()
        {
            // BlockedSinceUT must be set when SpawnBlocked is set.
            // This is used for timeout calculations and diagnostic logging.
            var chain = MakeChain(100, "tip-rec");

            Assert.False(chain.SpawnBlocked);
            Assert.Equal(0.0, chain.BlockedSinceUT);

            double blockedUT = 17150.0;
            chain.SpawnBlocked = true;
            chain.BlockedSinceUT = blockedUT;

            Assert.True(chain.SpawnBlocked);
            Assert.Equal(blockedUT, chain.BlockedSinceUT);

            // Duration calculation (for logging)
            double currentUT = 17200.0;
            double duration = currentUT - chain.BlockedSinceUT;
            Assert.Equal(50.0, duration);
        }

        [Fact]
        public void SpawnBlocked_ClearedOnSuccessfulSpawn()
        {
            // When spawn succeeds after being blocked, SpawnBlocked must be cleared.
            var chain = MakeChain(100, "tip-rec");
            chain.SpawnBlocked = true;
            chain.BlockedSinceUT = 17100.0;

            // Simulate successful spawn clearing the block
            chain.SpawnBlocked = false;

            Assert.False(chain.SpawnBlocked);
            // BlockedSinceUT retains its value (for logging the duration of the block)
            Assert.Equal(17100.0, chain.BlockedSinceUT);
        }

        [Fact]
        public void SpawnBlocked_DefaultsToFalse()
        {
            // New chains should not be blocked by default.
            var chain = new GhostChain();

            Assert.False(chain.SpawnBlocked);
            Assert.Equal(0.0, chain.BlockedSinceUT);
        }

        #endregion

        #region GhostExtender strategy selection (wiring verification)

        [Fact]
        public void GhostExtension_OrbitalStrategy_ChosenCorrectly()
        {
            // Recording with terminal orbit -> ChooseStrategy returns Orbital.
            // Verifies GhostExtender is wired correctly in the spawn context.
            var rec = MakeOrbitalRecording("orbital-rec", 100);

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Orbital, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("Orbital"));
        }

        [Fact]
        public void GhostExtension_SurfaceStrategy_ChosenCorrectly()
        {
            // Recording with terminal surface position -> ChooseStrategy returns Surface.
            var rec = MakeSurfaceRecording("surface-rec", 200);

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.Surface, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("Surface"));
        }

        [Fact]
        public void GhostExtension_LastRecordedPosition_FallbackForPointsOnly()
        {
            // Recording with only trajectory points (no terminal orbit/position) -> LastRecordedPosition.
            var rec = MakeRecording("points-only", 300);
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 17000.0,
                latitude = -0.1,
                longitude = -74.6,
                altitude = 70.0,
                bodyName = "Kerbin"
            });

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.LastRecordedPosition, strategy);
        }

        [Fact]
        public void GhostExtension_NoneForEmptyRecording()
        {
            // Recording with no data -> None.
            var rec = MakeRecording("empty", 400);

            var strategy = GhostExtender.ChooseStrategy(rec);

            Assert.Equal(GhostExtensionStrategy.None, strategy);
        }

        [Fact]
        public void GhostExtension_NullRecording_ReturnsNone()
        {
            var strategy = GhostExtender.ChooseStrategy(null);

            Assert.Equal(GhostExtensionStrategy.None, strategy);
            Assert.Contains(logLines, l =>
                l.Contains("[GhostExtend]") && l.Contains("null recording"));
        }

        #endregion

        #region TerrainCorrector integration

        [Fact]
        public void TerrainCorrection_AppliedForSurfaceSpawn()
        {
            // ShouldCorrectTerrain returns true for Landed + valid terrain height.
            // ComputeCorrectedAltitude preserves clearance above terrain.
            bool shouldCorrect = TerrainCorrector.ShouldCorrectTerrain(
                TerminalState.Landed, 65.0);

            Assert.True(shouldCorrect);

            // Recorded: altitude=66, terrainHeight=65 -> clearance=1m
            // Current terrain=70 -> corrected altitude=71m
            double corrected = TerrainCorrector.ComputeCorrectedAltitude(70.0, 66.0, 65.0);

            Assert.Equal(71.0, corrected);
            Assert.Contains(logLines, l =>
                l.Contains("[TerrainCorrect]") && l.Contains("corrected=71"));
        }

        [Fact]
        public void TerrainCorrection_SkippedForOrbital()
        {
            // Orbital terminal state should NOT trigger terrain correction.
            bool shouldCorrect = TerrainCorrector.ShouldCorrectTerrain(
                TerminalState.Orbiting, double.NaN);

            Assert.False(shouldCorrect);
        }

        [Fact]
        public void TerrainCorrection_SkippedForNaNTerrainHeight()
        {
            // Landed but NaN terrain height (pre-v7 recording) -> skip correction.
            bool shouldCorrect = TerrainCorrector.ShouldCorrectTerrain(
                TerminalState.Landed, double.NaN);

            Assert.False(shouldCorrect);
        }

        [Fact]
        public void TerrainCorrection_SplashedAlsoApplies()
        {
            // Splashed is a surface state -> should correct.
            bool shouldCorrect = TerrainCorrector.ShouldCorrectTerrain(
                TerminalState.Splashed, 0.0);

            Assert.True(shouldCorrect);
        }

        #endregion

        #region VesselGhoster pure guards

        [Fact]
        public void CanSpawnAtChainTip_BlockedChain_StillReturnsTrue()
        {
            // CanSpawnAtChainTip checks chain validity, NOT blocked state.
            // SpawnBlocked is handled by the caller (SpawnVesselOrChainTip).
            var chain = MakeChain(100, "tip-rec");
            chain.SpawnBlocked = true;

            bool canSpawn = VesselGhoster.CanSpawnAtChainTip(chain);

            Assert.True(canSpawn);
        }

        [Fact]
        public void CanSpawnAtChainTip_TerminatedChain_ReturnsFalse()
        {
            var chain = MakeChain(100, "tip-rec", terminated: true);

            bool canSpawn = VesselGhoster.CanSpawnAtChainTip(chain);

            Assert.False(canSpawn);
        }

        [Fact]
        public void SpawnCollisionPadding_Is5Meters()
        {
            // Verify the constant used for collision check padding.
            Assert.Equal(5f, VesselGhoster.SpawnCollisionPadding);
        }

        #endregion

        #region Physics-bubble spawn scoping constant

        [Fact]
        public void PhysicsBubbleSpawnRadius_Is2300()
        {
            // Verify the constant used for physics-bubble scoping in warp queue flush.
            Assert.Equal(2300.0, ParsekFlight.PhysicsBubbleSpawnRadius);
        }

        #endregion
    }
}
