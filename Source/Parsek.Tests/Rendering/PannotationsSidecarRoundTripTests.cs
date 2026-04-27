using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Round-trip and version-gating tests for the .pann annotation sidecar
    /// (design doc §17.3.1, §20.1 Phase 1 unit-test row, HR-3 / HR-10 / HR-12).
    /// Touches disk + ParsekLog (via FileIOUtils inside Write); runs in the
    /// Sequential collection.
    /// </summary>
    [Collection("Sequential")]
    public class PannotationsSidecarRoundTripTests : IDisposable
    {
        private readonly string tempDir;

        public PannotationsSidecarRoundTripTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(), "parsek_pann_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private static SmoothingSpline MakeSpline(int knotCount, byte frameTag)
        {
            double[] knots = new double[knotCount];
            float[] cx = new float[knotCount];
            float[] cy = new float[knotCount];
            float[] cz = new float[knotCount];
            for (int i = 0; i < knotCount; i++)
            {
                knots[i] = 100.0 + i;
                cx[i] = 1.0f + i * 0.1f;
                cy[i] = 10.0f + i * 0.2f;
                cz[i] = 1000.0f + i * 50.0f;
            }
            return new SmoothingSpline
            {
                SplineType = 0,
                Tension = 0.5f,
                KnotsUT = knots,
                ControlsX = cx,
                ControlsY = cy,
                ControlsZ = cz,
                FrameTag = frameTag,
                IsValid = true,
            };
        }

        [Fact]
        public void Write_Read_SingleSpline_RoundTripsBitExact()
        {
            // What makes it fail: any silent mutation of coefficients on
            // save/load would feed the renderer a different curve from the
            // one fitted at commit, breaking determinism (HR-3).
            string path = Path.Combine(tempDir, "rec1.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var input = new List<KeyValuePair<int, SmoothingSpline>>
            {
                new KeyValuePair<int, SmoothingSpline>(0, MakeSpline(8, frameTag: 0)),
            };

            PannotationsSidecarBinary.Write(path, "rec1", sourceSidecarEpoch: 5,
                sourceRecordingFormatVersion: 7, configurationHash: hash, splines: input);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);

            Assert.True(PannotationsSidecarBinary.TryRead(path, probe, out var output, out string failure));
            Assert.Null(failure);
            Assert.Single(output);

            var inSpline = input[0].Value;
            var outSpline = output[0].Value;
            Assert.Equal(input[0].Key, output[0].Key);
            Assert.Equal(inSpline.SplineType, outSpline.SplineType);
            Assert.Equal(inSpline.Tension, outSpline.Tension);
            Assert.Equal(inSpline.FrameTag, outSpline.FrameTag);
            Assert.Equal(inSpline.KnotsUT, outSpline.KnotsUT);
            Assert.Equal(inSpline.ControlsX, outSpline.ControlsX);
            Assert.Equal(inSpline.ControlsY, outSpline.ControlsY);
            Assert.Equal(inSpline.ControlsZ, outSpline.ControlsZ);
        }

        [Fact]
        public void Write_Read_EmptyReservedBlocks_StableLayout()
        {
            // What makes it fail: writing zero splines must still produce a
            // file the reader accepts; otherwise "no annotations yet"
            // recordings cannot persist a marker pinning the cache key.
            string path = Path.Combine(tempDir, "rec_empty.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);

            PannotationsSidecarBinary.Write(path, "recE", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);
            Assert.Equal("recE", probe.RecordingId);

            Assert.True(PannotationsSidecarBinary.TryRead(path, probe, out var splines, out string failure));
            Assert.Null(failure);
            Assert.Empty(splines);
        }

        [Fact]
        public void Probe_FileMissing_ReturnsFalseAnnotationAbsent()
        {
            // What makes it fail: if probe didn't distinguish "absent" from
            // "corrupt", the orchestrator would skip the lazy-compute path
            // and silently render without annotations.
            string path = Path.Combine(tempDir, "does-not-exist.pann");
            Assert.False(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.False(probe.Success);
            Assert.Equal("file missing", probe.FailureReason);
        }

        [Fact]
        public void Probe_VersionMismatch_ReturnsSupportedFalse()
        {
            // What makes it fail: HR-10 — silently accepting a future
            // version's payload as the current version's would consume
            // unrelated bytes as splines and produce gibberish renderings.
            string path = Path.Combine(tempDir, "rec_v9999.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recX", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Overwrite the version int (offset 4..7) with 9999 little-endian.
            byte[] bytes = File.ReadAllBytes(path);
            bytes[4] = 0x0F; bytes[5] = 0x27; bytes[6] = 0; bytes[7] = 0;
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Success);
            Assert.False(probe.Supported);
            Assert.NotNull(probe.FailureReason);
            Assert.Contains("unsupported pannotations version", probe.FailureReason);
            Assert.Equal(9999, probe.BinaryVersion);
        }

        [Fact]
        public void Probe_AlgorithmStampMismatch_DiscardsFile()
        {
            // What makes it fail: if the probe didn't expose
            // AlgorithmStampVersion, the orchestrator (T5) would have no way
            // to detect a stamp drift and would feed stale annotations into
            // the new algorithm — HR-10 / HR-11 violation. The discard
            // logic itself lives in the orchestrator; the probe's
            // contract here is just to expose the field.
            string path = Path.Combine(tempDir, "rec_stamp.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recS", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.Equal(PannotationsSidecarBinary.AlgorithmStampVersion, probe.AlgorithmStampVersion);
        }

        [Fact]
        public void Probe_SourceSidecarEpochMismatch_FieldExposed()
        {
            // What makes it fail: HR-10 — without exposing
            // SourceSidecarEpoch, the orchestrator can't detect a .prec
            // rewrite (supersede commit, repair) under the same recording
            // id, and stale splines from the previous epoch would silently
            // be used.
            string path = Path.Combine(tempDir, "rec_epoch.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recEp", sourceSidecarEpoch: 42,
                sourceRecordingFormatVersion: 7, configurationHash: hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.Equal(42, probe.SourceSidecarEpoch);
        }

        [Fact]
        public void Probe_ConfigurationHashFieldExposed()
        {
            // What makes it fail: dropping the 32-byte configuration hash
            // from the probe would defeat the cache-key check entirely;
            // future cfg changes would then silently reuse stale splines.
            string path = Path.Combine(tempDir, "rec_cfgkey.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recH", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.NotNull(probe.ConfigurationHash);
            Assert.Equal(32, probe.ConfigurationHash.Length);
            Assert.Equal(hash, probe.ConfigurationHash);
        }

        [Fact]
        public void ComputeConfigurationHash_BitStableAcrossRuns()
        {
            // What makes it fail: HR-3 determinism — if the canonical
            // encoding included any unstable input (machine endian, hash
            // algorithm with random seeding, dictionary order), the cache
            // key would drift and every load would force recompute.
            byte[] first = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            for (int i = 0; i < 100; i++)
            {
                byte[] again = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
                Assert.Equal(first, again);
            }
        }

        [Fact]
        public void ComputeConfigurationHash_PerturbingTensionChangesHash()
        {
            // What makes it fail: HR-10 — a tunable that doesn't contribute
            // to the hash means changing it never invalidates caches; the
            // renderer would silently consume splines fitted with a
            // different tension.
            byte[] baseline = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var perturbed = SmoothingConfiguration.Default;
            perturbed.Tension = 0.6f;
            byte[] altered = PannotationsSidecarBinary.ComputeConfigurationHash(perturbed);
            Assert.NotEqual(baseline, altered);
        }

        [Fact]
        public void ComputeConfigurationHash_PerturbingSplineTypeChangesHash()
        {
            // What makes it fail: same as above — adding Hermite later (type
            // = 1) without changing the hash would let stale Catmull-Rom
            // splines be reused for Hermite-fit recordings.
            byte[] baseline = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var perturbed = SmoothingConfiguration.Default;
            perturbed.SplineType = 1;
            byte[] altered = PannotationsSidecarBinary.ComputeConfigurationHash(perturbed);
            Assert.NotEqual(baseline, altered);
        }

        [Fact]
        public void ComputeConfigurationHash_PerturbingMinSamplesChangesHash()
        {
            // What makes it fail: lowering MinSamplesPerSection later would
            // make new sections eligible that previously fell through to
            // legacy bracket; without hash sensitivity, those sections
            // would never be re-fit.
            byte[] baseline = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var perturbed = SmoothingConfiguration.Default;
            perturbed.MinSamplesPerSection = 5;
            byte[] altered = PannotationsSidecarBinary.ComputeConfigurationHash(perturbed);
            Assert.NotEqual(baseline, altered);
        }

        [Fact]
        public void Write_UsesAtomicTmpRename()
        {
            // What makes it fail: HR-12 — non-atomic writes leave a half-
            // written file on a crash mid-flush, and a future probe would
            // accept the corrupt header. Atomic tmp+rename guarantees the
            // destination either holds the previous good file or the new
            // good file, never a partial one.
            string path = Path.Combine(tempDir, "rec_atomic.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recA", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"),
                "FileIOUtils.SafeWriteBytes must clean up the .tmp after rename.");
        }

        [Fact]
        public void Write_OverwritesExistingFile()
        {
            // What makes it fail: a non-overwriting write would orphan the
            // previous .pann after a recompute, and the next load would
            // pick up the stale file (HR-10 violation).
            string path = Path.Combine(tempDir, "rec_overwrite.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            var first = new List<KeyValuePair<int, SmoothingSpline>>
            {
                new KeyValuePair<int, SmoothingSpline>(0, MakeSpline(4, frameTag: 0)),
            };
            PannotationsSidecarBinary.Write(path, "rec1", 1, 7, hash, first);

            var second = new List<KeyValuePair<int, SmoothingSpline>>
            {
                new KeyValuePair<int, SmoothingSpline>(0, MakeSpline(4, frameTag: 0)),
                new KeyValuePair<int, SmoothingSpline>(1, MakeSpline(6, frameTag: 0)),
            };
            PannotationsSidecarBinary.Write(path, "rec1", 2, 7, hash, second);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.Equal(2, probe.SourceSidecarEpoch);
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe, out var splines, out _));
            Assert.Equal(2, splines.Count);
            Assert.Equal(0, splines[0].Key);
            Assert.Equal(1, splines[1].Key);
            Assert.Equal(6, splines[1].Value.KnotsUT.Length);
        }
    }
}
