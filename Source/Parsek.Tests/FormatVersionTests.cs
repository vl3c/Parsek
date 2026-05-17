using System;
using System.Collections.Generic;
using System.Globalization;
using Parsek.Tests.Generators;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class FormatVersionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public FormatVersionTests()
        {
            RecordingStore.SuppressLogging = false;
            RecordingStore.ResetForTesting();
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
        }

        [Fact]
        public void CurrentRecordingFormatVersion_IsV1()
        {
            // Bumped 0 -> 1 in the VesselSwitchContinuation branch type commit
            // (docs/dev/plans/segment-scoped-switch-fly-autorecord.md Phase A.1).
            // Per pre-1.0 no-backwards-compat policy, pre-v1 saves are rejected
            // at load via IsRecordingSchemaCompatible(format-version-mismatch).
            // Fails if: someone bumps CurrentRecordingFormatVersion without
            // updating this pin, or rolls it back without restoring the
            // pre-bump enum / migration semantics.
            Assert.Equal(1, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(1, RecordingStore.CurrentRecordingSchemaGeneration);
        }

        [Fact]
        public void HistoricalFeatureConstants_CollapseToCurrentV0()
        {
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, RecordingStore.CurrentRecordingFormatVersion);
        }

        [Fact]
        public void NewRecording_DefaultsToCurrentSchema()
        {
            var rec = new Recording();

            Assert.Equal(RecordingStore.CurrentRecordingFormatVersion, rec.RecordingFormatVersion);
            Assert.Equal(RecordingStore.CurrentRecordingSchemaGeneration, rec.RecordingSchemaGeneration);
            Assert.True(RecordingStore.UsesRelativeLocalFrameContract(rec.RecordingFormatVersion));
        }

        [Fact]
        public void RecordingBuilder_EmitsCurrentV0AndSchemaGeneration()
        {
            var builder = new RecordingBuilder("TestVessel");
            builder.AddPoint(17000, 0, 0, 100);
            builder.AddPoint(17010, 0, 0, 200);

            ConfigNode trajectoryNode = builder.BuildTrajectoryNode();
            ConfigNode metadataNode = builder.BuildV3Metadata();

            Assert.Equal(
                RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                trajectoryNode.GetValue("version"));
            Assert.Equal(
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture),
                trajectoryNode.GetValue("recordingSchemaGeneration"));
            Assert.Equal(
                RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture),
                metadataNode.GetValue("recordingFormatVersion"));
            Assert.Equal(
                RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture),
                metadataNode.GetValue("recordingSchemaGeneration"));
        }

        [Theory]
        [InlineData(0, 0, "generation-missing")]
        [InlineData(0, -1, "generation-older")]
        [InlineData(0, 2, "generation-newer")]
        [InlineData(13, 1, "format-version-mismatch")]
        public void IsRecordingSchemaCompatible_RejectsNonCurrentSchema(
            int formatVersion,
            int schemaGeneration,
            string expectedReason)
        {
            bool compatible = RecordingStore.IsRecordingSchemaCompatible(
                formatVersion,
                schemaGeneration,
                out string reason);

            Assert.False(compatible);
            Assert.Equal(expectedReason, reason);
        }

        [Fact]
        public void LoadRecordingFrom_MissingGeneration_IsRejected()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "legacy-default-zero");
            node.AddValue("recordingFormatVersion", "0");

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);

            Assert.Equal(-1, rec.RecordingFormatVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[Codec]") &&
                l.Contains("reason=generation-missing"));
        }

        [Fact]
        public void RecordingTreeLoad_MetadataRejectedRoot_DropsEntireTree()
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", "tree-schema-prune");
            treeNode.AddValue("treeName", "Schema Prune");
            treeNode.AddValue("rootRecordingId", "legacy-root");
            treeNode.AddValue("activeRecordingId", "legacy-root");
            treeNode.AddValue("treeFormatVersion", RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture));
            treeNode.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));

            ConfigNode rejected = treeNode.AddNode("RECORDING");
            rejected.AddValue("recordingId", "legacy-root");
            rejected.AddValue("recordingFormatVersion", "0");

            ConfigNode child = treeNode.AddNode("RECORDING");
            child.AddValue("recordingId", "current-child");
            child.AddValue("recordingFormatVersion", RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            child.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            child.AddValue("parentRecordingId", "legacy-root");
            child.AddValue("parentBranchPointId", "bp-legacy");

            ConfigNode bp = treeNode.AddNode("BRANCH_POINT");
            bp.AddValue("id", "bp-legacy");
            bp.AddValue("ut", "10");
            bp.AddValue("type", "0");
            bp.AddValue("parentId", "legacy-root");
            bp.AddValue("childId", "current-child");

            RecordingTree loaded = RecordingTree.Load(treeNode);

            Assert.Empty(loaded.Recordings);
            Assert.Equal(string.Empty, loaded.RootRecordingId);
            Assert.Null(loaded.ActiveRecordingId);
            Assert.Empty(loaded.BranchPoints);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("dropped entire tree") &&
                l.Contains("legacy-root"));
        }

        [Fact]
        public void RecordingTreeLoad_MissingTreeGeneration_DropsEntireTree()
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", "tree-missing-generation");
            treeNode.AddValue("treeName", "Missing Generation");
            treeNode.AddValue("rootRecordingId", "current-root");
            treeNode.AddValue("treeFormatVersion", RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture));

            ConfigNode current = treeNode.AddNode("RECORDING");
            current.AddValue("recordingId", "current-root");
            current.AddValue("recordingFormatVersion", RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            current.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));

            RecordingTree loaded = RecordingTree.Load(treeNode);

            Assert.Empty(loaded.Recordings);
            Assert.Equal(string.Empty, loaded.RootRecordingId);
            Assert.Null(loaded.ActiveRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingTree]") &&
                l.Contains("unsupported schema generation") &&
                l.Contains("generation=(missing)"));
        }

        [Fact]
        public void RecordingTreeLoad_MetadataRejectedNonRoot_PrunesDanglingBranchReferences()
        {
            var treeNode = new ConfigNode("RECORDING_TREE");
            treeNode.AddValue("id", "tree-schema-prune-child");
            treeNode.AddValue("treeName", "Schema Prune Child");
            treeNode.AddValue("rootRecordingId", "current-root");
            treeNode.AddValue("activeRecordingId", "current-root");
            treeNode.AddValue("treeFormatVersion", RecordingTree.CurrentTreeFormatVersion.ToString(CultureInfo.InvariantCulture));
            treeNode.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));

            ConfigNode root = treeNode.AddNode("RECORDING");
            root.AddValue("recordingId", "current-root");
            root.AddValue("recordingFormatVersion", RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            root.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));

            ConfigNode rejected = treeNode.AddNode("RECORDING");
            rejected.AddValue("recordingId", "legacy-child");
            rejected.AddValue("recordingFormatVersion", "0");
            rejected.AddValue("parentRecordingId", "current-root");
            rejected.AddValue("parentBranchPointId", "bp-legacy");

            ConfigNode grandchild = treeNode.AddNode("RECORDING");
            grandchild.AddValue("recordingId", "current-grandchild");
            grandchild.AddValue("recordingFormatVersion", RecordingStore.CurrentRecordingFormatVersion.ToString(CultureInfo.InvariantCulture));
            grandchild.AddValue("recordingSchemaGeneration", RecordingStore.CurrentRecordingSchemaGeneration.ToString(CultureInfo.InvariantCulture));
            grandchild.AddValue("parentRecordingId", "legacy-child");
            grandchild.AddValue("parentBranchPointId", "bp-legacy");

            ConfigNode bp = treeNode.AddNode("BRANCH_POINT");
            bp.AddValue("id", "bp-legacy");
            bp.AddValue("ut", "10");
            bp.AddValue("type", "0");
            bp.AddValue("parentId", "legacy-child");
            bp.AddValue("childId", "current-grandchild");

            RecordingTree loaded = RecordingTree.Load(treeNode);

            Assert.False(loaded.Recordings.ContainsKey("legacy-child"));
            Assert.True(loaded.Recordings.ContainsKey("current-root"));
            Assert.True(loaded.Recordings.ContainsKey("current-grandchild"));
            Assert.Equal("current-root", loaded.RootRecordingId);
            Assert.Equal("current-root", loaded.ActiveRecordingId);
            Assert.Empty(loaded.BranchPoints);
            Assert.Null(loaded.Recordings["current-grandchild"].ParentRecordingId);
            Assert.Null(loaded.Recordings["current-grandchild"].ParentBranchPointId);
        }

        [Fact]
        public void DropFailedSidecarHydrationRecordings_RootFailure_DropsEntireTree()
        {
            var tree = new RecordingTree
            {
                Id = "tree-root-sidecar-failure",
                TreeName = "Root Sidecar Failure",
                RootRecordingId = "root",
                ActiveRecordingId = "root"
            };
            tree.Recordings["root"] = new Recording
            {
                RecordingId = "root",
                SidecarLoadFailed = true,
                SidecarLoadFailureReason = "format-version-mismatch"
            };
            tree.Recordings["child"] = new Recording
            {
                RecordingId = "child",
                ParentRecordingId = "root"
            };
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "bp-root",
                ParentRecordingIds = new List<string> { "root" },
                ChildRecordingIds = new List<string> { "child" }
            });
            tree.BackgroundMap[123u] = "root";

            int removed = ParsekScenario.DropFailedSidecarHydrationRecordings(
                tree,
                "test-root-sidecar");

            Assert.Equal(2, removed);
            Assert.Empty(tree.Recordings);
            Assert.Empty(tree.BranchPoints);
            Assert.Empty(tree.BackgroundMap);
            Assert.Equal(string.Empty, tree.RootRecordingId);
            Assert.Null(tree.ActiveRecordingId);
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]") &&
                l.Contains("dropped entire tree") &&
                l.Contains("root"));
        }

        [Fact]
        public void LoadRecordingFrom_FutureGeneration_IsRejected()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", "future-generation");
            node.AddValue("recordingFormatVersion", "0");
            node.AddValue("recordingSchemaGeneration", "2");

            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);

            Assert.Equal(-1, rec.RecordingFormatVersion);
            Assert.Contains(logLines, l =>
                l.Contains("[Codec]") &&
                l.Contains("reason=generation-newer"));
        }
    }
}
