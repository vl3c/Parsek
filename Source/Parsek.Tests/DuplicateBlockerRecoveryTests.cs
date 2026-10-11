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

        private const string LaunchGuidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string LaunchGuidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        [Fact]
        public void ShouldRecoverBlockerVessel_SelfPidMatch_ReturnsTrue()
        {
            // #112 quicksave-duplicate: KSP restored our own spawn with the same PID.
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, LaunchGuidB));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_SiblingRecordingPid_ReturnsFalse_Bug312()
        {
            // #312: two recordings of the same vessel type on the runway. The blocker's
            // PID belongs to a DIFFERENT recording, so recovery would destroy sibling
            // work. Must return false so the caller falls through to walkback.
            var rec = MakeRecordedSpawn("Crater Crawler", spawnedPid: 11111);
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, 22222, null));
        }

        // catches: the old name gate - the player renamed the Parsek-spawned vessel, the
        // quicksave restored it under its new name, and the #112 recovery silently fell
        // through to walkback. The spawn pid is KSP-unique, so the name is irrelevant.
        [Fact]
        public void ShouldRecoverBlockerVessel_RenamedOwnSpawn_StillRecovered()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            rec.VesselPersistentId = 777u;
            rec.RecordedVesselGuid = LaunchGuidA;
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, LaunchGuidB));
        }

        // catches: an adoption stamp (spawn pid == the craft-baked recorded pid) recovering
        // a RELAUNCH of the same craft that reuses the baked pid, whatever it is named.
        [Fact]
        public void ShouldRecoverBlockerVessel_AdoptionStampedRelaunchOfSameCraft_NeverRecovered()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            rec.VesselPersistentId = 12345u;   // adoption stamp
            rec.RecordedVesselGuid = LaunchGuidA;
            // The relaunch carries the baked pid and a fresh guid; same name or not, never ours.
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, LaunchGuidB));
            // The adopted vessel itself (same launch guid) is still recoverable.
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, LaunchGuidA));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_AdoptionStampUnknownGuid_FallsBackToPid()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            rec.VesselPersistentId = 12345u;
            Assert.True(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, LaunchGuidB));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NotYetSpawned_ReturnsFalse()
        {
            // First-spawn case: recording has never spawned before (VesselSpawned=false).
            // There is no "previous spawn" to recover, so the blocker cannot be a #112 duplicate.
            var rec = new Recording { VesselName = "Aeris 4A" }; // VesselSpawned=false, SpawnedVesselPersistentId=0
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, 99999, null));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_SpawnedFlagCleared_ReturnsFalse()
        {
            var rec = MakeRecordedSpawn("Aeris 4A", spawnedPid: 12345);
            rec.VesselSpawned = false;
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, 12345, null));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_ZeroSpawnedPid_ReturnsFalse()
        {
            // Defensive: VesselSpawned=true but SpawnedVesselPersistentId=0 should never
            // match any real blocker PID.
            var rec = new Recording { VesselName = "Aeris 4A", VesselSpawned = true };
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(rec, 0, null));
        }

        [Fact]
        public void ShouldRecoverBlockerVessel_NullRecording_ReturnsFalse()
        {
            Assert.False(VesselSpawner.ShouldRecoverBlockerVessel(null, 12345, null));
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
            // The real guard, not a copy of it: VesselSpawner.ShouldEnterDuplicateBlockerRecovery
            // is the predicate CheckSpawnCollisions calls, so a removed latch reds here.
            var rec = new Recording
            {
                DuplicateBlockerRecovered = true,
                VesselSpawned = true,
                SpawnedVesselPersistentId = 12345,
                VesselName = "Aeris 4A",
            };

            // Everything else about this blocker is a perfect match, so the latch is
            // the only thing standing between it and a second recovery.
            Assert.False(VesselSpawner.ShouldEnterDuplicateBlockerRecovery(
                rec, blockerVesselLoaded: true, blockerPid: 12345, blockerGuid: null));

            // Positive control: clear the latch and the same inputs DO enter recovery,
            // so the refusal above is the latch rather than a dead predicate.
            rec.DuplicateBlockerRecovered = false;
            Assert.True(VesselSpawner.ShouldEnterDuplicateBlockerRecovery(
                rec, blockerVesselLoaded: true, blockerPid: 12345, blockerGuid: null));

            // An unloaded (or absent) blocker cannot be recovered: the call site passes
            // false here for a null blockerVessel.
            Assert.False(VesselSpawner.ShouldEnterDuplicateBlockerRecovery(
                rec, blockerVesselLoaded: false, blockerPid: 12345, blockerGuid: null));

            Assert.False(VesselSpawner.ShouldEnterDuplicateBlockerRecovery(
                null, blockerVesselLoaded: true, blockerPid: 12345, blockerGuid: null));
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
