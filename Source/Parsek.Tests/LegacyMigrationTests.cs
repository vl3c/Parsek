using System;
using System.Collections.Generic;
using System.Globalization;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Regression tests for the Rewind-to-Staging Phase 1 legacy migrations
    /// (design doc section 9).
    ///
    /// Guards against:
    ///  - Loading a pre-feature RECORDING ConfigNode that never had a
    ///    <c>mergeState</c> key must succeed without Warn and default to
    ///    <see cref="MergeState.Immutable"/>.
    ///  - A legacy <c>committed = True/False</c> bool (never shipped in a
    ///    release but described in design section 9) must map to
    ///    Immutable / NotCommitted respectively.
    ///  - Idempotent: loading the same node twice promotes once; the
    ///    one-shot <c>[Recording] Legacy migration:</c> Info line is only
    ///    emitted once.
    ///  - A stray non-empty <c>supersedeTargetId</c> on an Immutable
    ///    recording logs Warn and is treated as cleared.
    /// </summary>
    [Collection("Sequential")]
    public class LegacyMigrationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public LegacyMigrationTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
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
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
        }

        private static ConfigNode MakeRecordingNode(string recId)
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("recordingId", recId);
            node.AddValue("vesselName", "TestVessel");
            node.AddValue("vesselPersistentId", "0");
            node.AddValue("recordingFormatVersion", "4");
            node.AddValue("loopPlayback", "False");
            node.AddValue("loopIntervalSeconds", (10.0).ToString("R", CultureInfo.InvariantCulture));
            node.AddValue("lastResIdx", "-1");
            node.AddValue("pointCount", "0");
            return node;
        }

        [Fact]
        public void LoadRecording_NoMergeStateNoCommittedBool_DefaultsImmutable_NoWarn()
        {
            var node = MakeRecordingNode("rec_legacy_no_field");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.Immutable, rec.MergeState);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Recording]") && l.Contains("Legacy migration"));
        }

        [Fact]
        public void LoadRecording_LegacyCommittedTrue_MapsToImmutable()
        {
            var node = MakeRecordingNode("rec_legacy_committed_true");
            node.AddValue("committed", "True");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.Equal(1, RecordingStore.LegacyMergeStateMigrationCount);
        }

        [Fact]
        public void LoadRecording_LegacyCommittedFalse_MapsToNotCommitted()
        {
            var node = MakeRecordingNode("rec_legacy_committed_false");
            node.AddValue("committed", "False");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.NotCommitted, rec.MergeState);
            Assert.Equal(1, RecordingStore.LegacyMergeStateMigrationCount);
        }

        [Fact]
        public void LoadRecording_ExplicitMergeStateBeatsLegacyCommittedBool()
        {
            // A defensive test: if both the new `mergeState` and the legacy `committed`
            // field appear on the same node (should never happen in practice), the new
            // explicit field wins and no migration counter bumps.
            var node = MakeRecordingNode("rec_both_fields");
            node.AddValue("mergeState", MergeState.CommittedProvisional.ToString());
            node.AddValue("committed", "True");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.CommittedProvisional, rec.MergeState);
            Assert.Equal(0, RecordingStore.LegacyMergeStateMigrationCount);
        }

        [Fact]
        public void LegacyMigration_OneShotLogEmitted_OnFirstTreeLoad_NotOnSecond()
        {
            // Build two single-recording trees so EmitLegacyMergeStateMigrationLogOnce
            // fires exactly once across repeated tree loads within the same session.
            var treeNode1 = new ConfigNode("RECORDING_TREE");
            treeNode1.AddValue("id", "tree-a");
            treeNode1.AddValue("rootRecordingId", "rec_legacy_a");
            var recNode1 = treeNode1.AddNode("RECORDING");
            recNode1.AddValue("recordingId", "rec_legacy_a");
            recNode1.AddValue("vesselName", "A");
            recNode1.AddValue("vesselPersistentId", "0");
            recNode1.AddValue("recordingFormatVersion", "4");
            recNode1.AddValue("loopPlayback", "False");
            recNode1.AddValue("loopIntervalSeconds", (10.0).ToString("R", CultureInfo.InvariantCulture));
            recNode1.AddValue("lastResIdx", "-1");
            recNode1.AddValue("pointCount", "0");
            recNode1.AddValue("committed", "True");

            RecordingTree.Load(treeNode1);
            int firstCount = logLines.Count;
            bool firstSeen = logLines.Exists(l =>
                l.Contains("[Recording]") && l.Contains("Legacy migration") &&
                l.Contains("1 recordings"));
            Assert.True(firstSeen,
                "First tree load should emit one-shot [Recording] Legacy migration Info line");

            logLines.Clear();

            // Second tree in the same session: migration counter already 1 but log is
            // already emitted — no new line.
            var treeNode2 = new ConfigNode("RECORDING_TREE");
            treeNode2.AddValue("id", "tree-b");
            treeNode2.AddValue("rootRecordingId", "rec_legacy_b");
            var recNode2 = treeNode2.AddNode("RECORDING");
            recNode2.AddValue("recordingId", "rec_legacy_b");
            recNode2.AddValue("vesselName", "B");
            recNode2.AddValue("vesselPersistentId", "0");
            recNode2.AddValue("recordingFormatVersion", "4");
            recNode2.AddValue("loopPlayback", "False");
            recNode2.AddValue("loopIntervalSeconds", (10.0).ToString("R", CultureInfo.InvariantCulture));
            recNode2.AddValue("lastResIdx", "-1");
            recNode2.AddValue("pointCount", "0");
            recNode2.AddValue("committed", "False");

            RecordingTree.Load(treeNode2);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[Recording]") && l.Contains("Legacy migration"));
        }

        [Fact]
        public void LoadRecording_StraySupersedeTargetIdOnImmutable_WarnAndCleared()
        {
            // Design section 5.5 legacy-write safety: SupersedeTargetId is transient.
            // Any recording serialized as Immutable/CommittedProvisional that still
            // carries a non-empty target must be logged Warn and cleared on load.
            var node = MakeRecordingNode("rec_stray");
            // Default MergeState is Immutable (no `mergeState` key).
            node.AddValue("supersedeTargetId", "rec_victim");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.Immutable, rec.MergeState);
            Assert.Null(rec.SupersedeTargetId);
            Assert.Contains(logLines, l =>
                l.Contains("[Recording]") && l.Contains("Stray SupersedeTargetId") &&
                l.Contains("rec_stray"));
        }

        [Fact]
        public void LoadRecording_SupersedeTargetIdOnNotCommitted_Preserved()
        {
            var node = MakeRecordingNode("rec_prov");
            node.AddValue("mergeState", MergeState.NotCommitted.ToString());
            node.AddValue("supersedeTargetId", "rec_victim");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(MergeState.NotCommitted, rec.MergeState);
            Assert.Equal("rec_victim", rec.SupersedeTargetId);
        }
    }
}
