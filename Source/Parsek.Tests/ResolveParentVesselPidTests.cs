using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for VesselSpawner.ResolveParentVesselPid — resolves the parent recording's
    /// VesselPersistentId for EVA collision exemption during spawn.
    /// Bug T57: EVA recordings fail to spawn because their entire trajectory overlaps
    /// with the already-spawned parent vessel.
    /// </summary>
    [Collection("Sequential")]
    public class ResolveParentVesselPidTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ResolveParentVesselPidTests()
        {
            RecordingStore.SuppressLogging = true;
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

        [Fact]
        public void ResolvesParentPid_FromCommittedTree()
        {
            string treeId = Guid.NewGuid().ToString("N");
            string parentId = Guid.NewGuid().ToString("N");
            string evaId = Guid.NewGuid().ToString("N");

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "KerbalX",
                RootRecordingId = parentId
            };
            tree.Recordings[parentId] = new Recording
            {
                RecordingId = parentId,
                TreeId = treeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 12345
            };
            tree.Recordings[evaId] = new Recording
            {
                RecordingId = evaId,
                TreeId = treeId,
                VesselName = "Bob Kerman",
                EvaCrewName = "Bob Kerman",
                ParentRecordingId = parentId
            };
            RecordingStore.AddCommittedTreeForTesting(tree);

            uint pid = VesselSpawner.ResolveParentVesselPid(tree.Recordings[evaId]);

            Assert.Equal(12345u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenNoParentRecordingId()
        {
            var rec = new Recording
            {
                RecordingId = "eva1",
                TreeId = "tree1",
                EvaCrewName = "Bob",
                ParentRecordingId = null
            };

            uint pid = VesselSpawner.ResolveParentVesselPid(rec);

            Assert.Equal(0u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenNoTreeId()
        {
            var rec = new Recording
            {
                RecordingId = "eva1",
                TreeId = null,
                EvaCrewName = "Bob",
                ParentRecordingId = "parent1"
            };

            uint pid = VesselSpawner.ResolveParentVesselPid(rec);

            Assert.Equal(0u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenTreeNotFound()
        {
            var rec = new Recording
            {
                RecordingId = "eva1",
                TreeId = "nonexistent",
                EvaCrewName = "Bob",
                ParentRecordingId = "parent1"
            };

            uint pid = VesselSpawner.ResolveParentVesselPid(rec);

            Assert.Equal(0u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenParentRecordingNotInTree()
        {
            string treeId = Guid.NewGuid().ToString("N");
            string rootId = Guid.NewGuid().ToString("N");
            string evaId = Guid.NewGuid().ToString("N");

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "KerbalX",
                RootRecordingId = rootId
            };
            tree.Recordings[rootId] = new Recording
            {
                RecordingId = rootId,
                TreeId = treeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 12345
            };
            RecordingStore.AddCommittedTreeForTesting(tree);

            var eva = new Recording
            {
                RecordingId = evaId,
                TreeId = treeId,
                EvaCrewName = "Bob",
                ParentRecordingId = "doesNotExist"
            };

            uint pid = VesselSpawner.ResolveParentVesselPid(eva);

            Assert.Equal(0u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenParentHasNoPid()
        {
            string treeId = Guid.NewGuid().ToString("N");
            string parentId = Guid.NewGuid().ToString("N");
            string evaId = Guid.NewGuid().ToString("N");

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "KerbalX",
                RootRecordingId = parentId
            };
            tree.Recordings[parentId] = new Recording
            {
                RecordingId = parentId,
                TreeId = treeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 0 // no PID
            };
            tree.Recordings[evaId] = new Recording
            {
                RecordingId = evaId,
                TreeId = treeId,
                VesselName = "Bob Kerman",
                EvaCrewName = "Bob Kerman",
                ParentRecordingId = parentId
            };
            RecordingStore.AddCommittedTreeForTesting(tree);

            uint pid = VesselSpawner.ResolveParentVesselPid(tree.Recordings[evaId]);

            Assert.Equal(0u, pid);
        }

        [Fact]
        public void ReturnsZero_WhenNullRecording()
        {
            uint pid = VesselSpawner.ResolveParentVesselPid(null);
            Assert.Equal(0u, pid);
        }

        [Fact]
        public void LogsParentResolution()
        {
            string treeId = Guid.NewGuid().ToString("N");
            string parentId = Guid.NewGuid().ToString("N");
            string evaId = Guid.NewGuid().ToString("N");

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "KerbalX",
                RootRecordingId = parentId
            };
            tree.Recordings[parentId] = new Recording
            {
                RecordingId = parentId,
                TreeId = treeId,
                VesselName = "Kerbal X",
                VesselPersistentId = 99999
            };
            tree.Recordings[evaId] = new Recording
            {
                RecordingId = evaId,
                TreeId = treeId,
                VesselName = "Bob Kerman",
                EvaCrewName = "Bob Kerman",
                ParentRecordingId = parentId
            };
            RecordingStore.AddCommittedTreeForTesting(tree);
            logLines.Clear();

            VesselSpawner.ResolveParentVesselPid(tree.Recordings[evaId]);

            Assert.Contains(logLines, l =>
                l.Contains("[Spawner]") && l.Contains("T57") && l.Contains("99999"));
        }
    }
}
