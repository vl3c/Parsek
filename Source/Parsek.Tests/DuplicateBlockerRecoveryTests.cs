using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for the duplicate blocker recovery fix (bug #112):
    /// - <see cref="VesselSpawner.ShouldRecoverBlockerVessel"/> pure decision method
    /// - <see cref="Recording.DuplicateBlockerRecovered"/> flag behavior
    /// - Reset of spawn fields in <see cref="RecordingStore.ResetAllPlaybackState"/>
    /// </summary>
    [Collection("Sequential")]
    public class DuplicateBlockerRecoveryTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public DuplicateBlockerRecoveryTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RecordingStore.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        // ────────────────────────────────────────────────────────────
        //  ShouldRecoverBlockerVessel — pure decision method
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ShouldRecoverBlockerVessel_SameName_ReturnsTrue()
        {
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel("Aeris 4A", "Aeris 4A"));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_DifferentName_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("Kerbal X", "Aeris 4A"));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullBlockerName_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(null, "Aeris 4A"));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullRecordingName_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("Aeris 4A", null));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_EmptyBlockerName_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("", "Aeris 4A"));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_EmptyRecordingName_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("Aeris 4A", ""));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_BothEmpty_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("", ""));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_CaseSensitive_ReturnsFalse()
        {
            // Ordinal comparison — both names should already be resolved via
            // ResolveLocalizedName, so case differences indicate different vessels
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel("aeris 4a", "Aeris 4A"));
        }

        // ────────────────────────────────────────────────────────────
        //  DuplicateBlockerRecovered flag behavior
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void DuplicateBlockerRecovered_DefaultsFalse()
        {
            var rec = new Recording();
            Assert.False(rec.DuplicateBlockerRecovered);
        }

        [Fact]
        public void DuplicateBlockerRecovered_PreventsSecondRecoveryCheck()
        {
            // Simulates the guard in CheckSpawnCollisions: once DuplicateBlockerRecovered
            // is set, the name-match check is not entered regardless of match
            var rec = new Recording { DuplicateBlockerRecovered = true };

            // Even with a matching name, the guard prevents entry
            bool wouldRecover = !rec.DuplicateBlockerRecovered
                && VesselSpawner.ShouldRecoverBlockerVessel("Aeris 4A", "Aeris 4A");
            Assert.False(wouldRecover);
        }

        // ────────────────────────────────────────────────────────────
        //  ResetRecordingPlaybackFields — resets all spawn state
        // ────────────────────────────────────────────────────────────

        [Fact]
        public void ResetPlaybackState_ClearsDuplicateBlockerRecovered()
        {
            RecordingStore.SuppressLogging = false;
            var rec = new Recording
            {
                VesselName = "TestVessel",
                RecordingId = "test-reset",
                DuplicateBlockerRecovered = true,
                CollisionBlockCount = 50,
                SpawnAbandoned = true,
                VesselSpawned = true,
                SpawnDeathCount = 2,
                SpawnAttempts = 1,
                SpawnedVesselPersistentId = 12345
            };

            RecordingStore.AddCommittedForTesting(rec);
            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.DuplicateBlockerRecovered);
            Assert.Equal(0, rec.CollisionBlockCount);
            Assert.False(rec.SpawnAbandoned);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void CollisionBlockCount_DefaultsZero()
        {
            var rec = new Recording();
            Assert.Equal(0, rec.CollisionBlockCount);
        }

        [Fact]
        public void SpawnAbandoned_DefaultsFalse()
        {
            var rec = new Recording();
            Assert.False(rec.SpawnAbandoned);
        }
    }
}
