using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class Bug270SidecarEpochTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public Bug270SidecarEpochTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
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

        // --- .sfs round-trip tests ---

        [Fact]
        public void SidecarEpoch_RoundTrips_ThroughSfsMetadata()
        {
            var rec = new Recording { SidecarEpoch = 3 };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(node, restored);

            Assert.Equal(3, restored.SidecarEpoch);
        }

        [Fact]
        public void SidecarEpoch_Zero_OmittedFromSfs()
        {
            var rec = new Recording { SidecarEpoch = 0 };
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);

            Assert.Null(node.GetValue("sidecarEpoch"));
        }

        [Fact]
        public void SidecarEpoch_MissingSfsField_DefaultsToZero()
        {
            var node = new ConfigNode("RECORDING");
            node.AddValue("vesselName", "OldSave");

            var rec = new Recording();
            RecordingTree.LoadRecordingFrom(node, rec);

            Assert.Equal(0, rec.SidecarEpoch);
        }

        // --- ShouldSkipStaleSidecar validation tests ---

        [Fact]
        public void ShouldSkipStaleSidecar_MatchingEpoch_ReturnsFalse()
        {
            var rec = new Recording
            {
                RecordingId = "test-match",
                SidecarEpoch = 2
            };

            bool skipped = RecordingStore.ShouldSkipStaleSidecar(rec, 2);

            Assert.False(skipped);
        }

        [Fact]
        public void ShouldSkipStaleSidecar_MismatchedEpoch_ReturnsTrue()
        {
            var rec = new Recording
            {
                RecordingId = "test-stale",
                SidecarEpoch = 1
            };

            bool skipped = RecordingStore.ShouldSkipStaleSidecar(rec, 3);

            Assert.True(skipped);
            Assert.Contains(logLines, l =>
                l.Contains("Sidecar epoch mismatch") &&
                l.Contains("test-stale") &&
                l.Contains("expects epoch 1") &&
                l.Contains("has epoch 3"));
        }

        [Fact]
        public void ShouldSkipStaleSidecar_ZeroSfsEpoch_SkipsValidation()
        {
            var rec = new Recording
            {
                RecordingId = "test-old-save",
                SidecarEpoch = 0
            };

            bool skipped = RecordingStore.ShouldSkipStaleSidecar(rec, 5);

            Assert.False(skipped);
        }

        [Fact]
        public void ShouldSkipStaleSidecar_ZeroPrecEpoch_WithNonZeroSfsEpoch_DetectsMismatch()
        {
            var rec = new Recording
            {
                RecordingId = "test-old-prec",
                SidecarEpoch = 2
            };

            bool skipped = RecordingStore.ShouldSkipStaleSidecar(rec, 0);

            Assert.True(skipped);
        }

        // --- Epoch increment in SaveRecordingFiles scenario ---

        [Fact]
        public void SidecarEpoch_IncrementedOnSave_MatchesSfsAfterSaveSequence()
        {
            // Simulate the save sequence: SaveRecordingFiles increments epoch,
            // then SaveRecordingInto writes the same epoch to .sfs
            var rec = new Recording
            {
                RecordingId = "test-sequence",
                SidecarEpoch = 0
            };

            // Simulate SaveRecordingFiles incrementing the epoch
            rec.SidecarEpoch++;
            Assert.Equal(1, rec.SidecarEpoch);

            // SaveRecordingInto would write this to .sfs
            var sfsNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(sfsNode, rec);

            // On load, .sfs epoch should match what was written to .prec
            var restored = new Recording();
            RecordingTree.LoadRecordingFrom(sfsNode, restored);
            Assert.Equal(1, restored.SidecarEpoch);

            // And the validation should pass
            bool skipped = RecordingStore.ShouldSkipStaleSidecar(restored, 1);
            Assert.False(skipped);
        }

        [Fact]
        public void SidecarEpoch_QuicksaveQuickload_DetectsStaleness()
        {
            // Full scenario: quicksave at T2 (epoch=1), autosave at T3 (epoch=2),
            // quickload T2 → .sfs has epoch=1, .prec has epoch=2
            var rec = new Recording
            {
                RecordingId = "test-quickload",
                SidecarEpoch = 0
            };

            // T2: quicksave → increment to 1
            rec.SidecarEpoch++;
            int t2Epoch = rec.SidecarEpoch;  // 1

            var t2SfsNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(t2SfsNode, rec);

            // T3: autosave → increment to 2
            rec.SidecarEpoch++;
            int t3Epoch = rec.SidecarEpoch;  // 2

            // Quickload T2: load .sfs from T2 quicksave
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(t2SfsNode, loaded);
            Assert.Equal(1, loaded.SidecarEpoch);

            // .prec on disk has T3 epoch
            bool skipped = RecordingStore.ShouldSkipStaleSidecar(loaded, t3Epoch);
            Assert.True(skipped);
            Assert.Contains(logLines, l =>
                l.Contains("Sidecar epoch mismatch") &&
                l.Contains("sidecar is stale"));
        }
    }
}
