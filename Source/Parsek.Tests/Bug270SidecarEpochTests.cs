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

        // --- Bug #290: out-of-band writes must not drift epoch ---

        [Fact]
        public void OutOfBandWrite_PreservesEpoch_MatchesAfterQuickload()
        {
            // Simulate: OnSave (epoch 0->1), BgRecorder write (no increment),
            // quickload validates .sfs epoch 1 vs .prec epoch 1 → match
            var rec = new Recording
            {
                RecordingId = "test-oob",
                SidecarEpoch = 0
            };

            // OnSave: increment epoch (simulates SaveRecordingFiles default)
            rec.SidecarEpoch++;
            int onSaveEpoch = rec.SidecarEpoch; // 1

            // Write epoch to .sfs
            var sfsNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(sfsNode, rec);

            // BgRecorder out-of-band write: no increment (simulates incrementEpoch: false)
            // epoch stays at 1
            int precEpochAfterOob = rec.SidecarEpoch; // still 1

            // Quickload: restore from .sfs
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(sfsNode, loaded);
            Assert.Equal(1, loaded.SidecarEpoch);

            // Validate: .prec epoch matches .sfs epoch
            bool skipped = RecordingStore.ShouldSkipStaleSidecar(loaded, precEpochAfterOob);
            Assert.False(skipped);
        }

        [Fact]
        public void MultipleOnSaveIncrements_AllMatchAfterQuickload()
        {
            // Simulate: two OnSave cycles (epoch 0->1->2), then quickload
            // from the second save. .sfs and .prec both at epoch 2.
            var rec = new Recording
            {
                RecordingId = "test-multi-save",
                SidecarEpoch = 0
            };

            // First OnSave
            rec.SidecarEpoch++;
            Assert.Equal(1, rec.SidecarEpoch);

            // Second OnSave
            rec.SidecarEpoch++;
            Assert.Equal(2, rec.SidecarEpoch);

            // Write to .sfs at epoch 2
            var sfsNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(sfsNode, rec);

            // Quickload: restore from .sfs
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(sfsNode, loaded);
            Assert.Equal(2, loaded.SidecarEpoch);

            // Validate: .prec epoch 2 matches .sfs epoch 2
            bool skipped = RecordingStore.ShouldSkipStaleSidecar(loaded, 2);
            Assert.False(skipped);
        }

        [Fact]
        public void SceneExitForceWrite_AfterOnSave_DoesNotCauseMismatch()
        {
            // Simulate the scene-exit sequence:
            // 1. OnSave increments epoch (1), writes .sfs with epoch 1
            // 2. FinalizeTreeRecordings marks dirty
            // 3. Force-write with incrementEpoch:false writes .prec with epoch 1
            // 4. OnLoad reads .sfs epoch 1, validates against .prec epoch 1
            var rec = new Recording
            {
                RecordingId = "test-scene-exit",
                SidecarEpoch = 0
            };

            // Step 1: OnSave
            rec.SidecarEpoch++;
            var sfsNode = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(sfsNode, rec);

            // Steps 2-3: force-write without epoch increment
            // epoch stays at 1, .prec written with epoch 1
            int forceWriteEpoch = rec.SidecarEpoch; // 1

            // Step 4: OnLoad in next scene
            var loaded = new Recording();
            RecordingTree.LoadRecordingFrom(sfsNode, loaded);

            bool skipped = RecordingStore.ShouldSkipStaleSidecar(loaded, forceWriteEpoch);
            Assert.False(skipped);
        }

        // --- Original #270 staleness detection (must still work) ---

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
                l.Contains("sidecar is stale (bug #270)"));
        }
    }
}
