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
        //
        //  #112 original scenario: a Parsek-spawned vessel is restored from a
        //  quicksave and collides with the fresh spawn. Recovery is the right move.
        //
        //  #312 regression: the original version matched by NAME only, so four
        //  "Crater Crawler" showcase recordings on the runway cannibalized each
        //  other -- each new spawn found the previous as a "duplicate" and
        //  destroyed it. New rule: blocker PID must match this recording's OWN
        //  SpawnedVesselPersistentId. Siblings fall through to walkback.
        // ────────────────────────────────────────────────────────────

        private static Recording MakeRecordedSpawn(string name, uint spawnedPid)
        {
            return new Recording
            {
                VesselName = name,
                VesselSpawned = true,
                SpawnedVesselPersistentId = spawnedPid,
            };
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_SelfPidMatch_ReturnsTrue()
        {
            // #112 quicksave-duplicate: KSP restored our own spawn with the same PID.
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", "Aeris 4A", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_SiblingRecordingPid_ReturnsFalse_Bug312()
        {
            // #312: two recordings of the same vessel type on the runway. The blocker's
            // PID belongs to a DIFFERENT recording, so recovery would destroy sibling
            // work. Must return false so the caller falls through to walkback.
            var rec = MakeRecordedSpawn("Crater Crawler", spawnedPid: 11111);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Crater Crawler", "Crater Crawler", blockerPid: 22222));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_DifferentName_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Kerbal X", "Aeris 4A", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NotYetSpawned_ReturnsFalse()
        {
            // First-spawn case: recording has never spawned before (VesselSpawned=false).
            // There is no "previous spawn" to recover, so the blocker cannot be a #112 duplicate.
            var rec = new Recording { VesselName = "Aeris 4A" }; // VesselSpawned=false, SpawnedVesselPersistentId=0
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", "Aeris 4A", blockerPid: 99999));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_ZeroSpawnedPid_ReturnsFalse()
        {
            // Defensive: VesselSpawned=true but SpawnedVesselPersistentId=0 should never
            // match any real blocker PID.
            var rec = new Recording { VesselName = "Aeris 4A", VesselSpawned = true };
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", "Aeris 4A", blockerPid: 0));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullRecording_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(null, "Aeris 4A", "Aeris 4A", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullBlockerName_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, null, "Aeris 4A", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullRecordingName_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", null, blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_EmptyBlockerName_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "", "Aeris 4A", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_EmptyRecordingName_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", "", blockerPid: 12345));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_CaseSensitive_ReturnsFalse()
        {
            // Ordinal comparison -- both names are already resolved via
            // ResolveLocalizedName at the call site, so case differences signal
            // different vessels.
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, "aeris 4a", "Aeris 4A", blockerPid: 12345));
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
            // is set, the recovery check is not entered regardless of match.
            var rec = new Recording
            {
                DuplicateBlockerRecovered = true,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 12345,
                VesselName = "Aeris 4A",
            };

            // Even with a matching PID + name, the guard prevents entry
            bool wouldRecover = !rec.DuplicateBlockerRecovered
                && VesselSpawner.ShouldRecoverBlockerVessel(rec, "Aeris 4A", "Aeris 4A", blockerPid: 12345);
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

            RecordingStore.AddRecordingWithTreeForTesting(rec);
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
