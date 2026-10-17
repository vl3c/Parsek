using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Todo REFLY-MERGE-GHOST-ONLY-NULLS-A-SPAWNED-LEAF. A Re-Fly merge builds its default
    /// vessel decisions through <c>ShouldSpawnAtRecordingEnd</c>, which answers "already
    /// spawned" for a leaf whose real vessel already materialized at its end, so
    /// <c>ApplyVesselDecisions</c> nulls that leaf's in-memory <c>VesselSnapshot</c>. These
    /// cells drive the real decision, sidecar-save, rewind-reset and spawn-gate methods to
    /// pin whether a later rewind past the leaf's start still spawns the vessel at its end:
    /// the in-memory null is transient, the <c>_vessel.craft</c> sidecar is never deleted by
    /// a save with a null snapshot, and the spawn gate re-hydrates from it.
    /// </summary>
    [Collection("Sequential")]
    public class ReFlyGhostOnlyLeafRespawnTests : IDisposable
    {
        private const string LeafId = "4af6cfd7interceptorleaf";
        private const uint SpawnedPid = 424242u;

        private readonly List<string> logLines = new List<string>();
        private readonly string saveRoot;

        public ReFlyGhostOnlyLeafRespawnTests()
        {
            RecordingStore.SkipSidecarCurrencyCheckForTesting = false;
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            saveRoot = Path.Combine(
                Path.GetTempPath(), "parsek-refly-ghostonly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(saveRoot);
            RecordingPaths.SaveRootOverrideForTesting = saveRoot;
        }

        public void Dispose()
        {
            RecordingPaths.SaveRootOverrideForTesting = null;
            try
            {
                if (Directory.Exists(saveRoot))
                    Directory.Delete(saveRoot, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
        }

        private static ConfigNode MakeVesselNode()
        {
            var n = new ConfigNode("VESSEL");
            n.AddValue("name", "Kerbal X");
            n.AddValue("sit", "ORBITING");
            return n;
        }

        /// <summary>
        /// The Interceptor leaf as committed by the FIRST merge: a spawnable orbiting leaf
        /// whose real vessel has since spawned at its end.
        /// </summary>
        private static RecordingTree MakeTreeWithSpawnedLeaf(out Recording leaf)
        {
            var tree = new RecordingTree { Id = "tree-rc1", TreeName = "Kerbal X" };
            leaf = new Recording
            {
                RecordingId = LeafId,
                TreeId = tree.Id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                VesselName = "Kerbal X",
                TerminalStateValue = TerminalState.Orbiting,
                VesselSnapshot = MakeVesselNode(),
                VesselSpawned = true,
                SpawnedVesselPersistentId = SpawnedPid,
            };
            leaf.Points.Add(new TrajectoryPoint
            {
                ut = 100, latitude = 0, longitude = 0, altitude = 80000,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 2200, 0), bodyName = "Kerbin"
            });
            leaf.Points.Add(new TrajectoryPoint
            {
                ut = 200, latitude = 0.1, longitude = 1.0, altitude = 80000,
                rotation = new Quaternion(0, 0, 0, 1),
                velocity = new Vector3(0, 2200, 0), bodyName = "Kerbin"
            });
            tree.Recordings[LeafId] = leaf;
            return tree;
        }

        private static string VesselSidecarPath(string id)
            => RecordingPaths.ResolveSaveScopedPath(RecordingPaths.BuildVesselSnapshotRelativePath(id));

        /// <summary>
        /// Steps shared by every cell: the first merge's OnSave wrote the sidecar with the
        /// snapshot; the Re-Fly merge's default decision ghost-onlys the already-spawned
        /// leaf; ApplyVesselDecisions nulls the snapshot; the next OnSave runs.
        /// </summary>
        private RecordingTree GhostOnlyTheSpawnedLeafThroughAReFlyMerge(
            bool sidecarWrittenBeforeMerge, out Recording leaf)
        {
            var tree = MakeTreeWithSpawnedLeaf(out leaf);
            if (sidecarWrittenBeforeMerge)
            {
                Assert.True(RecordingStore.SaveRecordingFiles(leaf, incrementEpoch: true));
                Assert.True(File.Exists(VesselSidecarPath(LeafId)));
            }

            var decisions = MergeDialog.BuildDefaultVesselDecisions(tree);
            Assert.False(decisions[LeafId]); // "already spawned" -> ghost-only default

            MergeDialog.ApplyVesselDecisions(tree, decisions);
            Assert.Null(leaf.VesselSnapshot);
            Assert.NotNull(leaf.GhostVisualSnapshot);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("ApplyVesselDecisions: ghost-only for 'Kerbal X'")
                && l.Contains("spawn snapshot nulled"));

            // While the vessel is live the gate still refuses (already spawned) and never
            // probes disk, so no negative hydration cache is poisoned here.
            var (spawnWhileLive, liveReason) =
                GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(leaf, false, tree);
            Assert.False(spawnWhileLive);
            Assert.Contains("already spawned", liveReason);
            Assert.False(leaf.VesselSnapshotHydrationFailed);

            // OnSave after the merge, with the in-memory snapshot null.
            Assert.True(RecordingStore.SaveRecordingFiles(leaf, incrementEpoch: true));
            return tree;
        }

        [Fact]
        public void GhostOnlyOnSpawnedLeaf_SaveKeepsVesselSidecar_RewindToLaunchReset_SpawnsAtEndAgain()
        {
            var tree = GhostOnlyTheSpawnedLeafThroughAReFlyMerge(
                sidecarWrittenBeforeMerge: true, out var leaf);

            // A save with a null snapshot neither deletes nor blanks the vessel sidecar.
            Assert.True(File.Exists(VesselSidecarPath(LeafId)));

            // Rewind-to-Launch to before the leaf's start: the committed tree's spawn
            // tracking is reset (the leaf is not kept committed history).
            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.ResetAllPlaybackState();
            Assert.False(leaf.VesselSpawned);
            Assert.Equal(0u, leaf.SpawnedVesselPersistentId);
            Assert.Null(leaf.VesselSnapshot); // still null in memory across the in-session load

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(leaf, false, tree);

            Assert.True(needsSpawn, "expected spawn after rewind, got reason: " + reason);
            Assert.NotNull(leaf.VesselSnapshot);
            Assert.Equal("Kerbal X", leaf.VesselSnapshot.GetValue("name"));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("Re-hydrated vessel snapshot from sidecar for spawn"));
        }

        [Fact]
        public void GhostOnlyOnSpawnedLeaf_InSessionLoadReset_ReFlyPath_SpawnsAtEndAgain()
        {
            var tree = GhostOnlyTheSpawnedLeafThroughAReFlyMerge(
                sidecarWrittenBeforeMerge: true, out var leaf);

            // Re-Fly / in-session load: the OnLoad tree-mutable-state reset zeroes spawn
            // tracking; an RP quicksave older than the leaf carries no spawnedPid for it,
            // so the tree-state-restore loop leaves it at zero.
            int reset = ParsekScenario.ResetTreeRecordingMutableStateForLoad(
                new List<Recording> { leaf });
            Assert.Equal(1, reset);
            Assert.False(leaf.VesselSpawned);
            Assert.Equal(0u, leaf.SpawnedVesselPersistentId);

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(leaf, false, tree);

            Assert.True(needsSpawn, "expected spawn after Re-Fly reset, got reason: " + reason);
            Assert.NotNull(leaf.VesselSnapshot);
        }

        [Fact]
        public void GhostOnlyOnSpawnedLeaf_ColdLoad_ReadsSnapshotBackFromSidecar()
        {
            GhostOnlyTheSpawnedLeafThroughAReFlyMerge(sidecarWrittenBeforeMerge: true, out var leaf);

            // Cold load of the post-merge .sfs: metadata from the node, bulk from disk.
            var sfs = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(sfs, leaf);
            var reloaded = new Recording();
            RecordingTree.LoadRecordingFrom(sfs, reloaded);
            Assert.True(RecordingStore.LoadRecordingFiles(reloaded));

            Assert.NotNull(reloaded.VesselSnapshot);
            Assert.Equal("Kerbal X", reloaded.VesselSnapshot.GetValue("name"));
        }

        [Fact]
        public void Boundary_LeafNeverSavedWithItsSnapshot_CannotRespawnAfterRewind()
        {
            // Precondition the green cells rely on: the sidecar must have been written
            // while the snapshot was still in memory. A leaf ghost-only'd before any save
            // has no durable copy and the gate answers "no vessel snapshot".
            var tree = GhostOnlyTheSpawnedLeafThroughAReFlyMerge(
                sidecarWrittenBeforeMerge: false, out var leaf);
            Assert.False(File.Exists(VesselSidecarPath(LeafId)));

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.ResetAllPlaybackState();

            var (needsSpawn, reason) = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(leaf, false, tree);
            Assert.False(needsSpawn);
            Assert.Equal("no vessel snapshot", reason);
        }
    }
}
