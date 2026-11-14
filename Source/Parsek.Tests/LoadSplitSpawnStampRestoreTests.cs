using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// LOAD-SPLIT-SPAWN-STAMP-ON-HEAD: loading a save whose ship is in flight must continue
    /// that ship's committed recording. The load-time optimizer split gives the later half
    /// the recording's end (terminal state, vessel snapshot); the spawn stamp has to go with
    /// it, or the crew pass marks the tail spawned with no pid and the committed-spawned
    /// restore finds no recording for the live vessel (the B39-B42 Jool forges, 2026-11-14).
    /// </summary>
    [Collection("Sequential")]
    public class LoadSplitSpawnStampRestoreTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorStoreSuppress;

        public LoadSplitSpawnStampRestoreTests()
        {
            priorStoreSuppress = RecordingStore.SuppressLogging;
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = true;

            RecordingStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            RecordingStore.SaveGameForTesting = null;
            GameStateStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        private const string JoolFixture = "jool-orbit-recorded";
        private const string JoolTreeId = "6ec68e87b782476f970a71df8c8b4685";
        private const string JoolMainId = "ddcc6d007ff54a9c8246c7958011883a";
        private const uint JoolVesselPid = 807470910u;
        private const string JoolVesselGuid = "5384d5f4fe8244d782aaa5cb79345562";
        // FLIGHTSTATE UT of the fixture's persistent.sfs: the live clock at scene entry.
        private const double JoolSaveUT = 27787319.41951086;

        private static string FixtureDir(string fixture)
        {
            string root = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(root, "harness", "fixtures", "saves", fixture);
        }

        /// <summary>
        /// Installs the fixture's committed tree the way a cold load leaves it before the
        /// optimization pass: tree metadata from persistent.sfs, trajectories from the text
        /// sidecars, vessel snapshots from the PSN0 sidecars (through the production codecs).
        /// </summary>
        private static RecordingTree InstallFixtureTree(string fixture)
        {
            string dir = FixtureDir(fixture);
            ConfigNode sfs = ConfigNode.Load(Path.Combine(dir, "persistent.sfs"));
            Assert.NotNull(sfs);
            ConfigNode game = sfs.GetNode("GAME") ?? sfs;
            ConfigNode treeNode = null;
            foreach (ConfigNode scenario in game.GetNodes("SCENARIO"))
            {
                if (scenario.GetValue("name") == "ParsekScenario")
                    treeNode = scenario.GetNode("RECORDING_TREE");
            }
            Assert.NotNull(treeNode);

            RecordingTree tree = RecordingTree.Load(treeNode);
            foreach (Recording rec in tree.Recordings.Values.OrderBy(r => r.RecordingId, StringComparer.Ordinal))
            {
                string recDir = Path.Combine(dir, "Parsek", "Recordings");
                string prec = Path.Combine(recDir, rec.RecordingId + ".prec.txt");
                Assert.True(File.Exists(prec), "fixture sidecar must exist: " + prec);
                TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(ConfigNode.Load(prec), rec);
                string vessel = Path.Combine(recDir, rec.RecordingId + "_vessel.craft");
                if (File.Exists(vessel))
                {
                    Assert.True(SnapshotSidecarCodec.TryLoad(vessel, out ConfigNode node, out SnapshotSidecarProbe probe),
                        rec.RecordingId + ": " + SnapshotSidecarCodec.DescribeProbe(probe));
                    rec.VesselSnapshot = node;
                }
                RecordingStore.AddCommittedInternal(rec);
            }
            RecordingStore.AddCommittedTreeInternal(tree);
            return tree;
        }

        [Fact]
        public void JoolFixture_LoadSplit_ThenRestoreResumesTheShipsRecordingOnTheChainTip()
        {
            InstallFixtureTree(JoolFixture);
            Recording before = RecordingStore.CommittedTrees[0].Recordings[JoolMainId];
            Assert.True(before.VesselSpawned);
            Assert.Equal(JoolVesselPid, before.SpawnedVesselPersistentId);

            // OnLoad's order: the optimization pass, then the crew pass.
            CommittedListOptimizer.RunOptimizationPass();
            ParsekScenario.OnLoadPhaseAutoUnreserveCrewPastEndUT(RecordingStore.CommittedRecordings, JoolSaveUT);

            RecordingTree tree = RecordingStore.CommittedTrees[0];
            Recording head = tree.Recordings[JoolMainId];
            Recording tip = tree.Recordings.Values.Single(r =>
                r != head && r.ChainId == head.ChainId && r.ChainIndex == head.ChainIndex + 1);

            // The load-time split happened and the tip owns the recording's end.
            Assert.Equal(0, head.ChainIndex);
            Assert.Equal(tip.RecordingId, tree.ActiveRecordingId);
            Assert.Equal(TerminalState.Orbiting, tip.TerminalStateValue);

            // The spawn stamp went with the end: the tip, not the head.
            Assert.True(tip.VesselSpawned);
            Assert.Equal(JoolVesselPid, tip.SpawnedVesselPersistentId);
            Assert.False(head.VesselSpawned);
            Assert.Equal(0u, head.SpawnedVesselPersistentId);
            // The crew pass left the tip's snapshot alone (it stands for a live vessel).
            Assert.NotNull(tip.VesselSnapshot);

            Assert.True(ParsekFlight.TryFindCommittedTreeForSpawnedVessel(
                RecordingStore.CommittedTrees, JoolVesselPid,
                out RecordingTree found, out string recordingId, JoolVesselGuid, JoolSaveUT));
            Assert.Equal(JoolTreeId, found.Id);
            Assert.Equal(tip.RecordingId, recordingId);

            RecordingTree liveTree = RecordingTree.DeepClone(found);
            Assert.Equal(
                ParsekFlight.CommittedSpawnedVesselRestoreAction.ResumeActiveRecording,
                ParsekFlight.PrepareCommittedTreeRestoreForSpawnedVessel(liveTree, recordingId, JoolVesselPid));

            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("Chain spawn stamp moved to the tip")
                && l.Contains(JoolMainId) && l.Contains(tip.RecordingId));
        }

        private static Recording Piece(string id, string chainId, int chainIndex, uint pid,
            TerminalState? terminal = null, uint spawnedPid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                TreeId = "tree_stamp",
                VesselName = "Probe",
                VesselPersistentId = pid,
                RecordedVesselGuid = "aaaabbbbccccddddeeeeffff00001111",
                ChainId = chainId,
                ChainIndex = chainIndex,
                TerminalStateValue = terminal,
                SpawnedVesselPersistentId = spawnedPid,
                VesselSpawned = spawnedPid != 0,
            };
        }

        [Fact]
        public void MoveChainSpawnStampsToTip_HealsASaveSplitWithTheStampOnTheHead()
        {
            Recording head = Piece("rec_head", "chain", 0, 4242u, spawnedPid: 4242u);
            Recording tip = Piece("rec_tip", "chain", 1, 4242u, TerminalState.Orbiting);

            int moved = RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { head, tip }, null);

            Assert.Equal(1, moved);
            Assert.True(tip.VesselSpawned);
            Assert.Equal(4242u, tip.SpawnedVesselPersistentId);
            Assert.False(head.VesselSpawned);
            Assert.Equal(0u, head.SpawnedVesselPersistentId);
        }

        [Fact]
        public void MoveChainSpawnStampsToTip_LeavesAStampedTipAndANonTerminalTipAlone()
        {
            Recording head = Piece("rec_head", "chain", 0, 4242u, spawnedPid: 4242u);
            Recording stampedTip = Piece("rec_tip", "chain", 1, 4242u, TerminalState.Orbiting, spawnedPid: 4242u);
            Assert.Equal(0, RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { head, stampedTip }, null));
            Assert.True(head.VesselSpawned);

            Recording head2 = Piece("rec_head2", "chain2", 0, 4242u, spawnedPid: 4242u);
            Recording openTip = Piece("rec_tip2", "chain2", 1, 4242u);
            Assert.Equal(0, RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { head2, openTip }, null));
            Assert.True(head2.VesselSpawned);
            Assert.False(openTip.VesselSpawned);
        }

        [Fact]
        public void MoveChainSpawnStampsToTip_SkipsAmbiguousOtherLaunchAndSupersededChains()
        {
            // Two stamped earlier pieces: no single owner to move.
            Recording a = Piece("rec_a", "chain_amb", 0, 4242u, spawnedPid: 4242u);
            Recording b = Piece("rec_b", "chain_amb", 1, 4242u, spawnedPid: 4242u);
            Recording c = Piece("rec_c", "chain_amb", 2, 4242u, TerminalState.Orbiting);
            Assert.Equal(0, RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { a, b, c }, null));
            Assert.False(c.VesselSpawned);

            // A stamped piece of another launch (different guid) is not the tip's vessel.
            Recording other = Piece("rec_other", "chain_launch", 0, 4242u, spawnedPid: 4242u);
            other.RecordedVesselGuid = "99998888777766665555444433332222";
            Recording otherTip = Piece("rec_other_tip", "chain_launch", 1, 4242u, TerminalState.Orbiting);
            Assert.Equal(0, RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { other, otherTip }, null));
            Assert.False(otherTip.VesselSpawned);

            // A Re-Fly HEAD / TIP pair (TIP superseded by the fork) keeps its own shape.
            Recording reHead = Piece("rec_rhead", "chain_refly", 0, 4242u, spawnedPid: 4242u);
            Recording reTip = Piece("rec_rtip", "chain_refly", 1, 4242u, TerminalState.Orbiting);
            var supersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = "rel_1", OldRecordingId = "rec_rtip", NewRecordingId = "rec_fork",
                },
            };
            Assert.Equal(0, RecordingOptimizer.MoveChainSpawnStampsToTip(new[] { reHead, reTip }, supersedes));
            Assert.True(reHead.VesselSpawned);
            Assert.False(reTip.VesselSpawned);
        }

        [Fact]
        public void MergeInto_CarriesTheAbsorbedTipsSpawnStamp()
        {
            Recording target = Piece("rec_target", "chain", 0, 4242u);
            Recording absorbed = Piece("rec_absorbed", "chain", 1, 4242u, TerminalState.Orbiting, spawnedPid: 4242u);

            RecordingOptimizer.MergeInto(target, absorbed);

            Assert.True(target.VesselSpawned);
            Assert.Equal(4242u, target.SpawnedVesselPersistentId);
        }
    }
}
