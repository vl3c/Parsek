using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class CommitFlowTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CommitFlowTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            GhostPlaybackLogic.ResetVesselExistsOverride();
        }

        public void Dispose()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        #region Helpers

        private static TrackSection MakeSection(
            double startUT, double endUT,
            TrackSectionSource source = TrackSectionSource.Active)
        {
            var frames = new List<TrajectoryPoint>
            {
                new TrajectoryPoint
                {
                    ut = startUT,
                    latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                },
                new TrajectoryPoint
                {
                    ut = endUT,
                    latitude = 0, longitude = 0, altitude = 70000,
                    bodyName = "Kerbin",
                    rotation = Quaternion.identity,
                    velocity = Vector3.zero
                }
            };

            return new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                source = source,
                sampleRateHz = 10f,
                frames = frames,
                checkpoints = new List<OrbitSegment>(),
                boundaryDiscontinuityMeters = 0f
            };
        }

        private static PartEvent MakePartEvent(
            double ut, uint pid, PartEventType eventType)
        {
            return new PartEvent
            {
                ut = ut,
                partPersistentId = pid,
                eventType = eventType,
                partName = "part",
                value = 0f,
                moduleIndex = 0
            };
        }

        private static Recording MakeRecording(
            string id, string treeId, string vesselName,
            uint pid = 0, List<TrackSection> sections = null,
            List<PartEvent> events = null)
        {
            var rec = new Recording();
            rec.RecordingId = id;
            rec.TreeId = treeId;
            rec.VesselName = vesselName;
            rec.VesselPersistentId = pid;
            rec.TrackSections = sections ?? new List<TrackSection>();
            rec.PartEvents = events ?? new List<PartEvent>();
            return rec;
        }

        private static ConfigNode MakeMinimalSnapshot()
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("name", "Test Vessel");
            node.AddValue("pid", "12345");
            return node;
        }

        private static RecordingTree MakeTree(
            string name, string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree();
            tree.Id = treeId;
            tree.TreeName = name;
            for (int i = 0; i < recordings.Length; i++)
                tree.Recordings[recordings[i].RecordingId] = recordings[i];
            if (recordings.Length > 0)
            {
                tree.RootRecordingId = recordings[0].RecordingId;
                tree.ActiveRecordingId = recordings[0].RecordingId;
            }
            tree.RebuildBackgroundMap();
            return tree;
        }

        #endregion

        // ================================================================
        // 1. Merge wiring in CommitTree
        // ================================================================

        #region CommitTree runs SessionMerger

        [Fact]
        public void CommitTree_RunsMerger_OverlappingSectionsResolved()
        {
            // Two overlapping sections: Active should win over Background
            var sections = new List<TrackSection>
            {
                MakeSection(0, 100, TrackSectionSource.Active),
                MakeSection(50, 150, TrackSectionSource.Background)
            };
            var rec = MakeRecording("rec-1", "tree-merge", "Merge Vessel",
                sections: sections);
            var tree = MakeTree("Merge Test", "tree-merge", rec);

            RecordingStore.CommitTree(tree);

            // After commit, the recording in the committed list should have
            // resolved (non-overlapping) sections
            Assert.Single(RecordingStore.CommittedRecordings);
            var committed = RecordingStore.CommittedRecordings[0];
            Assert.Equal(2, committed.TrackSections.Count);

            // Active [0-100] preserved entirely
            Assert.Equal(TrackSectionSource.Active, committed.TrackSections[0].source);
            Assert.Equal(0, committed.TrackSections[0].startUT);
            Assert.Equal(100, committed.TrackSections[0].endUT);

            // Background trimmed to [100-150]
            Assert.Equal(TrackSectionSource.Background, committed.TrackSections[1].source);
            Assert.Equal(100, committed.TrackSections[1].startUT);
            Assert.Equal(150, committed.TrackSections[1].endUT);
        }

        [Fact]
        public void CommitTree_PreservesTransientFields()
        {
            var rec = MakeRecording("rec-t", "tree-transient", "Transient Vessel",
                sections: new List<TrackSection> { MakeSection(0, 100) });
            rec.VesselSnapshot = MakeMinimalSnapshot();
            rec.GhostVisualSnapshot = MakeMinimalSnapshot();
            rec.VesselSpawned = false;
            rec.VesselDestroyed = true;
            rec.DistanceFromLaunch = 1234.5;
            rec.MaxDistanceFromLaunch = 5678.9;
            rec.VesselSituation = "Orbiting Kerbin";
            rec.LastAppliedResourceIndex = 7;
            rec.PlaybackEnabled = true;
            rec.LoopPlayback = true;
            rec.LoopIntervalSeconds = 42.0;
            rec.RewindSaveFileName = "quicksave_01";
            rec.PreLaunchFunds = 100000.0;

            var tree = MakeTree("Transient Test", "tree-transient", rec);

            RecordingStore.CommitTree(tree);

            var committed = RecordingStore.CommittedRecordings[0];
            Assert.NotNull(committed.VesselSnapshot);
            Assert.NotNull(committed.GhostVisualSnapshot);
            Assert.True(committed.VesselDestroyed);
            Assert.Equal(1234.5, committed.DistanceFromLaunch);
            Assert.Equal(5678.9, committed.MaxDistanceFromLaunch);
            Assert.Equal("Orbiting Kerbin", committed.VesselSituation);
            Assert.Equal(7, committed.LastAppliedResourceIndex);
            Assert.True(committed.PlaybackEnabled);
            Assert.True(committed.LoopPlayback);
            Assert.Equal(42.0, committed.LoopIntervalSeconds);
            Assert.Equal("quicksave_01", committed.RewindSaveFileName);
            Assert.Equal(100000.0, committed.PreLaunchFunds);
        }

        [Fact]
        public void CommitTree_MergerLogged()
        {
            var rec = MakeRecording("rec-log", "tree-log", "Log Vessel",
                sections: new List<TrackSection> { MakeSection(0, 100) });
            var tree = MakeTree("Log Test", "tree-log", rec);

            RecordingStore.CommitTree(tree);

            // SessionMerger logs should appear
            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") && l.Contains("starting merge"));
            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") && l.Contains("completed merge"));
        }

        [Fact]
        public void CommitTree_MultipleRecordings_AllMerged()
        {
            var rec1 = MakeRecording("rec-a", "tree-multi", "Alpha",
                pid: 1000,
                sections: new List<TrackSection>
                {
                    MakeSection(0, 100, TrackSectionSource.Active),
                    MakeSection(50, 150, TrackSectionSource.Background)
                });
            var rec2 = MakeRecording("rec-b", "tree-multi", "Beta",
                pid: 2000,
                sections: new List<TrackSection>
                {
                    MakeSection(0, 200, TrackSectionSource.Checkpoint)
                });

            var tree = MakeTree("Multi Test", "tree-multi", rec1, rec2);

            RecordingStore.CommitTree(tree);

            Assert.Equal(2, RecordingStore.CommittedRecordings.Count);

            // rec-a should have its overlap resolved
            Recording committedA = null, committedB = null;
            for (int i = 0; i < RecordingStore.CommittedRecordings.Count; i++)
            {
                if (RecordingStore.CommittedRecordings[i].RecordingId == "rec-a")
                    committedA = RecordingStore.CommittedRecordings[i];
                if (RecordingStore.CommittedRecordings[i].RecordingId == "rec-b")
                    committedB = RecordingStore.CommittedRecordings[i];
            }

            Assert.NotNull(committedA);
            Assert.NotNull(committedB);
            Assert.Equal(2, committedA.TrackSections.Count); // Active + trimmed Background
            Assert.Single(committedB.TrackSections); // Single checkpoint unchanged
        }

        [Fact]
        public void CommitTree_NoSections_NoCrash()
        {
            var rec = MakeRecording("rec-empty", "tree-empty", "Empty Vessel");
            var tree = MakeTree("Empty Test", "tree-empty", rec);

            RecordingStore.CommitTree(tree);

            Assert.Single(RecordingStore.CommittedRecordings);
            Assert.Empty(RecordingStore.CommittedRecordings[0].TrackSections);
        }

        [Fact]
        public void CommitTree_MergeReplacesInTree()
        {
            // Verify that the recording in tree.Recordings dict is actually replaced
            var sections = new List<TrackSection>
            {
                MakeSection(0, 200, TrackSectionSource.Background),
                MakeSection(50, 150, TrackSectionSource.Active)
            };
            var rec = MakeRecording("rec-replace", "tree-replace", "Replace Vessel",
                sections: sections);
            var tree = MakeTree("Replace Test", "tree-replace", rec);

            RecordingStore.CommitTree(tree);

            // The tree's own recordings dict should have the merged version
            var treeRec = tree.Recordings["rec-replace"];
            Assert.Equal(3, treeRec.TrackSections.Count);
            // BG [0-50] + Active [50-150] + BG [150-200]
            Assert.Equal(TrackSectionSource.Background, treeRec.TrackSections[0].source);
            Assert.Equal(TrackSectionSource.Active, treeRec.TrackSections[1].source);
            Assert.Equal(TrackSectionSource.Background, treeRec.TrackSections[2].source);
        }

        #endregion

        // ================================================================
        // 2. RealVesselExists with injectable override
        // ================================================================

        #region RealVesselExists

        [Fact]
        public void RealVesselExists_ZeroPid_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.False(GhostPlaybackLogic.RealVesselExists(0));
        }

        [Fact]
        public void RealVesselExists_OverrideReturnsTrue_ReturnsTrue()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 12345);
            Assert.True(GhostPlaybackLogic.RealVesselExists(12345));
        }

        [Fact]
        public void RealVesselExists_OverrideReturnsFalse_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
            Assert.False(GhostPlaybackLogic.RealVesselExists(99999));
        }

        [Fact]
        public void RealVesselExists_NoOverride_ZeroPid_ReturnsFalse()
        {
            GhostPlaybackLogic.ResetVesselExistsOverride();
            // With no override and no FlightGlobals, PID 0 always returns false
            Assert.False(GhostPlaybackLogic.RealVesselExists(0));
        }

        #endregion

        // ================================================================
        // 3. ShouldSkipExternalVesselGhost decision logic
        // ================================================================

        #region ShouldSkipExternalVesselGhost

        [Fact]
        public void ShouldSkip_NullTreeId_ReturnsFalse()
        {
            // Non-tree recordings (standalone) are never skipped
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                null, 12345, false));
        }

        [Fact]
        public void ShouldSkip_EmptyTreeId_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "", 12345, false));
        }

        [Fact]
        public void ShouldSkip_ActiveRecording_ReturnsFalse()
        {
            // Active recording is the player's own vessel — never skip
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-123", 12345, true));
        }

        [Fact]
        public void ShouldSkip_ZeroPid_ReturnsFalse()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-123", 0, false));
        }

        [Fact]
        public void ShouldSkip_ExternalVesselExists_ReturnsTrue()
        {
            // Background tree recording whose real vessel still exists
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 5000);
            Assert.True(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-123", 5000, false));
        }

        [Fact]
        public void ShouldSkip_ExternalVesselGone_ReturnsFalse()
        {
            // Background tree recording whose real vessel no longer exists — need ghost
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-123", 5000, false));
        }

        [Fact]
        public void ShouldSkip_DifferentPidNotFound_ReturnsFalse()
        {
            // Override only finds PID 1000, but recording has PID 5000
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 1000);
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-123", 5000, false));
        }

        [Fact]
        public void ShouldSkip_TreeOwnedVessel_ReturnsFalse()
        {
            // Tree-owned vessel: recording belongs to a committed tree that has this PID
            // Ghost should play to show the recorded trajectory even if real vessel exists
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            var rec = MakeRecording("rec-1", "tree-own", "Jumping Flea", pid: 9000,
                sections: new List<TrackSection> { MakeSection(100, 200) });
            var tree = MakeTree("Jumping Flea", "tree-own", rec);
            RecordingStore.CommitTree(tree);

            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-own", 9000, false));
        }

        [Fact]
        public void ShouldSkip_TreeOwnedEvaBranch_ReturnsFalse()
        {
            // EVA branch vessel PID is also owned by the tree — ghost should play
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            var rec1 = MakeRecording("rec-ship", "tree-eva", "Ship", pid: 8000,
                sections: new List<TrackSection> { MakeSection(100, 200) });
            var rec2 = MakeRecording("rec-eva", "tree-eva", "Jeb", pid: 7000,
                sections: new List<TrackSection> { MakeSection(200, 300) });
            var tree = MakeTree("Ship", "tree-eva", rec1, rec2);
            RecordingStore.CommitTree(tree);

            // Both PIDs should be owned by the tree
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-eva", 8000, false));
            Assert.False(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-eva", 7000, false));
        }

        [Fact]
        public void ShouldSkip_ExternalVesselNotOwnedByTree_ReturnsTrue()
        {
            // External vessel from a different tree — should be skipped
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 6000);
            var rec = MakeRecording("rec-1", "tree-A", "Station", pid: 5000,
                sections: new List<TrackSection> { MakeSection(100, 200) });
            var tree = MakeTree("Station", "tree-A", rec);
            RecordingStore.CommitTree(tree);

            // PID 6000 is NOT in tree-A's recordings (tree-A has PID 5000)
            Assert.True(GhostPlaybackLogic.ShouldSkipExternalVesselGhost(
                "tree-A", 6000, false));
        }

        [Fact]
        public void ShouldSkip_OwnedVesselPids_PopulatedFromRecordings()
        {
            var rec1 = MakeRecording("rec-1", "tree-pid", "Ship", pid: 1111);
            var rec2 = MakeRecording("rec-2", "tree-pid", "Kerbal", pid: 2222);
            var rec3 = MakeRecording("rec-3", "tree-pid", "Ship", pid: 1111); // duplicate PID
            var tree = MakeTree("Ship", "tree-pid", rec1, rec2, rec3);

            Assert.Contains((uint)1111, tree.OwnedVesselPids);
            Assert.Contains((uint)2222, tree.OwnedVesselPids);
            Assert.Equal(2, tree.OwnedVesselPids.Count); // deduped
        }

        #endregion

        // ================================================================
        // 3b. ShouldSpawnAtRecordingEnd — real vessel dedup
        // ================================================================

        #region ShouldSpawnAtRecordingEnd real vessel dedup

        [Fact]
        public void ShouldSpawn_RealVesselExists_ReturnsFalse()
        {
            // Vessel already in scene — no spawn needed
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => pid == 3000);
            var rec = new Recording
            {
                RecordingId = "rec-spawn",
                VesselName = "Ship",
                VesselPersistentId = 3000,
                VesselSnapshot = MakeMinimalSnapshot(),
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, false, false);

            Assert.False(needsSpawn);
            Assert.Contains("real vessel already exists", reason);
            Assert.True(rec.VesselSpawned);
            Assert.Equal((uint)3000, rec.SpawnedVesselPersistentId);
        }

        [Fact]
        public void ShouldSpawn_RealVesselGone_ReturnsTrue()
        {
            // Vessel not in scene (e.g., after revert) — spawn needed
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => false);
            var rec = new Recording
            {
                RecordingId = "rec-spawn2",
                VesselName = "Ship",
                VesselPersistentId = 3000,
                VesselSnapshot = MakeMinimalSnapshot(),
                TerminalStateValue = TerminalState.Landed
            };

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                rec, false, false);

            Assert.True(needsSpawn);
            Assert.Equal("", reason);
        }

        #endregion

        // ================================================================
        // 4. Log assertions for merge-in-commit-flow
        // ================================================================

        #region Merge commit logging

        [Fact]
        public void CommitTree_LogsMergerReplacementPerRecording()
        {
            var rec = MakeRecording("rec-verb", "tree-verb", "Verbose Vessel",
                sections: new List<TrackSection>
                {
                    MakeSection(0, 100, TrackSectionSource.Active)
                });
            var tree = MakeTree("Verbose Test", "tree-verb", rec);

            RecordingStore.CommitTree(tree);

            Assert.Contains(logLines, l =>
                l.Contains("[Merger]") &&
                l.Contains("CommitTree: merged recording") &&
                l.Contains("rec-verb"));
        }

        [Fact]
        public void CommitTree_MergerPreservesPartEvents()
        {
            var events = new List<PartEvent>
            {
                MakePartEvent(50, 42, PartEventType.EngineIgnited),
                MakePartEvent(100, 42, PartEventType.EngineShutdown)
            };
            var rec = MakeRecording("rec-ev", "tree-ev", "Event Vessel",
                sections: new List<TrackSection> { MakeSection(0, 200) },
                events: events);
            var tree = MakeTree("Event Test", "tree-ev", rec);

            RecordingStore.CommitTree(tree);

            var committed = RecordingStore.CommittedRecordings[0];
            Assert.Equal(2, committed.PartEvents.Count);
            Assert.Equal(50, committed.PartEvents[0].ut);
            Assert.Equal(100, committed.PartEvents[1].ut);
        }

        #endregion

        // ================================================================
        // 5. Edge cases
        // ================================================================

        #region Edge cases

        [Fact]
        public void CommitTree_DuplicateTree_MergerNotRunTwice()
        {
            var rec = MakeRecording("rec-dup", "tree-dup", "Dup Vessel",
                sections: new List<TrackSection> { MakeSection(0, 100) });
            var tree = MakeTree("Dup Test", "tree-dup", rec);

            RecordingStore.CommitTree(tree);
            int firstMergeCount = 0;
            for (int i = 0; i < logLines.Count; i++)
                if (logLines[i].Contains("[Merger]") && logLines[i].Contains("starting merge"))
                    firstMergeCount++;

            Assert.Equal(1, firstMergeCount);

            // Second commit of same tree ID — should be skipped
            logLines.Clear();
            RecordingStore.CommitTree(tree);

            int secondMergeCount = 0;
            for (int i = 0; i < logLines.Count; i++)
                if (logLines[i].Contains("[Merger]") && logLines[i].Contains("starting merge"))
                    secondMergeCount++;

            Assert.Equal(0, secondMergeCount);
        }

        [Fact]
        public void ResetVesselExistsOverride_RestoresDefault()
        {
            GhostPlaybackLogic.SetVesselExistsOverrideForTesting(pid => true);
            Assert.True(GhostPlaybackLogic.RealVesselExists(12345));

            GhostPlaybackLogic.ResetVesselExistsOverride();
            // After reset, PID 0 still returns false (guard clause)
            Assert.False(GhostPlaybackLogic.RealVesselExists(0));
        }

        #endregion
    }
}
