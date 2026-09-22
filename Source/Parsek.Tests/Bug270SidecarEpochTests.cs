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

        // --- Epoch increment in the real SaveRecordingFiles path ---
        //
        // These drive RecordingSidecarStore's save body (through
        // RecordingStore.SaveRecordingFilesToPathsForTesting) and read the epoch the .prec
        // actually carries, so the incrementEpoch contract decides the verdict rather than
        // a SidecarEpoch++ written in the test.

        private static Recording MakeEpochRecording(string id)
        {
            return new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 0
            };
        }

        private static string NewTempDir()
        {
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "parsek-bug270-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteTempDir(string dir)
        {
            try { System.IO.Directory.Delete(dir, true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static int SaveAndProbeEpoch(Recording rec, string dir, bool incrementEpoch)
        {
            string prec = System.IO.Path.Combine(dir, rec.RecordingId + ".prec");
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, prec,
                System.IO.Path.Combine(dir, rec.RecordingId + "_vessel.craft"),
                System.IO.Path.Combine(dir, rec.RecordingId + "_ghost.craft"),
                incrementEpoch));
            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(prec, out probe));
            return probe.SidecarEpoch;
        }

        [Fact]
        public void SidecarEpoch_IncrementedOnSave_MatchesSfsAfterSaveSequence()
        {
            // Two OnSave writes (incrementEpoch=true): each advances the epoch before the
            // .prec is written, so the .sfs written afterwards carries the same number.
            string dir = NewTempDir();
            try
            {
                var rec = MakeEpochRecording("test-sequence");

                Assert.Equal(1, SaveAndProbeEpoch(rec, dir, incrementEpoch: true));
                Assert.Equal(1, rec.SidecarEpoch);
                Assert.Equal(2, SaveAndProbeEpoch(rec, dir, incrementEpoch: true));
                Assert.Equal(2, rec.SidecarEpoch);

                var sfsNode = new ConfigNode("RECORDING");
                RecordingTree.SaveRecordingInto(sfsNode, rec);
                var restored = new Recording();
                RecordingTree.LoadRecordingFrom(sfsNode, restored);

                Assert.Equal(2, restored.SidecarEpoch);
                Assert.False(RecordingStore.ShouldSkipStaleSidecar(restored, 2));
            }
            finally
            {
                DeleteTempDir(dir);
            }
        }

        // --- Bug #290: out-of-band writes must not drift epoch ---

        [Fact]
        public void OutOfBandWrite_PreservesEpoch_MatchesAfterQuickload()
        {
            // The .sfs of the last OnSave carries epoch 1; a later out-of-band write
            // (BgRecorder, scene-exit force-write: incrementEpoch=false) must rewrite the
            // .prec at the SAME epoch, or the quickload of that .sfs reads it as stale.
            string dir = NewTempDir();
            try
            {
                var rec = MakeEpochRecording("test-oob");
                rec.SidecarEpoch = 1;

                var sfsNode = new ConfigNode("RECORDING");
                RecordingTree.SaveRecordingInto(sfsNode, rec);

                int precEpochAfterOob = SaveAndProbeEpoch(rec, dir, incrementEpoch: false);
                Assert.Equal(1, precEpochAfterOob);
                Assert.Equal(1, rec.SidecarEpoch);

                var loaded = new Recording();
                RecordingTree.LoadRecordingFrom(sfsNode, loaded);
                Assert.Equal(1, loaded.SidecarEpoch);
                Assert.False(RecordingStore.ShouldSkipStaleSidecar(loaded, precEpochAfterOob));
            }
            finally
            {
                DeleteTempDir(dir);
            }
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
