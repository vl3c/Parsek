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

        // --- ShouldResetSpawnState pure decision tests (guid-aware surface) ---
        // The pid-only ShouldResetSpawnState(uint, HashSet<uint>) predicate and the two
        // pid-only ReconcileSpawnStateAfterStrip overloads were deleted in #16 once their
        // last production caller (the plain Rewind-to-Launch OnLoad path) moved to
        // (pid, launch-guid) identities. Every cell below states the same contract over the
        // one remaining surface, so no test keeps a deleted bare-pid entry point alive.

        private static List<(uint pid, string guid)> Survivors(params uint[] pids)
        {
            var list = new List<(uint pid, string guid)>();
            for (int i = 0; i < pids.Length; i++)
                list.Add((pids[i], null));
            return list;
        }

        // Adoption-stamp fixture rule (the SpawnStateReconciliationTests contract): a
        // recording whose spawn endpoint is the craft-baked source pid MUST set
        // VesselPersistentId == SpawnedVesselPersistentId, because that equality is what
        // makes VesselLaunchIdentity.LiveVesselIsRecordedSpawn apply the launch-Guid gate at
        // all. A fixture that omits VesselPersistentId is a genuine Parsek spawn (KSP-unique
        // spawn pid, pid-only by contract) and proves nothing about the craft-baked collision.
        private static Recording AdoptionStamped(
            string name, uint craftBakedPid, string launchGuid, int spawnAttempts = 0)
        {
            return new Recording
            {
                VesselName = name,
                RecordedVesselGuid = launchGuid,
                VesselPersistentId = craftBakedPid,
                SpawnedVesselPersistentId = craftBakedPid,
                VesselSpawned = true,
                SpawnAttempts = spawnAttempts
            };
        }

        [Fact]
        public void ShouldResetSpawnState_PidZero_ReturnsFalse()
        {
            var rec = new Recording { VesselName = "T", SpawnedVesselPersistentId = 0 };
            Assert.False(ParsekScenario.ShouldResetSpawnState(rec, Survivors(100, 200)));
        }

        [Fact]
        public void ShouldResetSpawnState_PidInSurvivingSet_ReturnsFalse()
        {
            var rec = new Recording { VesselName = "T", SpawnedVesselPersistentId = 200 };
            Assert.False(ParsekScenario.ShouldResetSpawnState(rec, Survivors(100, 200, 300)));
        }

        [Fact]
        public void ShouldResetSpawnState_PidNotInSurvivingSet_ReturnsTrue()
        {
            var rec = new Recording { VesselName = "T", SpawnedVesselPersistentId = 999 };
            Assert.True(ParsekScenario.ShouldResetSpawnState(rec, Survivors(100, 200)));
        }

        [Fact]
        public void ShouldResetSpawnState_NullSurvivingSet_ReturnsTrue()
        {
            var rec = new Recording { VesselName = "T", SpawnedVesselPersistentId = 100 };
            Assert.True(ParsekScenario.ShouldResetSpawnState(rec, null));
        }

        [Fact]
        public void ShouldResetSpawnState_EmptySurvivingSet_ReturnsTrue()
        {
            var rec = new Recording { VesselName = "T", SpawnedVesselPersistentId = 100 };
            Assert.True(ParsekScenario.ShouldResetSpawnState(
                rec, new List<(uint pid, string guid)>()));
        }

        [Fact]
        public void ShouldResetSpawnState_AdoptionStampSurvivorFromADifferentLaunch_ReturnsTrue()
        {
            // #16 core statement at predicate level: the survivor carries the recording's
            // craft-baked pid but a conclusively different launch Guid, so it is NOT this
            // recording's vessel and the spawn state must be reset instead of pinned to a
            // stranger.
            var rec = AdoptionStamped("Kerbal X", 2708531065u,
                "11111111-1111-1111-1111-111111111111");

            Assert.True(ParsekScenario.ShouldResetSpawnState(
                rec,
                new List<(uint pid, string guid)>
                {
                    (2708531065u, "22222222-2222-2222-2222-222222222222")
                }));
        }

        [Fact]
        public void ShouldResetSpawnState_AdoptionStampSurvivorOfTheSameLaunch_ReturnsFalse()
        {
            const string launchGuid = "11111111-1111-1111-1111-111111111111";
            var rec = AdoptionStamped("Kerbal X", 2708531065u, launchGuid);

            Assert.False(ParsekScenario.ShouldResetSpawnState(
                rec,
                new List<(uint pid, string guid)> { (2708531065u, launchGuid) }));
        }

        [Fact]
        public void ShouldResetSpawnState_AdoptionStampSurvivorWithUnknownGuid_FallsBackToPidOnly()
        {
            // Legacy recording / legacy save: an unknown Guid on either side is never
            // conclusive, so behaviour is exactly the pre-#16 pid-only answer.
            var rec = AdoptionStamped("Kerbal X", 2708531065u, null);

            Assert.False(ParsekScenario.ShouldResetSpawnState(
                rec, new List<(uint pid, string guid)> { (2708531065u, null) }));
        }

        // --- ReconcileSpawnStateAfterStrip tests (guid-aware overload) ---

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
            var survivors = new List<(uint pid, string guid)>(); // empty - all vessels stripped

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, recordings);

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

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                Survivors(100), recordings);

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
                new List<(uint pid, string guid)>(), recordings);

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

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                Survivors(100), recordings); // only 100 survives

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
                new List<(uint pid, string guid)>(), null));
        }

        [Fact]
        public void Reconcile_EmptyRecordings_ReturnsZero()
        {
            Assert.Equal(0, ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)>(), new List<Recording>()));
        }

        [Fact]
        public void Reconcile_NullSurvivors_ResetsAllNonZeroPids()
        {
            var rec = new Recording
            {
                VesselName = "Test",
                SpawnedVesselPersistentId = 42,
                VesselSpawned = true
            };
            var recordings = new List<Recording> { rec };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                (IReadOnlyList<(uint pid, string guid)>)null, recordings);

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

            ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)>(), recordings);

            Assert.Contains(logLines, l =>
                l.Contains("ReconcileSpawnStateAfterStrip") && l.Contains("reset 1 recording(s)"));
        }

        [Fact]
        public void Reconcile_StrippedVessel_PreservesNonSpawnFields()
        {
            // LastAppliedResourceIndex is independent of vessel existence - must not be reset
            var rec = new Recording
            {
                VesselName = "Test",
                SpawnedVesselPersistentId = 500,
                VesselSpawned = true,
                LastAppliedResourceIndex = 42
            };
            var recordings = new List<Recording> { rec };

            ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)>(), recordings);

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

            ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)>(), recordings);

            Assert.DoesNotContain(logLines, l => l.Contains("ReconcileSpawnStateAfterStrip"));
        }

        // --- Plain Rewind-to-Launch OnLoad path (ParsekScenario post-strip reconcile) ---
        // That call site (the "Defense-in-depth: reconcile spawn state after all strips"
        // block) hands CollectSurvivingVesselIdentities(flightState.protoVessels) to the
        // guid-aware overload. Unlike the Re-Fly path there is no stripped-pid subtraction
        // to do: StripOrphanedSpawnedVessels / StripFuturePrelaunchVessels REMOVE the
        // ProtoVessel from flightState.protoVessels (protoVessels.RemoveAt), so the list
        // already IS the survivor set. What it shares with Re-Fly is the identity shape: a
        // preserved relaunch of the recorded craft must not keep a recording spawned.

        [Fact]
        public void Reconcile_PlainRewindShape_PreservedRelaunchDoesNotPinSpawnState()
        {
            const uint craftBakedPid = 2708531065u;
            var rec = AdoptionStamped("Kerbal X", craftBakedPid,
                "11111111-1111-1111-1111-111111111111", spawnAttempts: 1);
            var recordings = new List<Recording> { rec };

            // The survivor list is flightState.protoVessels after the strips: it holds a
            // DIFFERENT launch of the same craft that the rewind deliberately preserved.
            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)>
                {
                    (craftBakedPid, "22222222-2222-2222-2222-222222222222")
                },
                recordings);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Equal(0, rec.SpawnAttempts);
        }

        [Fact]
        public void Reconcile_PlainRewindShape_SurvivingOwnVessel_KeepsSpawnState()
        {
            const uint craftBakedPid = 2708531065u;
            const string launchGuid = "11111111-1111-1111-1111-111111111111";
            var rec = AdoptionStamped("Kerbal X", craftBakedPid, launchGuid, spawnAttempts: 1);
            var recordings = new List<Recording> { rec };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(
                new List<(uint pid, string guid)> { (craftBakedPid, launchGuid) },
                recordings);

            Assert.Equal(0, reconciled);
            Assert.Equal(craftBakedPid, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselSpawned);
        }

        // --- RewindInvoker.ReconcilePostStripSpawnState wrapper tests ---
        // Direct coverage for the Re-Fly post-load reconcile glue extracted from
        // RewindInvoker.RunStripActivateMarker: it subtracts the strip's removed PIDs
        // from the surviving save's protoVessel PIDs, logs the one-line summary at the
        // [Rewind] subsystem, and resets spawn state on any committed recording whose
        // spawned vessel is no longer present. The Unity-only protoVessel-PID collection
        // stays at the call site; this method takes the pre-collected lists, so the glue
        // is testable without a live KSP flightState. It takes (pid, launch-guid)
        // IDENTITIES, not bare pids, and routes to the guid-aware reconcile overload.

        [Fact]
        public void ReconcilePostStrip_SurvivorSharingCraftBakedPidFromADifferentLaunch_StillResets()
        {
            // The defect the guid-aware routing exists to close. persistentId is
            // craft-baked, so a relaunch of the recorded craft reuses the pid. Once
            // the Re-Fly scrub preserves unrelated vessels, that relaunch survives
            // into the loaded save and a pid-only decision reads it as "the
            // recording's spawned vessel is still alive" - leaving VesselSpawned
            // true against a stranger, which permanently blocks the terminal ghost
            // spawn. The launch guids conclusively differ, so the reset must fire.
            const uint craftBakedPid = 2708531065u;

            var rec = new Recording
            {
                VesselName = "Kerbal X",
                RecordedVesselGuid = "11111111-1111-1111-1111-111111111111",
                // Adoption stamp (SpawnedVesselPersistentId == VesselPersistentId)
                // is the ONLY shape the guid gate applies to: a genuine Parsek
                // spawn gets a KSP-unique pid and stays pid-only by contract
                // (VesselLaunchIdentity.LiveVesselIsRecordedSpawn:99-104). The
                // craft-baked collision hazard lives entirely in this shape.
                VesselPersistentId = craftBakedPid,
                SpawnedVesselPersistentId = craftBakedPid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var committed = new List<Recording> { rec };

            // Survivor carries the same baked pid but a DIFFERENT launch guid, and
            // was not stripped.
            var protoVesselIdentities = new List<(uint pid, string guid)>
            {
                (craftBakedPid, "22222222-2222-2222-2222-222222222222")
            };

            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                protoVesselIdentities, new List<uint>(), committed);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") && l.Contains("survivorsWithLaunchGuid=1"));
        }

        [Fact]
        public void ReconcilePostStrip_SurvivorOfTheSameLaunch_DoesNotReset()
        {
            // The other side of the guid gate: a survivor whose launch guid MATCHES
            // is genuinely the recording's spawned vessel, so spawn state must be
            // left alone (otherwise the ghost double-spawns beside the real craft).
            const uint craftBakedPid = 2708531065u;
            const string launchGuid = "11111111-1111-1111-1111-111111111111";

            var rec = new Recording
            {
                VesselName = "Kerbal X",
                RecordedVesselGuid = launchGuid,
                VesselPersistentId = craftBakedPid,
                SpawnedVesselPersistentId = craftBakedPid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var committed = new List<Recording> { rec };

            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<(uint pid, string guid)> { (craftBakedPid, launchGuid) },
                new List<uint>(),
                committed);

            Assert.Equal(0, reconciled);
            Assert.Equal(craftBakedPid, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselSpawned);
        }

        [Fact]
        public void ReconcilePostStrip_StrippedPidIsSubtractedEvenWhenItsIdentityCarriesAGuid()
        {
            // Guard the subtraction: PostLoadStripper.Strip Die()s the vessel but
            // leaves its ProtoVessel in the flightState mirror, so without the
            // explicit strippedPids subtraction the identity reads as a survivor
            // and the reset is masked - guid agreement makes that MORE likely, not
            // less, so the subtraction has to happen before the guid comparison.
            const uint pid = 2708531065u;
            const string launchGuid = "11111111-1111-1111-1111-111111111111";

            var rec = new Recording
            {
                VesselName = "Kerbal X",
                RecordedVesselGuid = launchGuid,
                SpawnedVesselPersistentId = pid,
                VesselSpawned = true,
                SpawnAttempts = 1
            };
            var committed = new List<Recording> { rec };

            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<(uint pid, string guid)> { (pid, launchGuid) },
                new List<uint> { pid },
                committed);

            Assert.Equal(1, reconciled);
            Assert.False(rec.VesselSpawned);
        }

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
            var protoVesselIdentities = new List<(uint pid, string guid)>
            {
                (activeProbePid, null), (capsulePid, null), (boosterPid, null)
            };
            var strippedPids = new List<uint> { capsulePid, boosterPid };

            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                protoVesselIdentities, strippedPids, committed);

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
                new List<(uint pid, string guid)> { (100u, null) }, new List<uint> { 100 }, null);

            Assert.Equal(0, reconciled);
            Assert.DoesNotContain(logLines, l => l.Contains("Post-strip reconcile:"));
        }

        [Fact]
        public void ReconcilePostStrip_EmptyCommitted_ReturnsZero_NoSummaryLog()
        {
            int reconciled = RewindInvoker.ReconcilePostStripSpawnState(
                new List<(uint pid, string guid)> { (100u, null) }, new List<uint>(), new List<Recording>());

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
                new List<(uint pid, string guid)> { (100u, null) }, null, committed);

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
