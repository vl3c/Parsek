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
    }
}
