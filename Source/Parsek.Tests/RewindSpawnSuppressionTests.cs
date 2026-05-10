using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Focused tests for the scoped SpawnSuppressedByRewind lifecycle (#573/#589).
    /// </summary>
    [Collection("Sequential")]
    public class RewindSpawnSuppressionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public RewindSpawnSuppressionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
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
        public void MarkRewoundTreeRecordingsAsGhostOnly_LogsSourceProtectionAndFutureSkip()
        {
            const uint sourcePid = 2708531065u;
            const string treeId = "tree-589";
            const double rewindUT = 10.0;

            var source = MakeRecording(
                "source",
                "Kerbal X",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: 30.0);
            var futureSameTree = MakeRecording(
                "future-bob",
                "Bob Kerman",
                sourcePid,
                treeId,
                startUT: 24034.0,
                endUT: 24062.0);

            RecordingStore.RewindReplayTargetSourcePid = sourcePid;
            RecordingStore.RewindReplayTargetRecordingId = source.RecordingId;
            RewindContext.BeginRewind(rewindUT, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(
                new List<Recording> { source, futureSameTree });

            Assert.Equal(1, marked);
            Assert.True(source.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                source.SpawnSuppressedByRewindReason);
            Assert.False(futureSameTree.SpawnSuppressedByRewind);

            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind applied") &&
                l.Contains("reason=same-recording") &&
                l.Contains("#573 active/source recording protection"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind not applied") &&
                l.Contains("reason=same-tree-future-recording") &&
                l.Contains("spawn allowed when endpoint is reached post-rewind"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind scope summary") &&
                l.Contains("futureAllowed=1"));
        }

        [Fact]
        public void ShouldSpawnAtRecordingEnd_LegacyUnscopedMarker_IsClearedAndSpawnAllowed()
        {
            var futureSameTree = MakeRecording(
                "future-legacy",
                "Bob Kerman",
                736156658u,
                "tree-legacy",
                startUT: 24034.0,
                endUT: 24062.0);
            futureSameTree.SpawnSuppressedByRewind = true;

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                futureSameTree,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(result.needsSpawn);
            Assert.False(futureSameTree.SpawnSuppressedByRewind);
            Assert.Null(futureSameTree.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(futureSameTree.SpawnSuppressedByRewindUT));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind cleared") &&
                l.Contains("reason=legacy-unscoped"));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("Spawn allowed despite same-tree rewind") &&
                l.Contains("markerReason=legacy-unscoped"));
        }

        [Fact]
        public void RepeatedRewind_ClearsStaleSuppressionOnUnrelatedRecording()
        {
            const uint firstPid = 111u;
            const uint secondPid = 222u;

            var staleFirstTree = MakeRecording(
                "first-source",
                "First Source",
                firstPid,
                "first-tree",
                startUT: 0.0,
                endUT: 20.0);
            staleFirstTree.SpawnSuppressedByRewind = true;
            staleFirstTree.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            staleFirstTree.SpawnSuppressedByRewindUT = 0.0;

            var secondSource = MakeRecording(
                "second-source",
                "Second Source",
                secondPid,
                "second-tree",
                startUT: 100.0,
                endUT: 160.0);

            RecordingStore.RewindReplayTargetSourcePid = secondPid;
            RecordingStore.RewindReplayTargetRecordingId = secondSource.RecordingId;
            RewindContext.BeginRewind(120.0, default(BudgetSummary), 0, 0, 0);

            int marked = ParsekScenario.MarkRewoundTreeRecordingsAsGhostOnly(
                new List<Recording> { staleFirstTree, secondSource });

            Assert.Equal(1, marked);
            Assert.False(staleFirstTree.SpawnSuppressedByRewind);
            Assert.True(secondSource.SpawnSuppressedByRewind);
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind cleared") &&
                l.Contains("stale marker no longer matches active/source scope"));
        }

        [Fact]
        public void ResetAllPlaybackState_LogsSuppressionClearLifecycle()
        {
            var rec = MakeRecording(
                "reset-source",
                "Reset Source",
                333u,
                "reset-tree",
                startUT: 0.0,
                endUT: 30.0);
            rec.SpawnSuppressedByRewind = true;
            rec.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            rec.SpawnSuppressedByRewindUT = 0.0;
            RecordingStore.AddRecordingWithTreeForTesting(rec);

            RecordingStore.ResetAllPlaybackState();

            Assert.False(rec.SpawnSuppressedByRewind);
            Assert.Null(rec.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(rec.SpawnSuppressedByRewindUT));
            Assert.Contains(logLines, l =>
                l.Contains("[Rewind]") &&
                l.Contains("SpawnSuppressedByRewind reset") &&
                l.Contains("reason=same-recording"));
        }

        [Fact]
        public void RecordingTree_SaveLoadRoundTrip_PreservesScopedSuppressionMetadata()
        {
            var source = MakeRecording(
                "persist-source",
                "Persist Source",
                444u,
                "persist-tree",
                startUT: 0.0,
                endUT: 30.0);
            source.SpawnSuppressedByRewind = true;
            source.SpawnSuppressedByRewindReason =
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording;
            source.SpawnSuppressedByRewindUT = 12.5;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, source);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.True(loaded.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                loaded.SpawnSuppressedByRewindReason);
            Assert.Equal(12.5, loaded.SpawnSuppressedByRewindUT);
        }

        [Fact]
        public void RecordingTree_LoadLegacyBoolOnlySuppression_TagsLegacyUnscoped()
        {
            var legacy = MakeRecording(
                "legacy-source",
                "Legacy Source",
                555u,
                "legacy-tree",
                startUT: 0.0,
                endUT: 30.0);
            legacy.SpawnSuppressedByRewind = true;

            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, legacy);

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.True(loaded.SpawnSuppressedByRewind);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                loaded.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(loaded.SpawnSuppressedByRewindUT));
        }

        [Fact]
        public void RecordingTree_LoadStraySuppressionMetadataWithoutBool_IgnoresReasonAndUT()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("id", "stray-metadata");
            node.AddValue("spawnSuppressedByRewindReason",
                ParsekScenario.RewindSpawnSuppressionReasonSameRecording);
            node.AddValue("spawnSuppressedByRewindUT", "12.5");

            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(node, loaded);

            Assert.False(loaded.SpawnSuppressedByRewind);
            Assert.Null(loaded.SpawnSuppressedByRewindReason);
            Assert.True(double.IsNaN(loaded.SpawnSuppressedByRewindUT));
        }

        [Fact]
        public void RecordingTree_LoadLegacyWholeTreeMarkers_PreservesProbableSourceAndAllowsFuture()
        {
            var source = MakeRecording(
                "legacy-root",
                "Legacy Root",
                666u,
                "legacy-tree",
                startUT: 0.0,
                endUT: 30.0);
            source.TreeOrder = 0;
            source.SpawnSuppressedByRewind = true;

            var futureSameTree = MakeRecording(
                "legacy-future",
                "Legacy Future Lander",
                666u,
                "legacy-tree",
                startUT: 24034.0,
                endUT: 24062.0);
            futureSameTree.TreeOrder = 1;
            futureSameTree.ParentRecordingId = source.RecordingId;
            futureSameTree.SpawnSuppressedByRewind = true;

            var tree = new RecordingTree
            {
                Id = "legacy-tree",
                TreeName = "Legacy Tree",
                RootRecordingId = source.RecordingId,
            };
            tree.AddOrReplaceRecording(source);
            tree.AddOrReplaceRecording(futureSameTree);

            var node = new ConfigNode("TREE");
            tree.Save(node);

            var loadedTree = RecordingTree.Load(node);
            var loadedSource = loadedTree.Recordings[source.RecordingId];
            var loadedFuture = loadedTree.Recordings[futureSameTree.RecordingId];
            loadedSource.VesselSnapshot = new ConfigNode("VESSEL");
            loadedFuture.VesselSnapshot = new ConfigNode("VESSEL");

            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                loadedSource.SpawnSuppressedByRewindReason);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                loadedFuture.SpawnSuppressedByRewindReason);

            var sourceResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                loadedSource,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: loadedTree);
            Assert.False(sourceResult.needsSpawn);
            Assert.Contains("#573", sourceResult.reason);

            var futureResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                loadedFuture,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: loadedTree);
            Assert.True(futureResult.needsSpawn);
            Assert.False(loadedFuture.SpawnSuppressedByRewind);

            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("Legacy SpawnSuppressedByRewind markers normalized") &&
                l.Contains("sourceProtected=1") &&
                l.Contains("futureEligible=1"));
        }

        [Fact]
        public void RecordingTree_LoadLegacyWholeTreeMarkers_DoesNotTreatFutureActiveLeafAsSource()
        {
            var source = MakeRecording(
                "legacy-root-active-case",
                "Legacy Root Active Case",
                777u,
                "legacy-tree-active-case",
                startUT: 0.0,
                endUT: 30.0);
            source.TreeOrder = 0;
            source.SpawnSuppressedByRewind = true;

            var activeFutureLeaf = MakeRecording(
                "legacy-active-future",
                "Legacy Active Future Lander",
                777u,
                "legacy-tree-active-case",
                startUT: 24034.0,
                endUT: 24062.0);
            activeFutureLeaf.TreeOrder = 1;
            activeFutureLeaf.ParentRecordingId = source.RecordingId;
            activeFutureLeaf.SpawnSuppressedByRewind = true;

            var tree = new RecordingTree
            {
                Id = "legacy-tree-active-case",
                TreeName = "Legacy Tree Active Case",
                // ActiveRecordingId can persist as a future terminal leaf on committed
                // legacy trees, so normalization must prefer the root when both exist.
                RootRecordingId = source.RecordingId,
                ActiveRecordingId = activeFutureLeaf.RecordingId,
            };
            tree.AddOrReplaceRecording(source);
            tree.AddOrReplaceRecording(activeFutureLeaf);

            var node = new ConfigNode("TREE");
            tree.Save(node);

            var loadedTree = RecordingTree.Load(node);
            var loadedSource = loadedTree.Recordings[source.RecordingId];
            var loadedFuture = loadedTree.Recordings[activeFutureLeaf.RecordingId];
            loadedSource.VesselSnapshot = new ConfigNode("VESSEL");
            loadedFuture.VesselSnapshot = new ConfigNode("VESSEL");

            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording,
                loadedSource.SpawnSuppressedByRewindReason);
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                loadedFuture.SpawnSuppressedByRewindReason);

            var secondNode = new ConfigNode("TREE");
            loadedTree.Save(secondNode);
            var reloadedTree = RecordingTree.Load(secondNode);
            var reloadedFuture = reloadedTree.Recordings[activeFutureLeaf.RecordingId];
            reloadedFuture.VesselSnapshot = new ConfigNode("VESSEL");

            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonLegacyUnscoped,
                reloadedFuture.SpawnSuppressedByRewindReason);

            var futureResult = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                reloadedFuture,
                isActiveChainMember: false,
                isChainLooping: false,
                treeContext: reloadedTree);
            Assert.True(futureResult.needsSpawn);
            Assert.False(reloadedFuture.SpawnSuppressedByRewind);
        }

        [Fact]
        public void ShouldApplyRewindSpawnSuppression_BoundaryOverlapUsesEpsilonOnly()
        {
            const uint sourcePid = 888u;
            const string treeId = "boundary-tree";
            const double rewindUT = 100.0;

            var endsExactlyAtRewind = MakeRecording(
                "ends-exactly",
                "Boundary Exact",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT);
            var endsWithinBoundaryEpsilon = MakeRecording(
                "ends-within-epsilon",
                "Boundary Within",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT - 0.0005);
            var endedBeforeBoundary = MakeRecording(
                "ended-before",
                "Boundary Before",
                sourcePid,
                treeId,
                startUT: 0.0,
                endUT: rewindUT - 0.01);

            Assert.True(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endsExactlyAtRewind,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string exactReason));
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording, exactReason);

            Assert.True(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endsWithinBoundaryEpsilon,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string withinReason));
            Assert.Equal(ParsekScenario.RewindSpawnSuppressionReasonSameRecording, withinReason);

            Assert.False(ParsekScenario.ShouldApplyRewindSpawnSuppression(
                endedBeforeBoundary,
                rewindRecordingId: "different-source",
                rewindSourcePid: sourcePid,
                rewoundTreeId: treeId,
                rewindUT: rewindUT,
                out string beforeReason));
            Assert.Null(beforeReason);
        }

        private static Recording MakeRecording(
            string recordingId,
            string vesselName,
            uint pid,
            string treeId,
            double startUT,
            double endUT)
        {
            return new Recording
            {
                RecordingId = recordingId,
                VesselName = vesselName,
                VesselPersistentId = pid,
                TreeId = treeId,
                ExplicitStartUT = startUT,
                ExplicitEndUT = endUT,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Landed,
            };
        }

        // ----------------------------------------------------------------
        // Canon (Immutable) Re-Fly fork preservation: end-to-end spawn-gate
        // assertion for fix-rewind-canon-forks. The predicate-classifier
        // upstream keeps the supersede relation across parent rewind; this
        // test confirms ShouldSpawnAtRecordingEnd reports needsSpawn=true
        // for the preserved canon fork, so its terminal-orbit
        // re-materialization actually fires when ghost playback completes.
        // ----------------------------------------------------------------

        [Fact]
        public void ShouldSpawnAtRecordingEnd_ReturnsTrue_ForPreservedCanonForkAfterParentRewind()
        {
            // Tree-less recording: ShouldSpawnAtRecordingEnd's
            // IsEffectiveLeafForVessel / IsNonLeafInTree helpers early-exit on
            // a null/empty TreeId before walking any tree. This is the
            // simplest valid fixture for this assertion; the full
            // tree-fixture path is exercised by other tests in this file
            // (e.g. ResetAllPlaybackState_LogsSuppressionClearLifecycle uses
            // AddRecordingWithTreeForTesting). Both shapes converge on the
            // same gate here because the tree helpers cannot reduce a leaf-
            // shaped recording's spawn eligibility.
            var canonFork = new Recording
            {
                RecordingId = "canon-orbital-probe",
                VesselName = "Kerbal X Probe",
                VesselPersistentId = 2823934496u,
                ExplicitStartUT = 456.79,
                ExplicitEndUT = 992.23,
                VesselSnapshot = new ConfigNode("VESSEL"),
                TerminalStateValue = TerminalState.Orbiting,
                MergeState = MergeState.Immutable,
                // Post-rewind state: VesselSpawned was reset by the rewind's
                // ResetAllPlaybackState (the live Re-Fly vessel was stripped),
                // so the spawn-at-recording-end path can fire fresh.
                VesselSpawned = false,
                SpawnedVesselPersistentId = 0,
                // Predicate-classifier preserved the supersede relation, so
                // SpawnSuppressedByRewind was never set on the canon fork.
                // (It only ever flagged the active/source recording per #589.)
                SpawnSuppressedByRewind = false,
                // Canon fork has no terminal-spawn supersession from a
                // downstream recording that's still live.
                TerminalSpawnSupersededByRecordingId = null,
            };

            var result = GhostPlaybackLogic.ShouldSpawnAtRecordingEnd(
                canonFork,
                isActiveChainMember: false,
                isChainLooping: false);

            Assert.True(result.needsSpawn,
                $"Canon Immutable orbital fork must be spawn-eligible after " +
                $"parent rewind so the persistent vessel re-materializes when " +
                $"ghost playback reaches EndUT. Got reason='{result.reason}'.");
            Assert.Equal(string.Empty, result.reason);
        }
    }
}
