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
            // This exercises the pure HashSet<uint> overload; the production-shape
            // coverage (survivor set built from protoVessels minus StrippedPids)
            // is in Reconcile_ReFlyStripScenario_ProductionInputShape_ResetsStrippedSiblings.
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

        // --- ComputeSurvivorsFromProtoVesselPids pure helper tests ---
        // Production input shape: a list of all ProtoVessel persistent IDs from
        // HighLogic.CurrentGame.flightState.protoVessels minus the PIDs of
        // vessels that PostLoadStripper.Strip just removed via Vessel.Die().
        // Vessel.Die() does NOT drop the matching ProtoVessel from the
        // flightState.protoVessels save-shape mirror, so a survivor set built
        // from protoVessels alone still contains every stripped capsule's PID
        // and silently masks the bug ShouldResetSpawnState is supposed to
        // detect.

        [Fact]
        public void ComputeSurvivors_SubtractsStrippedPids_FromProtoVesselList()
        {
            // The production-shape scenario: protoVessels still contains
            // capsule + sibling-booster PIDs after Vessel.Die() removed them,
            // because flightState.protoVessels is not auto-synced.
            var protoVesselPids = new uint[] { 100, 200, 300 }; // probe, capsule, booster
            var strippedPids = new uint[] { 200, 300 };          // capsule + booster stripped

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, strippedPids);

            Assert.Single(survivors);
            Assert.Contains(100u, survivors);
            Assert.DoesNotContain(200u, survivors);
            Assert.DoesNotContain(300u, survivors);
        }

        [Fact]
        public void ComputeSurvivors_NullStrippedPids_ReturnsAllProtoVesselPids()
        {
            var protoVesselPids = new uint[] { 100, 200 };

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, null);

            Assert.Equal(2, survivors.Count);
            Assert.Contains(100u, survivors);
            Assert.Contains(200u, survivors);
        }

        [Fact]
        public void ComputeSurvivors_EmptyStrippedPids_ReturnsAllProtoVesselPids()
        {
            var protoVesselPids = new uint[] { 100, 200 };

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, new uint[0]);

            Assert.Equal(2, survivors.Count);
        }

        [Fact]
        public void ComputeSurvivors_NullProtoVesselPids_ReturnsEmptySet()
        {
            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                null, new uint[] { 100 });

            Assert.Empty(survivors);
        }

        [Fact]
        public void ComputeSurvivors_AllProtoVesselPidsStripped_ReturnsEmptySet()
        {
            var protoVesselPids = new uint[] { 100, 200 };
            var strippedPids = new uint[] { 100, 200 };

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, strippedPids);

            Assert.Empty(survivors);
        }

        [Fact]
        public void ComputeSurvivors_StrippedPidNotInProtoVesselList_IsHarmless()
        {
            // Stripper may report PIDs that were never in protoVessels (e.g.
            // a vessel from a sibling slot that died at scene-load before the
            // protoVessel mirror was rebuilt). The subtraction must still
            // produce the right survivor set without crashing.
            var protoVesselPids = new uint[] { 100, 200 };
            var strippedPids = new uint[] { 200, 999 }; // 999 not in protoVessels

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, strippedPids);

            Assert.Single(survivors);
            Assert.Contains(100u, survivors);
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
        public void Reconcile_ReFlyStripScenario_ProductionInputShape_ResetsStrippedSiblings()
        {
            // PRODUCTION INPUT SHAPE: the post-strip survivor set is computed
            // by subtracting PostLoadStripResult.StrippedPids from the raw
            // flightState.protoVessels PID enumeration. This is the shape the
            // Re-Fly load path constructs at RewindInvoker.cs:~822.
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

            // Production shape: PostLoadStripper.Strip only calls Vessel.Die()
            // and records StrippedPids; the matching ProtoVessel stays in
            // flightState.protoVessels because that list is not auto-synced.
            // So protoVessels still includes all three PIDs, and the survivor
            // computation must subtract the stripper's report.
            var protoVesselPids = new uint[] { activeProbePid, capsulePid, otherSiblingPid };
            var strippedPids = new uint[] { capsulePid, otherSiblingPid };

            var survivors = ParsekScenario.ComputeSurvivorsFromProtoVesselPids(
                protoVesselPids, strippedPids);

            // Sanity: only the active probe is a survivor.
            Assert.Single(survivors);
            Assert.Contains(activeProbePid, survivors);
            Assert.DoesNotContain(capsulePid, survivors);
            Assert.DoesNotContain(otherSiblingPid, survivors);

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, recordings);

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

        [Fact]
        public void Reconcile_ReFlyStripScenario_WhenSurvivorSetIsNotSubtracted_BugReappears()
        {
            // REGRESSION GUARD: this test pins the failure mode the
            // production-shape test above defends against. If the call site
            // were to revert to passing flightState.protoVessels directly
            // (without subtracting StrippedPids), every "stripped" PID would
            // still appear as a survivor and the reconcile would do nothing.
            // This test reproduces THAT buggy behaviour deliberately so the
            // assertion makes the contract explicit.
            const uint capsulePid = 2708531065u;
            var capsule = new Recording
            {
                VesselName = "Kerbal X",
                SpawnedVesselPersistentId = capsulePid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var recordings = new List<Recording> { capsule };

            // Buggy survivor set: includes the stripped PID. This is what the
            // pre-fix code path produced because Vessel.Die() left the matching
            // ProtoVessel in flightState.protoVessels.
            var buggySurvivors = new HashSet<uint> { capsulePid };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(buggySurvivors, recordings);

            // Zero reconciled — the bug: stale SpawnedVesselPersistentId stays
            // and the engine's PID dedup gate continues to block re-spawn.
            Assert.Equal(0, reconciled);
            Assert.Equal(capsulePid, capsule.SpawnedVesselPersistentId);
            Assert.True(capsule.VesselSpawned);
        }

        // --- RewindInvoker.ReconcilePostStripSpawnState wrapper tests ---
        // Direct coverage for the Re-Fly post-load reconcile glue extracted from
        // RewindInvoker.RunStripActivateMarker: it subtracts the strip's removed PIDs
        // from the surviving save's protoVessel PIDs, logs the one-line summary at the
        // [Rewind] subsystem, and resets spawn state on any committed recording whose
        // spawned vessel is no longer present. The Unity-only protoVessel-PID collection
        // stays at the call site; this method takes the pre-collected lists, so the glue
        // is testable without a live KSP flightState.

        [Fact]
        public void ReconcilePostStrip_ProductionShape_ResetsStrippedSiblings_AndLogsCounts()
        {
            const uint activeProbePid = 3215646968u;
            const uint capsulePid = 2708531065u;
            const uint boosterPid = 1234567890u;

            var capsule = new Recording
            {
                VesselName = "Kerbal X",
                SpawnedVesselPersistentId = capsulePid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var booster = new Recording
            {
                VesselName = "Kerbal X Booster",
                SpawnedVesselPersistentId = boosterPid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var probe = new Recording
            {
                VesselName = "Kerbal X Probe",
                SpawnedVesselPersistentId = activeProbePid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var committed = new List<Recording> { capsule, booster, probe };

            // protoVessels still carries all three PIDs (Vessel.Die() does not sync the
            // flightState mirror); the strip removed the capsule + booster.
            var protoVesselPids = new List<uint> { activeProbePid, capsulePid, boosterPid };
            var strippedPids = new List<uint> { capsulePid, boosterPid };

            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                protoVesselPids, strippedPids, committed);

            Assert.Equal(2, reconciled);
            Assert.Equal(0u, capsule.SpawnedVesselPersistentId);
            Assert.False(capsule.VesselSpawned);
            Assert.Equal(0, capsule.SpawnAttempts);
            Assert.Equal(0u, booster.SpawnedVesselPersistentId);
            Assert.False(booster.VesselSpawned);
            Assert.Equal(0, booster.SpawnAttempts);
            Assert.Equal(activeProbePid, probe.SpawnedVesselPersistentId);
            Assert.True(probe.VesselSpawned);
            Assert.Equal(1, probe.SpawnAttempts);

            // The wrapper's own summary log at [Rewind] with the exact counts.
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Post-strip reconcile:")
                && l.Contains("strippedPids=2")
                && l.Contains("protoVesselsRemaining=3")
                && l.Contains("survivorPidCount=1"));
        }

        [Fact]
        public void ReconcilePostStrip_NullCommitted_ReturnsZero_NoSummaryLog()
        {
            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<uint> { 100 }, new List<uint> { 100 }, null);

            Assert.Equal(0, reconciled);
            Assert.DoesNotContain(logLines, l => l.Contains("Post-strip reconcile:"));
        }

        [Fact]
        public void ReconcilePostStrip_EmptyCommitted_ReturnsZero_NoSummaryLog()
        {
            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<uint> { 100 }, new List<uint>(), new List<Recording>());

            Assert.Equal(0, reconciled);
            Assert.DoesNotContain(logLines, l => l.Contains("Post-strip reconcile:"));
        }

        [Fact]
        public void ReconcilePostStrip_NullProtoVesselPids_TreatsAllSpawnedAsStripped()
        {
            var rec = new Recording
            {
                VesselName = "Vessel",
                SpawnedVesselPersistentId = 777,
                VesselSpawned = true
            };
            var committed = new List<Recording> { rec };

            // protoVessels null -> empty survivor set -> every non-zero spawn PID reset.
            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                null, new List<uint>(), committed);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Post-strip reconcile:")
                && l.Contains("protoVesselsRemaining=0")
                && l.Contains("survivorPidCount=0"));
        }

        [Fact]
        public void ReconcilePostStrip_NullStrippedPids_AllProtoSurvive_NoReset()
        {
            var rec = new Recording
            {
                VesselName = "Vessel",
                SpawnedVesselPersistentId = 100,
                VesselSpawned = true
            };
            var committed = new List<Recording> { rec };

            // strippedPids null -> survivors = all proto PIDs -> spawn state preserved.
            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<uint> { 100 }, null, committed);

            Assert.Equal(0, reconciled);
            Assert.Equal(100u, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Post-strip reconcile:")
                && l.Contains("strippedPids=0")
                && l.Contains("survivorPidCount=1"));
        }

        // --- BUG-C: in-session OnLoad re-restore of the durable terminal abandon ---

        [Fact]
        public void RestorePersistedTerminalAbandon_SavedFlag_ReappliesAbandonAfterReset()
        {
            // The in-session OnLoad reconcile clears the terminal spawn-safety fields
            // and then restores the saved subset. This helper is the part that
            // re-applies the persisted "cannot spawn safely" abandon so a known-dead
            // terminal-orbit vessel is not re-spawned after a scene change (BUG-C).
            var rec = new Recording { VesselName = "R2-B2-S5" };
            // Simulate the tree-mutable-state reset having just cleared the flag.
            TerminalOrbitSpawnSafety.Clear(rec);
            Assert.False(rec.TerminalSpawnCannotSpawnSafely);

            var savedNode = new ConfigNode("RECORDING");
            savedNode.AddValue("terminalSpawnCannotSpawnSafely", "True");
            savedNode.AddValue("terminalSpawnSafetyReasonCode",
                TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied);

            ParsekScenario.RestorePersistedTerminalAbandon(rec, savedNode);

            Assert.True(rec.TerminalSpawnCannotSpawnSafely,
                "The saved cannot-spawn-safely abandon must be re-applied so the "
                + "scene-change reconcile does not re-enable a known-dead terminal spawn.");
            Assert.Equal(TerminalOrbitSpawnSafety.ReasonSpawnedVesselDied,
                rec.TerminalSpawnSafetyReasonCode);
        }

        [Fact]
        public void RestorePersistedTerminalAbandon_NoSavedFlag_LeavesAbandonClear()
        {
            // A recording that was never abandoned must stay spawn-eligible after the
            // reconcile: an absent key leaves the (already-cleared) flag false, so a
            // revert quicksave (which has no tree nodes) never freezes a healthy
            // terminal-orbit recording.
            var rec = new Recording { VesselName = "Healthy Orbiter" };
            TerminalOrbitSpawnSafety.Clear(rec);

            var savedNode = new ConfigNode("RECORDING");
            // No terminalSpawnCannotSpawnSafely key written.

            ParsekScenario.RestorePersistedTerminalAbandon(rec, savedNode);

            Assert.False(rec.TerminalSpawnCannotSpawnSafely,
                "Absent terminalSpawnCannotSpawnSafely must leave the abandon clear.");
        }
    }
}
