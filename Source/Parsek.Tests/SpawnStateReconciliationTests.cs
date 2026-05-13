using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #168: spawned vessels not re-spawned after rewind because
    /// SpawnedVesselPersistentId is not reset when the vessel is stripped.
    /// ShouldResetSpawnState is the pure decision method.
    /// ReconcileSpawnStateAfterStrip operates on Recording lists.
    /// </summary>
    [Collection("Sequential")]
    public class SpawnStateReconciliationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SpawnStateReconciliationTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        // --- ShouldResetSpawnState pure decision tests ---

        [Fact]
        public void ShouldResetSpawnState_PidZero_ReturnsFalse()
        {
            var surviving = new HashSet<uint> { 100, 200 };
            Assert.False(ParsekScenario.ShouldResetSpawnState(0, surviving));
        }

        [Fact]
        public void ShouldResetSpawnState_PidInSurvivingSet_ReturnsFalse()
        {
            var surviving = new HashSet<uint> { 100, 200, 300 };
            Assert.False(ParsekScenario.ShouldResetSpawnState(200, surviving));
        }

        [Fact]
        public void ShouldResetSpawnState_PidNotInSurvivingSet_ReturnsTrue()
        {
            var surviving = new HashSet<uint> { 100, 200 };
            Assert.True(ParsekScenario.ShouldResetSpawnState(999, surviving));
        }

        [Fact]
        public void ShouldResetSpawnState_NullSurvivingSet_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldResetSpawnState(100, null));
        }

        [Fact]
        public void ShouldResetSpawnState_EmptySurvivingSet_ReturnsTrue()
        {
            Assert.True(ParsekScenario.ShouldResetSpawnState(100, new HashSet<uint>()));
        }

        // --- ReconcileSpawnStateAfterStrip tests (HashSet<uint> overload) ---

        [Fact]
        public void Reconcile_StrippedVessel_ResetsSpawnState()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                SpawnedVesselPersistentId = 500,
                VesselSpawned = true,
                SpawnAttempts = 2,
                SpawnDeathCount = 1
            };
            var recordings = new List<Recording> { rec };
            var survivingPids = new HashSet<uint>(); // empty — all vessels stripped

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivingPids, recordings);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0, rec.SpawnAttempts);
            Assert.Equal(0, rec.SpawnDeathCount);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") && l.Contains("pid=500") && l.Contains("re-spawn"));
        }

        [Fact]
        public void Reconcile_SurvivingVessel_PreservesSpawnState()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                SpawnedVesselPersistentId = 100,
                VesselSpawned = true
            };
            var recordings = new List<Recording> { rec };
            var survivingPids = new HashSet<uint> { 100 };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivingPids, recordings);

            Assert.Equal(0, reconciled);
            Assert.Equal(100u, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselSpawned);
        }

        [Fact]
        public void Reconcile_NeverSpawned_NoChange()
        {
            var rec = new Recording
            {
                VesselName = "TestVessel",
                SpawnedVesselPersistentId = 0,
                VesselSpawned = false
            };
            var recordings = new List<Recording> { rec };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                new HashSet<uint>(), recordings);

            Assert.Equal(0, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void Reconcile_MixedRecordings_OnlyResetsStripped()
        {
            var recA = new Recording
            {
                VesselName = "Rocket A",
                SpawnedVesselPersistentId = 100,
                VesselSpawned = true
            };
            var recB = new Recording
            {
                VesselName = "Rocket B",
                SpawnedVesselPersistentId = 200,
                VesselSpawned = true
            };
            var recordings = new List<Recording> { recA, recB };
            var survivingPids = new HashSet<uint> { 100 }; // only 100 survives

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivingPids, recordings);

            Assert.Equal(1, reconciled);
            Assert.Equal(100u, recA.SpawnedVesselPersistentId);
            Assert.True(recA.VesselSpawned);
            Assert.Equal(0u, recB.SpawnedVesselPersistentId);
            Assert.False(recB.VesselSpawned);
        }

        [Fact]
        public void Reconcile_NullRecordings_ReturnsZero()
        {
            Assert.Equal(0, ParsekScenario.ReconcileSpawnStateAfterStrip(
                new HashSet<uint>(), null));
        }

        [Fact]
        public void Reconcile_EmptyRecordings_ReturnsZero()
        {
            Assert.Equal(0, ParsekScenario.ReconcileSpawnStateAfterStrip(
                new HashSet<uint>(), new List<Recording>()));
        }

        [Fact]
        public void Reconcile_NullSurvivingPids_ResetsAllNonZeroPids()
        {
            var rec = new Recording
            {
                VesselName = "Test",
                SpawnedVesselPersistentId = 42,
                VesselSpawned = true
            };
            var recordings = new List<Recording> { rec };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                (HashSet<uint>)null, recordings);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
        }

        [Fact]
        public void Reconcile_LogsSummary_WhenReconciled()
        {
            var rec = new Recording
            {
                VesselName = "Vessel",
                SpawnedVesselPersistentId = 999,
                VesselSpawned = true
            };
            var recordings = new List<Recording> { rec };

            ParsekScenario.ReconcileSpawnStateAfterStrip(new HashSet<uint>(), recordings);

            Assert.Contains(logLines, l =>
                l.Contains("ReconcileSpawnStateAfterStrip") && l.Contains("reset 1 recording(s)"));
        }

        [Fact]
        public void Reconcile_StrippedVessel_PreservesNonSpawnFields()
        {
            // LastAppliedResourceIndex is independent of vessel existence — must not be reset
            var rec = new Recording
            {
                VesselName = "Test",
                SpawnedVesselPersistentId = 500,
                VesselSpawned = true,
                LastAppliedResourceIndex = 42
            };
            var recordings = new List<Recording> { rec };

            ParsekScenario.ReconcileSpawnStateAfterStrip(new HashSet<uint>(), recordings);

            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(42, rec.LastAppliedResourceIndex);
        }

        [Fact]
        public void Reconcile_NoLogSummary_WhenNothingReconciled()
        {
            var rec = new Recording
            {
                VesselName = "Vessel",
                SpawnedVesselPersistentId = 0
            };
            var recordings = new List<Recording> { rec };

            ParsekScenario.ReconcileSpawnStateAfterStrip(new HashSet<uint>(), recordings);

            Assert.DoesNotContain(logLines, l => l.Contains("ReconcileSpawnStateAfterStrip"));
        }

        // --- Re-Fly invocation scenario (matches the 2026-05-13 playtest repro) ---
        // A prior merge committed a sibling recording with a real persistent vessel
        // (e.g. the empty Kerbal X capsule, terminal=Landed, SpawnedVesselPersistentId
        // pointed at PID 2708531065). When the player invokes Re-Fly on the Probe
        // slot, PostLoadStripper.Strip removes every non-selected sibling vessel —
        // including the capsule — but leaves the active Probe (pid 3215646968) alive.
        // Without ReconcileSpawnStateAfterStrip running on the Re-Fly load path, the
        // capsule's recording stays VesselSpawned=true and ghost playback at the
        // terminal endpoint logs "Spawn suppressed: already spawned (VesselSpawned=true)"
        // forever.

        [Fact]
        public void Reconcile_ReFlyStripScenario_ResetsAllSiblingsAbsentFromSurvivingSet()
        {
            const uint activeProbePid = 3215646968u;
            const uint capsulePid = 2708531065u;
            const uint otherSiblingPid = 1234567890u;

            var capsule = new Recording
            {
                VesselName = "Kerbal X",
                SpawnedVesselPersistentId = capsulePid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var otherSibling = new Recording
            {
                VesselName = "Kerbal X Booster",
                SpawnedVesselPersistentId = otherSiblingPid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var activeProbe = new Recording
            {
                VesselName = "Kerbal X Probe",
                SpawnedVesselPersistentId = activeProbePid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var recordings = new List<Recording> { capsule, otherSibling, activeProbe };

            // After PostLoadStripper.Strip only the active Probe survives.
            var survivingPids = new HashSet<uint> { activeProbePid };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivingPids, recordings);

            Assert.Equal(2, reconciled);

            // Capsule and other-sibling are reset so the engine can re-spawn them
            // at their terminal endpoints.
            Assert.Equal(0u, capsule.SpawnedVesselPersistentId);
            Assert.False(capsule.VesselSpawned);
            Assert.Equal(0, capsule.SpawnAttempts);
            Assert.Equal(0, capsule.SpawnDeathCount);

            Assert.Equal(0u, otherSibling.SpawnedVesselPersistentId);
            Assert.False(otherSibling.VesselSpawned);
            Assert.Equal(0, otherSibling.SpawnAttempts);
            Assert.Equal(0, otherSibling.SpawnDeathCount);

            // Active Probe survives the strip; its spawn state is preserved.
            Assert.Equal(activeProbePid, activeProbe.SpawnedVesselPersistentId);
            Assert.True(activeProbe.VesselSpawned);
            Assert.Equal(1, activeProbe.SpawnAttempts);

            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains($"pid={capsulePid}")
                && l.Contains("Kerbal X"));
            Assert.Contains(logLines, l =>
                l.Contains("ReconcileSpawnStateAfterStrip")
                && l.Contains("reset 2 recording(s)"));
        }
    }
}
