using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// BUG-H: a stock Revert-to-Launch/Prelaunch silently deleted the player's real, separately
    /// launched landed/orbiting craft because the strip matched flightState vessels by craft-baked
    /// NAME against a GLOBAL set of every recording's spawned-vessel name — with no launch-Guid gate
    /// and no scoping to the reverted flight.
    ///
    /// These tests pin the two corrections of the centralized
    /// <see cref="ParsekScenario.ShouldStripVesselForRecordings"/> predicate plus the guid-aware
    /// reconcile and the revert-target whitelist parser. The two Guids and the shared craft-baked pid
    /// model the real collision: two launches of one craft share <c>VesselPersistentId</c> but carry
    /// distinct <c>Vessel.id</c> Guids.
    /// </summary>
    [Collection("Sequential")]
    public class RevertVesselStripTests : IDisposable
    {
        private const string GuidA = "2b6e6a60d2c947489753371317fa067e"; // recording's launch
        private const string GuidB = "a424011b746440baae6030e225c9de31"; // a DIFFERENT real launch, same craft
        private const uint CraftPid = 2708531065u;                       // craft-baked pid (shared)

        private readonly List<string> logLines = new List<string>();

        public RevertVesselStripTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RevertDetector.ResetForTesting();
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
            RevertDetector.ResetForTesting();
        }

        // An adoption-stamp recording: spawn endpoint == craft-baked source pid (the real bug shape).
        private static Recording Adopted(string name, uint pid, string guid) => new Recording
        {
            VesselName = name,
            VesselPersistentId = pid,
            SpawnedVesselPersistentId = pid,
            RecordedVesselGuid = guid,
            VesselSpawned = true,
        };

        // A genuine Parsek spawn: spawn endpoint is a KSP-unique pid distinct from the source pid.
        private static Recording GenuineSpawn(string name, uint sourcePid, uint spawnPid, string guid) => new Recording
        {
            VesselName = name,
            VesselPersistentId = sourcePid,
            SpawnedVesselPersistentId = spawnPid,
            RecordedVesselGuid = guid,
            VesselSpawned = true,
        };

        // ---- Correction 1: launch-Guid gate (the 6-of-7 evidence vessels) ----

        [Fact]
        public void Revert_PidCollisionDifferentGuid_NotStripped()
        {
            // The recording adopted launch A (craft-baked pid). A real, SEPARATELY launched craft B
            // reuses the same baked pid but carries Guid B. It must NOT be stripped — it is a
            // different real launch, not the recording's vessel.
            var recordings = new List<Recording> { Adopted("R.1-S.1", CraftPid, GuidA) };

            // No scope (isolate the Guid gate): even with the candidate absent from any whitelist.
            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidB, Vessel.Situations.LANDED, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: false, preExistingWhitelist: null,
                out Recording matched, out string reason);

            Assert.False(strip);
            Assert.Null(matched);
            Assert.Contains("no same-launch", reason);
        }

        [Fact]
        public void Revert_SameLaunchAdoption_NotInScope_WouldStrip_ButGuidGateAlonePasses()
        {
            // Sanity: the same-launch adopted vessel DOES pass the Guid gate (Guid agrees). It is the
            // 7th evidence vessel; the SCOPE check (next test) is what protects it.
            var recordings = new List<Recording> { Adopted("R3-B2-U1-S7", CraftPid, GuidA) };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidA, Vessel.Situations.LANDED, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: false, preExistingWhitelist: null,
                out Recording matched, out _);

            Assert.True(strip);
            Assert.Same(recordings[0], matched);
        }

        // ---- Correction 2: scope to the reverted flight (launch-quicksave whitelist) ----

        [Fact]
        public void Revert_SameLaunchAdoption_InLaunchQuicksave_NotStripped()
        {
            // THE 7th EVIDENCE VESSEL: a real vessel the recording legitimately adopted (Guid match),
            // but it pre-existed the reverted launch (present in the revert-target quicksave) and
            // belongs to an unrelated mission. Scope must protect it even though the Guid gate passes.
            var recordings = new List<Recording> { Adopted("R3-B2-U1-S7", CraftPid, GuidA) };
            var launchQuicksavePids = new HashSet<uint> { CraftPid };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidA, Vessel.Situations.LANDED, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: true, preExistingWhitelist: launchQuicksavePids,
                out Recording matched, out string reason);

            Assert.False(strip);
            Assert.Null(matched);
            Assert.Contains("pre-existing", reason);
        }

        [Fact]
        public void Revert_GenuineSpawn_DuringRevertedFlight_NotInQuicksave_Stripped()
        {
            // The legitimate revert-strip target: a vessel Parsek spawned DURING the reverted flight
            // (KSP-unique spawn pid, not present in the launch quicksave) is removed.
            const uint spawnPid = 909090909u;
            var recordings = new List<Recording> { GenuineSpawn("Probe", CraftPid, spawnPid, GuidA) };
            var launchQuicksavePids = new HashSet<uint> { CraftPid }; // does NOT contain the spawn pid

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                spawnPid, "freshspawnguid", Vessel.Situations.ORBITING, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: true, preExistingWhitelist: launchQuicksavePids,
                out Recording matched, out string reason);

            Assert.True(strip);
            Assert.Same(recordings[0], matched);
            Assert.Contains("appeared during the reverted flight", reason);
        }

        [Fact]
        public void Revert_ScopeRequired_NullWhitelist_FailsClosed_NotStripped()
        {
            // Data-loss safety: if the launch-quicksave scope signal is unavailable, never strip.
            var recordings = new List<Recording> { GenuineSpawn("Probe", CraftPid, 909090909u, GuidA) };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                909090909u, "g", Vessel.Situations.ORBITING, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: true, preExistingWhitelist: null,
                out Recording matched, out string reason);

            Assert.False(strip);
            Assert.Null(matched);
            Assert.Contains("fail-closed", reason);
        }

        [Fact]
        public void Revert_PrelaunchVessel_Skipped_WhenSkipPrelaunch()
        {
            // The player's launch vessel on the pad is PRELAUNCH and must be protected on revert.
            var recordings = new List<Recording> { Adopted("Launch", CraftPid, GuidA) };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidA, Vessel.Situations.PRELAUNCH, recordings,
                matchSource: false, skipPrelaunch: true,
                requireWhitelist: false, preExistingWhitelist: null,
                out _, out string reason);

            Assert.False(strip);
            Assert.Contains("PRELAUNCH", reason);
        }

        [Fact]
        public void Revert_CandidatePidZero_NotStripped()
        {
            var recordings = new List<Recording> { Adopted("X", CraftPid, GuidA) };
            Assert.False(ParsekScenario.ShouldStripVesselForRecordings(
                0u, GuidA, Vessel.Situations.LANDED, recordings,
                false, true, false, null, out _, out _));
        }

        [Fact]
        public void Revert_NoMatchingRecording_NotStripped()
        {
            // A vessel that no recording claims (a plain real craft) is never touched.
            var recordings = new List<Recording> { Adopted("Other", 111u, GuidA) };
            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidB, Vessel.Situations.ORBITING, recordings,
                false, true, false, null, out Recording matched, out string reason);
            Assert.False(strip);
            Assert.Null(matched);
            Assert.Contains("no same-launch", reason);
        }

        // ---- Rewind contract: matchSource=true removes future recorded vessels, not other launches ----

        [Fact]
        public void Rewind_MatchSource_RecordedVessel_SameLaunch_Stripped()
        {
            // On rewind the recorded source vessel itself is future relative to the rewind point.
            var rec = new Recording
            {
                VesselName = "Active",
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = GuidA,
                SpawnedVesselPersistentId = 0,
            };
            var recordings = new List<Recording> { rec };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidA, Vessel.Situations.FLYING, recordings,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: null,
                out Recording matched, out _);

            Assert.True(strip);
            Assert.Same(rec, matched);
        }

        [Fact]
        public void Rewind_MatchSource_DifferentLaunchSameCraft_NotStripped()
        {
            // A different real launch of the same craft (Guid B) is never removed by the recorded-
            // source match, preserving the rewind invariant "never delete a different real launch".
            var rec = new Recording
            {
                VesselName = "Active",
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = GuidA,
            };
            var recordings = new List<Recording> { rec };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidB, Vessel.Situations.LANDED, recordings,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: null,
                out _, out string reason);

            Assert.False(strip);
            Assert.Contains("no same-launch", reason);
        }

        // ---- Rewind backstop: quicksave protect-set guards the guidless adoption-stamp gap ----

        [Fact]
        public void Rewind_GuidlessAdoption_VesselInQuicksave_ProtectedByWhitelist()
        {
            // The adversarial gap: a GUIDLESS adoption-stamp recording (RecordedVesselGuid empty)
            // falls back to pid-only matching, so a real vessel reusing the craft-baked pid would
            // match. On rewind (requireWhitelist=false) the rewind-quicksave protect-set must still
            // shield a vessel that pre-existed the rewind point even though the Guid gate can't.
            var recordings = new List<Recording> { Adopted("Legacy", CraftPid, null /* guidless */) };
            var rewindQuicksavePids = new HashSet<uint> { CraftPid };

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidB, Vessel.Situations.LANDED, recordings,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: rewindQuicksavePids,
                out Recording matched, out string reason);

            Assert.False(strip);
            Assert.Null(matched);
            Assert.Contains("pre-existing", reason);
        }

        [Fact]
        public void Rewind_GuidlessAdoption_VesselNotInQuicksave_StillStripped()
        {
            // A guidless adoption recording whose vessel is NOT in the rewind quicksave is genuinely
            // future (created after the rewind point), so the identity strip (pid-only fallback) still
            // removes it. requireWhitelist=false means a null/absent quicksave keeps prior behaviour.
            var recordings = new List<Recording> { Adopted("Legacy", CraftPid, null) };
            var rewindQuicksavePids = new HashSet<uint> { 12345u }; // does NOT contain CraftPid

            bool strip = ParsekScenario.ShouldStripVesselForRecordings(
                CraftPid, GuidB, Vessel.Situations.LANDED, recordings,
                matchSource: true, skipPrelaunch: false,
                requireWhitelist: false, preExistingWhitelist: rewindQuicksavePids,
                out Recording matched, out _);

            Assert.True(strip);
            Assert.Same(recordings[0], matched);
        }

        // ---- Guid-aware reconcile (don't reset against a same-pid different-launch survivor) ----

        [Fact]
        public void Reconcile_GuidAware_SurvivorIsDifferentLaunch_ResetsForRespawn()
        {
            // The recording's adopted vessel was stripped; a same-pid DIFFERENT-launch vessel survives.
            // Pid-only reconcile would see the pid "surviving" and never reset. Guid-aware reconcile
            // resets it so it can re-spawn (the survivor is a stranger, not the recording's vessel).
            var rec = Adopted("R.1-S.1", CraftPid, GuidA);
            var recordings = new List<Recording> { rec };
            var survivors = new List<(uint pid, string guid)> { (CraftPid, GuidB) };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, recordings);

            Assert.Equal(1, reconciled);
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
            Assert.False(rec.VesselSpawned);
            Assert.Contains(logLines, l => l.Contains("guid-aware") && l.Contains("reset 1 recording"));
        }

        [Fact]
        public void Reconcile_GuidAware_SurvivorIsSameLaunch_PreservesSpawnState()
        {
            // The adopted vessel itself survives (Guid matches): the recording keeps owning it.
            var rec = Adopted("R3-B2-U1-S7", CraftPid, GuidA);
            var recordings = new List<Recording> { rec };
            var survivors = new List<(uint pid, string guid)> { (CraftPid, GuidA) };

            int reconciled = ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, recordings);

            Assert.Equal(0, reconciled);
            Assert.Equal(CraftPid, rec.SpawnedVesselPersistentId);
            Assert.True(rec.VesselSpawned);
        }

        [Fact]
        public void Reconcile_GuidAware_GenuineSpawnSurvives_ByPid_PreservesSpawnState()
        {
            const uint spawnPid = 424242424u;
            var rec = GenuineSpawn("Probe", CraftPid, spawnPid, GuidA);
            var recordings = new List<Recording> { rec };
            // Genuine spawn pid match keeps spawn state regardless of the survivor's Guid.
            var survivors = new List<(uint pid, string guid)> { (spawnPid, "whatever") };

            Assert.Equal(0, ParsekScenario.ReconcileSpawnStateAfterStrip(survivors, recordings));
            Assert.Equal(spawnPid, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Reconcile_GuidAware_NullSurvivors_ResetsAllSpawned()
        {
            var rec = Adopted("X", CraftPid, GuidA);
            var recordings = new List<Recording> { rec };
            Assert.Equal(1, ParsekScenario.ReconcileSpawnStateAfterStrip(
                (IReadOnlyList<(uint pid, string guid)>)null, recordings));
            Assert.Equal(0u, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void ShouldResetSpawnState_GuidAware_NeverSpawned_NoReset()
        {
            var rec = new Recording { VesselName = "X", SpawnedVesselPersistentId = 0 };
            Assert.False(ParsekScenario.ShouldResetSpawnState(
                rec, new List<(uint pid, string guid)> { (CraftPid, GuidA) }));
        }

        // ---- ExtractFlightStateVesselPids: revert-target whitelist parser ----

        [Fact]
        public void ExtractFlightStateVesselPids_GameWrapper_ParsesVesselPids()
        {
            var config = new ConfigNode();
            var game = config.AddNode("GAME");
            var fs = game.AddNode("FLIGHTSTATE");
            var v1 = fs.AddNode("VESSEL");
            v1.AddValue("pid", GuidA);
            v1.AddValue("persistentId", "100");
            var v2 = fs.AddNode("VESSEL");
            v2.AddValue("pid", GuidB);
            v2.AddValue("persistentId", "200");

            var pids = RevertDetector.ExtractFlightStateVesselPids(config);

            Assert.Equal(2, pids.Count);
            Assert.Contains(100u, pids);
            Assert.Contains(200u, pids);
        }

        [Fact]
        public void ExtractFlightStateVesselPids_NoGameWrapper_StillParses()
        {
            var config = new ConfigNode();
            var fs = config.AddNode("FLIGHTSTATE");
            var v1 = fs.AddNode("VESSEL");
            v1.AddValue("persistentId", "555");

            var pids = RevertDetector.ExtractFlightStateVesselPids(config);

            Assert.Single(pids);
            Assert.Contains(555u, pids);
        }

        [Fact]
        public void ExtractFlightStateVesselPids_SkipsZeroAndUnparseable()
        {
            var config = new ConfigNode();
            var fs = config.AddNode("FLIGHTSTATE");
            fs.AddNode("VESSEL").AddValue("persistentId", "0");        // zero ignored
            fs.AddNode("VESSEL").AddValue("persistentId", "notanumber"); // unparseable ignored
            fs.AddNode("VESSEL").AddValue("persistentId", "777");

            var pids = RevertDetector.ExtractFlightStateVesselPids(config);

            Assert.Single(pids);
            Assert.Contains(777u, pids);
        }

        [Fact]
        public void ExtractFlightStateVesselPids_NullOrMissingFlightState_ReturnsEmpty()
        {
            Assert.Empty(RevertDetector.ExtractFlightStateVesselPids(null));
            Assert.Empty(RevertDetector.ExtractFlightStateVesselPids(new ConfigNode()));
        }

        // ---- BuildRevertTargetWhitelist: empty-snapshot -> null (fail-closed gate) ----

        [Fact]
        public void BuildRevertTargetWhitelist_PopulatedSnapshot_ReturnsPidSet()
        {
            var config = new ConfigNode();
            var fs = config.AddNode("GAME").AddNode("FLIGHTSTATE");
            fs.AddNode("VESSEL").AddValue("persistentId", "100");

            var pids = RevertDetector.BuildRevertTargetWhitelist(config);

            Assert.NotNull(pids);
            Assert.Contains(100u, pids);
        }

        [Fact]
        public void BuildRevertTargetWhitelist_EmptyOrUnparseableSnapshot_ReturnsNull_SoStripFailsClosed()
        {
            // A launch/prelaunch snapshot always has >= 1 vessel, so an empty parse means the layout
            // was unexpected; returning null makes the revert strip fail closed (strip nothing).
            Assert.Null(RevertDetector.BuildRevertTargetWhitelist(null));
            Assert.Null(RevertDetector.BuildRevertTargetWhitelist(new ConfigNode()));

            var onlyZero = new ConfigNode();
            onlyZero.AddNode("GAME").AddNode("FLIGHTSTATE").AddNode("VESSEL").AddValue("persistentId", "0");
            Assert.Null(RevertDetector.BuildRevertTargetWhitelist(onlyZero));
        }
    }
}
