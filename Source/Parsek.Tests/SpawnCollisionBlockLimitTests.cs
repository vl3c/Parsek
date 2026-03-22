using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the collision block limit (bug #110 fix):
    /// - <see cref="VesselSpawner.ShouldAbandonCollisionBlockedSpawn"/> pure method
    /// - <see cref="SpawnWarningUI.FormatChainStatus"/> walkback-exhausted variant
    /// - <see cref="SpawnWarningUI.ComputeGhostLabelText"/> walkback-exhausted variant
    /// </summary>
    [Collection("Sequential")]
    public class SpawnCollisionBlockLimitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnCollisionBlockLimitTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldAbandonCollisionBlockedSpawn
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldAbandonCollisionBlockedSpawn_AtMax_ReturnsTrue()
        {
            Assert.True(VesselSpawner.ShouldAbandonCollisionBlockedSpawn(150, 150));
        }

        [Fact]
        public void ShouldAbandonCollisionBlockedSpawn_AboveMax_ReturnsTrue()
        {
            Assert.True(VesselSpawner.ShouldAbandonCollisionBlockedSpawn(200, 150));
        }

        [Fact]
        public void ShouldAbandonCollisionBlockedSpawn_BelowMax_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldAbandonCollisionBlockedSpawn(149, 150));
        }

        [Fact]
        public void ShouldAbandonCollisionBlockedSpawn_Zero_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldAbandonCollisionBlockedSpawn(0, 150));
        }

        [Fact]
        public void ShouldAbandonCollisionBlockedSpawn_ZeroMax_ZeroCount_ReturnsTrue()
        {
            // Edge case: max=0 means abandon immediately
            Assert.True(VesselSpawner.ShouldAbandonCollisionBlockedSpawn(0, 0));
        }

        [Fact]
        public void MaxCollisionBlocks_ConstantValue()
        {
            // Verify the constant is set to 150 (~2.5s at 60fps)
            Assert.Equal(150, VesselSpawner.MaxCollisionBlocks);
        }

        // ────────────────────────────────────────────────────────────
        //  FormatChainStatus — walkback exhausted
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void FormatChainStatus_BlockedAndWalkbackExhausted_ShowsExhaustedMessage()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = true,
                WalkbackExhausted = true
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "TestVessel");

            Assert.Contains("walkback exhausted", status);
            Assert.Contains("manual placement required", status);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]") && l.Contains("FormatChainStatus")
                && l.Contains("walkbackExhausted=True"));
        }

        [Fact]
        public void FormatChainStatus_BlockedNotExhausted_ShowsWaitingMessage()
        {
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = true,
                WalkbackExhausted = false
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "TestVessel");

            Assert.Contains("waiting for clearance", status);
            Assert.DoesNotContain("walkback exhausted", status);
        }

        [Fact]
        public void FormatChainStatus_WalkbackExhaustedButNotBlocked_ShowsNormalStatus()
        {
            // WalkbackExhausted without SpawnBlocked should show normal status
            var chain = new GhostChain
            {
                OriginalVesselPid = 100,
                SpawnUT = 18500.0,
                TipRecordingId = "rec-1",
                IsTerminated = false,
                SpawnBlocked = false,
                WalkbackExhausted = true
            };

            string status = SpawnWarningUI.FormatChainStatus(chain, "TestVessel");

            Assert.Contains("spawns at UT=18500", status);
            Assert.DoesNotContain("walkback exhausted", status);
        }

        // ────────────────────────────────────────────────────────────
        //  ComputeGhostLabelText — walkback exhausted
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ComputeGhostLabelText_BlockedAndWalkbackExhausted_ShowsAbandoned()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText(
                "TestVessel", 18500.0, false, true, true);

            Assert.Contains("spawn abandoned", label);
            Assert.DoesNotContain("spawn blocked\n", label);
            Assert.Contains(logLines, l => l.Contains("[SpawnWarning]")
                && l.Contains("ComputeGhostLabelText")
                && l.Contains("walkbackExhausted=True"));
        }

        [Fact]
        public void ComputeGhostLabelText_BlockedNotExhausted_ShowsBlocked()
        {
            string label = SpawnWarningUI.ComputeGhostLabelText(
                "TestVessel", 18500.0, false, true, false);

            Assert.Contains("spawn blocked", label);
            Assert.DoesNotContain("abandoned", label);
        }

        [Fact]
        public void ComputeGhostLabelText_DefaultParam_BackwardCompatible()
        {
            // Existing callers without the isWalkbackExhausted param should still work
            string label = SpawnWarningUI.ComputeGhostLabelText(
                "TestVessel", 18500.0, false, true);

            Assert.Contains("spawn blocked", label);
            Assert.DoesNotContain("abandoned", label);
        }

        // ────────────────────────────────────────────────────────────
        //  GhostChain.WalkbackExhausted field
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void GhostChain_WalkbackExhausted_DefaultsFalse()
        {
            var chain = new GhostChain();
            Assert.False(chain.WalkbackExhausted);
        }

        // ────────────────────────────────────────────────────────────
        //  Recording.CollisionBlockCount field
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void Recording_CollisionBlockCount_DefaultsZero()
        {
            var rec = new Recording();
            Assert.Equal(0, rec.CollisionBlockCount);
        }

        // ────────────────────────────────────────────────────────────
        //  Recording.SpawnAbandoned field
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void Recording_SpawnAbandoned_DefaultsFalse()
        {
            var rec = new Recording();
            Assert.False(rec.SpawnAbandoned);
        }

        [Fact]
        public void SpawnAbandoned_PreventsVesselGoneReset()
        {
            // Simulate abandon: VesselSpawned=true, SpawnAbandoned=true, PID=0
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnAbandoned = true,
                SpawnedVesselPersistentId = 0,
                CollisionBlockCount = 150
            };

            // The vessel-gone check guards on !rec.SpawnAbandoned.
            // When SpawnAbandoned is true, the check should be skipped
            // — VesselSpawned stays true and CollisionBlockCount is not reset.
            bool wouldEnterVesselGoneCheck = !rec.SpawnAbandoned
                && (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0);

            Assert.False(wouldEnterVesselGoneCheck,
                "SpawnAbandoned should prevent the vessel-gone check from resetting spawn state");
            Assert.True(rec.VesselSpawned, "VesselSpawned must remain true after abandon");
            Assert.Equal(150, rec.CollisionBlockCount);
        }

        [Fact]
        public void SpawnAbandoned_False_AllowsVesselGoneReset()
        {
            // Normal spawn (not abandoned): vessel-gone check should be allowed
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnAbandoned = false,
                SpawnedVesselPersistentId = 0
            };

            bool wouldEnterVesselGoneCheck = !rec.SpawnAbandoned
                && (rec.VesselSpawned || rec.SpawnedVesselPersistentId != 0);

            Assert.True(wouldEnterVesselGoneCheck,
                "Normal spawns should still allow the vessel-gone reset check");
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldAbandonSpawnDeathLoop
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldAbandonSpawnDeathLoop_AtMax_ReturnsTrue()
        {
            Assert.True(VesselSpawner.ShouldAbandonSpawnDeathLoop(3, 3));
        }

        [Fact]
        public void ShouldAbandonSpawnDeathLoop_AboveMax_ReturnsTrue()
        {
            Assert.True(VesselSpawner.ShouldAbandonSpawnDeathLoop(5, 3));
        }

        [Fact]
        public void ShouldAbandonSpawnDeathLoop_BelowMax_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldAbandonSpawnDeathLoop(2, 3));
        }

        [Fact]
        public void ShouldAbandonSpawnDeathLoop_Zero_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldAbandonSpawnDeathLoop(0, 3));
        }

        [Fact]
        public void MaxSpawnDeathCycles_ConstantValue()
        {
            Assert.Equal(3, VesselSpawner.MaxSpawnDeathCycles);
        }

        [Fact]
        public void Recording_SpawnDeathCount_DefaultsZero()
        {
            var rec = new Recording();
            Assert.Equal(0, rec.SpawnDeathCount);
        }

        [Fact]
        public void SpawnDeathLoop_AtMax_AbandonsSpawn()
        {
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnedVesselPersistentId = 42,
                SpawnDeathCount = 2 // will become 3 after increment
            };

            // Simulate the vessel-gone check incrementing and hitting the cap
            rec.SpawnDeathCount++;
            bool shouldAbandon = VesselSpawner.ShouldAbandonSpawnDeathLoop(
                rec.SpawnDeathCount, VesselSpawner.MaxSpawnDeathCycles);

            Assert.True(shouldAbandon);

            // Apply abandon state
            rec.VesselSpawned = true;
            rec.SpawnAbandoned = true;

            Assert.True(rec.VesselSpawned);
            Assert.True(rec.SpawnAbandoned);
            Assert.Equal(3, rec.SpawnDeathCount);
        }

        [Fact]
        public void SpawnDeathLoop_BelowMax_ResetsSpawnState()
        {
            var rec = new Recording
            {
                VesselSpawned = true,
                SpawnedVesselPersistentId = 42,
                SpawnDeathCount = 0
            };

            // Simulate first death
            rec.SpawnDeathCount++;
            bool shouldAbandon = VesselSpawner.ShouldAbandonSpawnDeathLoop(
                rec.SpawnDeathCount, VesselSpawner.MaxSpawnDeathCycles);

            Assert.False(shouldAbandon);

            // Apply reset state (what the vessel-gone check does)
            rec.VesselSpawned = false;
            rec.SpawnedVesselPersistentId = 0;
            rec.CollisionBlockCount = 0;

            Assert.False(rec.VesselSpawned);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.Equal(1, rec.SpawnDeathCount); // count preserved across reset
        }
    }
}
