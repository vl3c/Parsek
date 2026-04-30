using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class ReFlyChainRootGhostSnapshotTests : IDisposable
    {
        private const string ParentId = "0eec0dd94ebf41efb3b200a97952aa4c";
        private const string ChildAId = "b3748511dc7841c9a12f7dbad51fa157";
        private const string ChildBId = "520d4d6c2c7a48f8affa4fcc7b36b844";
        private const string TreeId = "2c7abef824c741debd51ac9d4f819032";
        private const string BranchPointId = "901e1a765a6d4e75b99fc80760215415";

        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public ReFlyChainRootGhostSnapshotTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-refly-root-ghost-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            IncompleteBallisticSceneExitFinalizer.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        [Fact]
        public void ReFlyBreakupRoot_SceneExitFinalizerPreservesGhostSnapshotAcrossRoundTrip()
        {
            ConfigNode recordedStartGhost = VesselSnapshotBuilder
                .FleaRocket("Kerbal X", "Valentina Kerman", pid: 2708531065u)
                .Build();
            ConfigNode terminalEmptyVessel = BuildEmptyVesselSnapshot("Kerbal X", 2708531065u);

            Recording parent = MakeRecording(
                ParentId,
                "Kerbal X",
                pid: 2708531065u,
                vesselSnapshot: recordedStartGhost.CreateCopy(),
                ghostSnapshot: recordedStartGhost.CreateCopy());
            parent.ChildBranchPointId = BranchPointId;

            IncompleteBallisticSceneExitFinalizer.TryFinalizeOverrideForTesting =
                (Recording recording, Vessel vessel, double commitUT, out IncompleteBallisticFinalizationResult result) =>
                {
                    result = new IncompleteBallisticFinalizationResult
                    {
                        terminalState = TerminalState.Destroyed,
                        terminalUT = commitUT + 5.0,
                        vesselSnapshot = terminalEmptyVessel,
                        ghostVisualSnapshot = terminalEmptyVessel.CreateCopy()
                    };
                    return true;
                };

            Assert.True(IncompleteBallisticSceneExitFinalizer.TryApply(
                parent,
                vessel: null,
                commitUT: 20.0,
                logContext: "ReFlyChainRootGhostSnapshotTests"));
            Assert.Equal(0, CountPartNodes(parent.VesselSnapshot));
            Assert.True(CountPartNodes(parent.GhostVisualSnapshot) > 0);
            Assert.Equal(GhostSnapshotMode.Separate, parent.GhostSnapshotMode);

            Recording childA = MakeChildRecording(
                ChildAId,
                "Kerbal X Debris",
                pid: 4193611995u);
            Recording childB = MakeChildRecording(
                ChildBId,
                "Kerbal X Debris",
                pid: 2588600277u);

            RecordingTree tree = BuildBreakupTree(parent, childA, childB);
            var preSidecarTreeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(preSidecarTreeNode);
            RecordingTree preSidecarLoadedTree = RecordingTree.Load(preSidecarTreeNode);
            Assert.Equal(GhostSnapshotMode.Separate, preSidecarLoadedTree.Recordings[ParentId].GhostSnapshotMode);

            SaveSidecars(parent);
            SaveSidecars(childA);
            SaveSidecars(childB);

            Assert.True(File.Exists(GhostPath(ParentId)));

            var treeNode = new ConfigNode("RECORDING_TREE");
            tree.Save(treeNode);

            RecordingTree loadedTree = RecordingTree.Load(treeNode);
            Recording loadedParent = loadedTree.Recordings[ParentId];
            Assert.True(RecordingStore.LoadRecordingFilesFromPathsForTesting(
                loadedParent,
                PrecPath(ParentId),
                VesselPath(ParentId),
                GhostPath(ParentId)));

            Assert.Equal(GhostSnapshotMode.Separate, loadedParent.GhostSnapshotMode);
            Assert.Equal(0, CountPartNodes(loadedParent.VesselSnapshot));
            Assert.True(CountPartNodes(loadedParent.GhostVisualSnapshot) > 0);
            Assert.DoesNotContain(logLines, l =>
                l.Contains("missing ghost snapshot") ||
                l.Contains("Ghost build aborted for 'Kerbal X': snapshot has no PART nodes"));
        }

        private static RecordingTree BuildBreakupTree(
            Recording parent,
            Recording childA,
            Recording childB)
        {
            var tree = new RecordingTree
            {
                Id = TreeId,
                TreeName = "Kerbal X",
                RootRecordingId = ParentId,
                ActiveRecordingId = ParentId
            };

            parent.TreeId = TreeId;
            childA.TreeId = TreeId;
            childB.TreeId = TreeId;
            tree.AddOrReplaceRecording(parent);
            tree.AddOrReplaceRecording(childA);
            tree.AddOrReplaceRecording(childB);
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = BranchPointId,
                Type = BranchPointType.Breakup,
                UT = 16442.91,
                BreakupCause = "CRASH",
                BreakupDuration = 0.5,
                DebrisCount = 2,
                CoalesceWindow = 0.5,
                ParentRecordingIds = new List<string> { ParentId },
                ChildRecordingIds = new List<string> { ChildAId, ChildBId }
            });
            return tree;
        }

        private Recording MakeChildRecording(string id, string vesselName, uint pid)
        {
            ConfigNode snapshot = VesselSnapshotBuilder
                .ProbeShip(vesselName, pid)
                .Build();
            Recording rec = MakeRecording(id, vesselName, pid, snapshot, snapshot.CreateCopy());
            rec.ParentBranchPointId = BranchPointId;
            rec.IsDebris = true;
            return rec;
        }

        private static Recording MakeRecording(
            string id,
            string vesselName,
            uint pid,
            ConfigNode vesselSnapshot,
            ConfigNode ghostSnapshot)
        {
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                VesselName = vesselName,
                VesselPersistentId = pid,
                ExplicitStartUT = 10.0,
                ExplicitEndUT = 15.0,
                VesselSnapshot = vesselSnapshot,
                GhostVisualSnapshot = ghostSnapshot
            };
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 10.0,
                latitude = -0.1,
                longitude = -74.5,
                altitude = 1100.0,
                bodyName = "Kerbin"
            });
            rec.Points.Add(new TrajectoryPoint
            {
                ut = 15.0,
                latitude = -0.1,
                longitude = -74.6,
                altitude = 900.0,
                bodyName = "Kerbin"
            });
            rec.GhostSnapshotMode = RecordingStore.DetermineGhostSnapshotMode(rec);
            return rec;
        }

        private static ConfigNode BuildEmptyVesselSnapshot(string name, uint pid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            node.AddValue("name", name);
            node.AddValue("type", "Ship");
            node.AddValue("sit", "FLYING");
            node.AddValue("root", "0");
            node.AddNode("ORBIT");
            return node;
        }

        private void SaveSidecars(Recording rec)
        {
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec,
                PrecPath(rec.RecordingId),
                VesselPath(rec.RecordingId),
                GhostPath(rec.RecordingId)));
        }

        private string PrecPath(string id) => Path.Combine(tempDir, id + ".prec");
        private string VesselPath(string id) => Path.Combine(tempDir, id + "_vessel.craft");
        private string GhostPath(string id) => Path.Combine(tempDir, id + "_ghost.craft");

        private static int CountPartNodes(ConfigNode snapshot)
        {
            return snapshot?.GetNodes("PART")?.Length ?? 0;
        }
    }
}
