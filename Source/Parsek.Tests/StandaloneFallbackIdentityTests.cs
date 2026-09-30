using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// REWIND-KEPT-VESSEL-RESPAWNED-BY-STANDALONE-SEGMENT. Evidence shape: EVA-10
    /// 2026-09-29_2018 / _2029. The harness boot resumed the committed Kerbal X lander
    /// (tree recording 61f37753, adoption-stamped pid 2708531065), stopped it before
    /// onFlightReady, and ResetFlightReadyState dropped the tree but kept the stopped
    /// recorder. The next StartRecording committed its capture as a standalone 2-point
    /// recording (23258.4-23258.5) with VesselPersistentId = 0. A later Rewind-to-Launch
    /// (adjustedUT 23248.6) kept the lander as committed history, and the standalone's
    /// terminal spawn then built a second lander on the same spot.
    /// </summary>
    [Collection("Sequential")]
    public class StandaloneFallbackIdentityTests : IDisposable
    {
        private const uint LanderPid = 2708531065u;
        private const string LanderGuid = "5493223fe49b42b181998849a9a2aefa";
        private const string OtherLaunchGuid = "0123456789abcdef0123456789abcdef";
        private const double AdjustedUT = 23248.6;

        private readonly List<string> logLines = new List<string>();

        public StandaloneFallbackIdentityTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            VesselSpawner.ResetMaterializedSourceVesselExistsOverrideForTesting();
            ParsekLog.ResetTestOverrides();
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RewindContext.ResetForTesting();
            PlaybackScopeTracker.ResetForTesting();
        }

        private static TrajectoryPoint Point(double ut, float speed)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                latitude = 0.7746,
                longitude = -124.6879,
                altitude = 3854.2,
                bodyName = "Mun",
                rotation = Quaternion.identity,
                velocity = new Vector3(speed, 0f, 0f),
            };
        }

        private static ConfigNode LanderSnapshot(uint pid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", LanderGuid);
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("name", "Kerbal X");
            node.AddValue("sit", "LANDED");
            return node;
        }

        /// <summary>The recorder's stop capture: like BuildCaptureRecording, no VesselPersistentId.</summary>
        private static Recording Capture()
        {
            var captured = new Recording
            {
                VesselName = "Kerbal X",
                RecordedVesselGuid = LanderGuid,
                VesselSnapshot = LanderSnapshot(LanderPid),
            };
            captured.Points.Add(Point(23258.445293629633, 9.2f));
            captured.Points.Add(Point(23258.545293629635, 9.2f));
            return captured;
        }

        /// <summary>The FallbackCommitSplitRecorder standalone build, as the production path runs it.</summary>
        private static Recording BuildStandalone(uint recorderVesselPid)
        {
            Recording captured = Capture();
            Recording rec = RecordingStore.CreateRecordingFromFlightData(
                captured.Points, captured.VesselName,
                orbitSegments: captured.OrbitSegments,
                partEvents: captured.PartEvents,
                flagEvents: captured.FlagEvents);
            Assert.NotNull(rec);
            ParsekFlight.ApplyCapturedSplitStateToStandaloneRecording(rec, captured, recorderVesselPid);
            return rec;
        }

        /// <summary>The committed tree's lander leaf, adoption-stamped (spawn pid == baked pid).</summary>
        private static Recording LanderHolder()
        {
            return new Recording
            {
                RecordingId = "61f3775361fe4130a66a69b1425b7209",
                VesselName = "Kerbal X",
                TreeId = "0da22482a8a648c6835b7bcd6b0f200d",
                ExplicitStartUT = 23143.0,
                ExplicitEndUT = 23158.0,
                VesselPersistentId = LanderPid,
                RecordedVesselGuid = LanderGuid,
                SpawnedVesselPersistentId = LanderPid,
                VesselSpawned = true,
                TerminalStateValue = TerminalState.Landed,
            };
        }

        // ---- the recorded vessel pid reaches the standalone recording ----

        [Fact]
        public void Standalone_CarriesRecorderVesselPid()
        {
            Recording rec = BuildStandalone(LanderPid);
            Assert.Equal(LanderPid, rec.VesselPersistentId);
            Assert.Equal(LanderGuid, rec.RecordedVesselGuid);
            Assert.Null(rec.TreeId);
        }

        [Fact]
        public void Standalone_UnknownRecorderPid_FallsBackToSnapshotPersistentId()
        {
            Recording rec = BuildStandalone(0u);
            Assert.Equal(LanderPid, rec.VesselPersistentId);
        }

        [Fact]
        public void ResolveStandaloneRecordedVesselPid_NoRecorderPidNoSnapshot_IsZero()
        {
            var captured = new Recording();
            Assert.Equal(0u, ParsekFlight.ResolveStandaloneRecordedVesselPid(0u, captured));
            Assert.Equal(0u, ParsekFlight.ResolveStandaloneRecordedVesselPid(0u, null));
            Assert.Equal(7u, ParsekFlight.ResolveStandaloneRecordedVesselPid(7u, null));
        }

        // ---- the rewind strip scope sees the standalone recording ----

        [Fact]
        public void RewindStripScope_StandaloneOfSameLaunchReplays_StripsKeptLander()
        {
            Recording holder = LanderHolder();
            Recording standalone = BuildStandalone(LanderPid);
            var all = new List<Recording> { holder, standalone };

            RecordingStore.RewindSpawnStripScope scope =
                RecordingStore.ResolveRewindSpawnStripScope(all, AdjustedUT);

            Assert.Contains(LanderPid, scope.StripPids);
            Assert.DoesNotContain(LanderPid, scope.KeptPids);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]")
                && l.Contains("Rewind strip scope: strip spawned pid=2708531065")
                && l.Contains("same-vessel-recording-replays:" + standalone.RecordingId));
        }

        [Fact]
        public void RewindStripScope_StandaloneOfDifferentLaunch_KeepsLander()
        {
            Recording holder = LanderHolder();
            Recording relaunch = BuildStandalone(LanderPid);
            relaunch.RecordedVesselGuid = OtherLaunchGuid;
            var all = new List<Recording> { holder, relaunch };

            RecordingStore.RewindSpawnStripScope scope =
                RecordingStore.ResolveRewindSpawnStripScope(all, AdjustedUT);

            Assert.Contains(LanderPid, scope.KeptPids);
            Assert.Empty(scope.StripPids);
        }

        // ---- the spawn adopts the live same-launch vessel instead of building a twin ----

        [Fact]
        public void Spawn_LiveSameLaunchLander_AdoptsInsteadOfTwin()
        {
            Recording standalone = BuildStandalone(LanderPid);
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == LanderPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => LanderGuid);

            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                standalone, "Spawner", "Vessel spawn for #11 (Kerbal X)");

            Assert.True(adopted);
            Assert.True(standalone.VesselSpawned);
            Assert.Equal(LanderPid, standalone.SpawnedVesselPersistentId);
            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]")
                && l.Contains("source vessel pid=2708531065 already exists - adopting instead of spawning duplicate"));
        }

        [Fact]
        public void Spawn_LanderStrippedByRewind_StillSpawns()
        {
            Recording standalone = BuildStandalone(LanderPid);
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => false);

            bool adopted = VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                standalone, "Spawner", "respawn after strip");

            Assert.False(adopted);
            Assert.False(standalone.VesselSpawned);
            Assert.Equal(0u, standalone.SpawnedVesselPersistentId);
        }

        [Fact]
        public void Spawn_LiveVesselIsDifferentLaunchOfSameCraft_NotAdopted()
        {
            Recording standalone = BuildStandalone(LanderPid);
            VesselSpawner.SetMaterializedSourceVesselExistsOverrideForTesting(pid => pid == LanderPid);
            VesselSpawner.SetMaterializedSourceVesselGuidOverrideForTesting(pid => OtherLaunchGuid);

            Assert.False(VesselSpawner.TryAdoptExistingSourceVesselForSpawn(
                standalone, "Spawner", "relaunch"));
            Assert.Equal(0u, standalone.SpawnedVesselPersistentId);
        }

        // ---- a capture of a dropped tree is never committed standalone ----

        [Fact]
        public void DroppedTreeCapture_NoActiveTree_Discarded()
        {
            var droppedTree = new RecordingTree
            {
                Id = "0da22482a8a648c6835b7bcd6b0f200d",
                TreeName = "Kerbal X",
            };

            bool discarded = ParsekFlight.TryDiscardCaptureOfDroppedTree(
                liveActiveTree: null, recorderTree: droppedTree, captured: Capture(),
                recordedVesselPid: LanderPid);

            Assert.True(discarded);
            Assert.Contains(logLines, l =>
                l.Contains("[Flight]")
                && l.Contains("FallbackCommitSplitRecorder: discarded capture of dropped tree 'Kerbal X'")
                && l.Contains("id=0da22482a8a648c6835b7bcd6b0f200d")
                && l.Contains("pid=2708531065 points=2")
                && l.Contains("no standalone commit outside a tree"));
        }

        [Fact]
        public void DroppedTreeCapture_ActiveTreePresent_NotDiscarded()
        {
            var tree = new RecordingTree { Id = "t1", TreeName = "Kerbal X" };

            Assert.False(ParsekFlight.TryDiscardCaptureOfDroppedTree(
                liveActiveTree: tree, recorderTree: tree, captured: Capture(), recordedVesselPid: LanderPid));
            Assert.DoesNotContain(logLines, l => l.Contains("discarded capture of dropped tree"));
        }

        [Fact]
        public void DroppedTreeCapture_RecorderNeverTreeBound_NotDiscarded()
        {
            Assert.False(ParsekFlight.TryDiscardCaptureOfDroppedTree(
                liveActiveTree: null, recorderTree: null, captured: Capture(), recordedVesselPid: LanderPid));
            Assert.DoesNotContain(logLines, l => l.Contains("discarded capture of dropped tree"));
        }
    }
}
