using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// COLD-LOAD-UNSTAMPED-LEAF-RESTORE-SILENT-NO-MATCH: a committed leaf whose real vessel is
    /// still in the world but carries no spawn stamp is marked spawned with no spawn pid by the
    /// Cold-load crew pass (EndUT passed). The committed-tree restore must still resume it when
    /// the live vessel is positively the recorded launch, and must count and log a refusal when
    /// the launch guid does not prove it.
    /// </summary>
    [Collection("Sequential")]
    public class ColdLoadUnstampedLeafRestoreTests : System.IDisposable
    {
        private const uint CraftPid = 612987736u;
        private const string LaunchGuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string OtherLaunchGuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const double SaveUT = 5000.0;

        private readonly List<string> logLines = new List<string>();

        public ColdLoadUnstampedLeafRestoreTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            VesselNameStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            VesselNameStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
        }

        private static ConfigNode SnapshotOf(string guid, uint pid)
        {
            var node = new ConfigNode("VESSEL");
            node.AddValue("pid", guid);
            node.AddValue("persistentId", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return node;
        }

        /// <summary>The transport's committed leaf as RSF-1's host save holds it: a snapshot,
        /// the live vessel's pid and guid, and no spawn stamp.</summary>
        private static RecordingTree UnstampedLeafTree(string recordedGuid = LaunchGuid)
        {
            var leaf = new Recording
            {
                RecordingId = "rec_leaf",
                TreeId = "tree_c",
                VesselName = "Transport C",
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = recordedGuid,
                VesselSnapshot = SnapshotOf(recordedGuid ?? LaunchGuid, CraftPid),
                ExplicitStartUT = 1000.0,
                ExplicitEndUT = 2000.0,
                TerminalStateValue = TerminalState.Landed,
            };
            var tree = new RecordingTree
            {
                Id = "tree_c",
                TreeName = "Transport C",
                RootRecordingId = leaf.RecordingId,
                ActiveRecordingId = leaf.RecordingId,
            };
            tree.Recordings[leaf.RecordingId] = leaf;
            return tree;
        }

        private static Recording CommitAndRunCrewPass(RecordingTree tree)
        {
            RecordingStore.AddCommittedTreeForTesting(tree);
            foreach (Recording rec in tree.Recordings.Values)
                RecordingStore.AddCommittedInternal(rec);
            ParsekScenario.OnLoadPhaseAutoUnreserveCrewPastEndUT(RecordingStore.CommittedRecordings, SaveUT);
            return tree.Recordings["rec_leaf"];
        }

        // Fails if: after a Cold FLIGHT load the player's next flight of a vessel whose
        // committed leaf has no spawn stamp silently opens a new tree instead of resuming
        // its own (RSF-1 run 2026-11-16_2349).
        [Fact]
        public void CrewPassMarkedLeaf_SameLaunch_RestoresIntoItsTree()
        {
            RecordingTree tree = UnstampedLeafTree();
            Recording leaf = CommitAndRunCrewPass(tree);

            // The crew pass's mark: spawned with no spawn pid, snapshot dropped.
            Assert.True(leaf.VesselSpawned);
            Assert.Equal(0u, leaf.SpawnedVesselPersistentId);
            Assert.Null(leaf.VesselSnapshot);

            Assert.True(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, CraftPid,
                out RecordingTree found, out string recordingId, LaunchGuid, SaveUT));
            Assert.Same(tree, found);
            Assert.Equal("rec_leaf", recordingId);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("matched unstamped spawned leaf 'rec_leaf'"));

            RecordingTree liveTree = RecordingTree.DeepClone(found);
            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording,
                ParsekFlight.PrepareCommittedTreeRestoreForSpawnedVessel(liveTree, recordingId, CraftPid));
        }

        // Fails if: the full take path refuses the crew-pass-marked leaf, or resumes it with
        // the spawned mark still set (the next commit would then default it to ghost-only).
        [Fact]
        public void CrewPassMarkedLeaf_TakeRestore_ClearsTheSpawnedMark()
        {
            RecordingTree tree = UnstampedLeafTree();
            CommitAndRunCrewPass(tree);

            Assert.True(ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore(
                CraftPid, out RecordingTree liveTree, out string recordingId,
                out ParsekFlight.CommittedSpawnedVesselRestoreAction action, LaunchGuid, SaveUT));
            Assert.Equal("rec_leaf", recordingId);
            Assert.Equal(ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording, action);
            Recording resumed = liveTree.Recordings[recordingId];
            Assert.False(resumed.VesselSpawned);
            Assert.Equal(0u, resumed.SpawnedVesselPersistentId);
            Assert.Equal(CraftPid, resumed.VesselPersistentId);
        }

        // Fails if: a relaunch of the same craft (shared baked pid, fresh guid) resumes the
        // older launch's crew-pass-marked leaf.
        [Fact]
        public void CrewPassMarkedLeaf_OtherLaunch_RefusedAndLogged()
        {
            CommitAndRunCrewPass(UnstampedLeafTree());

            Assert.False(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, CraftPid,
                out RecordingTree found, out string recordingId, OtherLaunchGuid, SaveUT));
            Assert.Null(found);
            Assert.Null(recordingId);
            Assert.Contains(logLines, l => l.Contains("[Flight]")
                && l.Contains("refused 1 committed recording(s)")
                && l.Contains("marked spawned with no spawn pid"));
        }

        // Fails if: the mark (which carries no pid to check) is accepted on the pid alone
        // when either launch guid is unknown.
        [Fact]
        public void CrewPassMarkedLeaf_UnknownGuid_RefusedAndLogged()
        {
            CommitAndRunCrewPass(UnstampedLeafTree(recordedGuid: null));

            Assert.False(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, CraftPid,
                out RecordingTree _, out string _, LaunchGuid, SaveUT));
            Assert.Contains(logLines, l => l.Contains("marked spawned with no spawn pid"));

            logLines.Clear();
            Assert.False(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, CraftPid + 1u,
                out RecordingTree _, out string _, LaunchGuid, SaveUT));
            Assert.DoesNotContain(logLines, l => l.Contains("marked spawned with no spawn pid"));
        }

        // Fails if: an abandoned spawn (no pid by a different route) is read as the
        // crew pass's mark.
        [Fact]
        public void IsUnstampedSpawnedLeafOfLiveLaunch_TruthTable()
        {
            var rec = new Recording
            {
                RecordingId = "r",
                VesselPersistentId = CraftPid,
                RecordedVesselGuid = LaunchGuid,
                VesselSpawned = true,
            };
            Assert.True(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid, LaunchGuid));
            Assert.False(ParsekFlight.IsUnprovenUnstampedSpawnedLeaf(rec, CraftPid, LaunchGuid));

            Assert.False(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid, null));
            Assert.True(ParsekFlight.IsUnprovenUnstampedSpawnedLeaf(rec, CraftPid, null));
            Assert.False(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid + 1u, LaunchGuid));
            Assert.False(ParsekFlight.IsUnprovenUnstampedSpawnedLeaf(rec, CraftPid + 1u, LaunchGuid));

            rec.SpawnAbandoned = true;
            Assert.False(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid, LaunchGuid));
            Assert.False(ParsekFlight.IsUnprovenUnstampedSpawnedLeaf(rec, CraftPid, null));
            rec.SpawnAbandoned = false;

            rec.SpawnedVesselPersistentId = 777u;
            Assert.False(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid, LaunchGuid));
            rec.SpawnedVesselPersistentId = 0u;

            rec.VesselSpawned = false;
            Assert.False(ParsekFlight.IsUnstampedSpawnedLeafOfLiveLaunch(rec, CraftPid, LaunchGuid));
            Assert.False(ParsekFlight.IsUnprovenUnstampedSpawnedLeaf(rec, CraftPid, null));
        }
    }
}
