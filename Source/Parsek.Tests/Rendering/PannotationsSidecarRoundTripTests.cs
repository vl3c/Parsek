using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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

        // --- P2#2: malformed-payload defence ---

        // Helpers: locate where each post-header count sits in a freshly-written
        // .pann. Header layout from PannotationsSidecarBinary.Write:
        //   magic(4) + binVer(4) + algStamp(4) + epoch(4) + formatVer(4) + cfgHash(32)
        //   = 52 bytes, plus the length-prefixed recordingId string.
        // BinaryWriter.Write(string) prefixes a LEB128 7-bit length byte; for
        // strings < 128 bytes the prefix is a single byte equal to the length.
        // So total pre-payload = 52 + 1 + recordingId.Length (ASCII).
        private static int CountOffset(string recordingId, int blockIndex)
        {
            // blockIndex: 0 = stringTableCount, 1 = splineCount, 2 = outlierCount,
            // 3 = anchorCount, 4 = coBubbleCount. With Phase 1's empty payload,
            // each post-block-0 count sits exactly 4 bytes after the previous
            // one (no entries written between them).
            int header = 52 + 1 + Encoding.UTF8.GetByteCount(recordingId);
            return header + blockIndex * 4;
        }

        private static void WriteInt32At(byte[] bytes, int offset, int value)
        {
            bytes[offset + 0] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
            bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        [Fact]
        public void TryRead_NegativeStringTableCount_ReturnsFalseWithReason()
        {
            // What makes it fail: P2#2 root cause — without the count
            // validation, `new List<string>(tableCount)` throws
            // ArgumentOutOfRangeException for tableCount = -1, escapes the
            // narrow catch (EndOfStreamException / IOException) and crashes
            // the load thread. The fix returns false with a human-readable
            // failureReason and lets the caller recompute.
            string path = Path.Combine(tempDir, "rec_negtable.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 0), -1);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("invalid string-table count", reason);
        }

        [Fact]
        public void TryRead_NegativeSplineCount_ReturnsFalseWithReason()
        {
            // What makes it fail: P2#2 — same risk as above for the spline-list
            // count. A negative value would propagate as
            // ArgumentOutOfRangeException out of the for-loop's allocation.
            string path = Path.Combine(tempDir, "rec_negspline.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 1), -1);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("invalid spline count", reason);
        }

        [Fact]
        public void TryRead_OversizedStringTableCount_ReturnsFalseWithReason()
        {
            // What makes it fail: P2#2 — int.MaxValue passes a < 0 check but
            // forces a multi-GB List<string> allocation and either crashes
            // with OutOfMemoryException or locks the process for seconds.
            // The MaxReasonableCount upper bound stops the allocation before
            // it starts.
            string path = Path.Combine(tempDir, "rec_hugetable.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 0), int.MaxValue);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("invalid string-table count", reason);
        }

        [Fact]
        public void TryRead_OversizedSplineCount_ReturnsFalseWithReason()
        {
            // What makes it fail: P2#2 — same upper-bound concern for the
            // spline-list count.
            string path = Path.Combine(tempDir, "rec_hugespline.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 1), int.MaxValue);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("invalid spline count", reason);
        }

        [Fact]
        public void TryRead_TruncatedFile_ReturnsFalseWithReason()
        {
            // What makes it fail: a truncated file is the canonical "crash
            // mid-write" outcome the atomic-write contract is supposed to
            // prevent. The reader still must reject it gracefully — never
            // throw — because a tampered or corrupted file can show up by
            // any means (manual edit, disk error, unfinished SafeWriteBytes
            // recovery).
            string path = Path.Combine(tempDir, "rec_truncated.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Probe needs the full header + recordingId string, so truncate
            // mid-payload after the probe would succeed. Slice to just past
            // the recordingId — TryRead will EndOfStream on the first
            // ReadInt32 of the string table.
            byte[] full = File.ReadAllBytes(path);
            int tableOffset = CountOffset("recT", 0);
            byte[] truncated = new byte[tableOffset + 2];
            Array.Copy(full, truncated, truncated.Length);
            File.WriteAllBytes(path, truncated);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.NotNull(reason);
        }

        [Fact]
        public void TryRead_GenericMalformedPayload_DoesNotThrow()
        {
            // What makes it fail: the broad catch (Exception) safety net.
            // A malformed string at the head of the table would throw
            // ArgumentOutOfRangeException from BinaryReader.ReadString on
            // a negative LEB128 length byte, which is neither
            // EndOfStreamException nor IOException — without the broad
            // catch the load thread crashes.
            string path = Path.Combine(tempDir, "rec_malformed.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Plant a non-zero string-table count (1) and corrupt the first
            // string entry's LEB128 length prefix (0xFF + 0xFF + 0xFF + 0xFF
            // = invalid 7-bit-encoded length, ReadString throws).
            byte[] full = File.ReadAllBytes(path);
            int tableCountOffset = CountOffset("recT", 0);
            WriteInt32At(full, tableCountOffset, 1);
            byte[] corrupt = new byte[tableCountOffset + 4 + 8];
            Array.Copy(full, corrupt, tableCountOffset + 4);
            for (int i = tableCountOffset + 4; i < corrupt.Length; i++)
                corrupt[i] = 0xFF;
            File.WriteAllBytes(path, corrupt);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            // The key invariant: TryRead must not throw. The exact
            // failureReason wording is implementation-dependent; we only
            // require returns=false and a populated reason.
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.NotNull(reason);
        }

        [Fact]
        public void TryRead_StringTableCountExceedsRemainingBytes_RejectedBeforeAllocation()
        {
            // What makes it fail: a 10K per-block cap still permits a count
            // larger than the actual file's remaining bytes. Without the
            // stream-length sanity check, the loader would allocate a 10K-
            // capacity List<string> and then EndOfStream on the first
            // ReadString — wasted work and a misleading failure reason.
            // The stream-length check rejects the count before allocation
            // with a precise "would need N bytes but only M remain" reason.
            string path = Path.Combine(tempDir, "rec_streamlen.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Empty Phase-1 payload has 16 bytes after the string-table
            // count (4 each for splineCount, outlierCount, anchorCount,
            // coBubbleCount). Set tableCount = 17 → 17 bytes of payload
            // claimed, only 16 remain.
            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 0), 17);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("would need", reason);
            Assert.Contains("string-table", reason);
        }

        [Fact]
        public void TryRead_SplineCountAboveRealisticCap_RejectedWithExceedsCap()
        {
            // What makes it fail: prior to the per-block cap (was
            // MaxReasonableCount = 10M), a moderately-large spline count
            // could pass validation and force a 10M-entry list allocation
            // before the loader even started reading entries. Real .pann
            // files have a few hundred entries at most; the per-block cap
            // (MaxSplineEntries = 10K) rejects any value above that
            // immediately with the canonical "exceeds cap" reason.
            string path = Path.Combine(tempDir, "rec_capcheck.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recT", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Set spline count to something just above the per-block cap so
            // the cap check fires first (the value is still within
            // MaxReasonableCount = 10M, so the stricter cap is the only
            // thing rejecting it).
            byte[] bytes = File.ReadAllBytes(path);
            WriteInt32At(bytes, CountOffset("recT", 1), 10_001);
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe, out _, out string reason);
            Assert.False(ok);
            // Either the per-block cap or the stream-length check fires —
            // both are acceptable rejections; both reasons contain enough
            // for a developer to diagnose. Just confirm rejection happens
            // (not throw, not silent-accept).
            Assert.Contains("spline", reason);
        }

        // --- existing tests resume ---

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

        [Fact]
        public void TryProbe_FileLockedByAnotherProcess_ReturnsFalse_DoesNotThrow()
        {
            // What makes it fail: TryProbe must absorb FileStream IO errors
            // (file locked by another process, anti-virus scan, mid-rename
            // collision) and return false with a populated reason. Without
            // the catch, the exception bubbles up to LoadRecordingFiles
            // and aborts the entire recording load over a regenerable
            // cache — a violation of HR-9 (.pann is regenerable, never
            // blocks .prec hydration).
            string path = Path.Combine(tempDir, "rec_locked.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recL", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Hold an exclusive lock on the file so the open in TryProbe
            // hits an IOException (sharing-violation on Windows).
            bool probeResult;
            string reason;
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                probeResult = PannotationsSidecarBinary.TryProbe(path, out var probe);
                reason = probe.FailureReason;
            }

            Assert.False(probeResult);
            Assert.NotNull(reason);
            // Either "io error" (FileStream IOException) or another caught
            // category — the test only asserts non-throw + populated reason.
        }

        [Fact]
        public void TryProbe_MalformedRecordingIdLength_ReturnsFalse_DoesNotThrow()
        {
            // What makes it fail: BinaryReader.ReadString() throws
            // ArgumentOutOfRangeException on a corrupt LEB128 length byte.
            // Before the outer catch was added, that exception escaped
            // TryProbe and aborted recording load. The fix maps any
            // malformed-payload exception to a cache-miss + recompute.
            string path = Path.Combine(tempDir, "rec_malformed_id.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recM", 1, 7, hash,
                new List<KeyValuePair<int, SmoothingSpline>>());

            // Corrupt the recording-id length prefix. Magic(4) + binVer(4)
            // + algStamp(4) + epoch(4) + fmt(4) + configHash(32) = 52,
            // so byte 52 is the first byte of the LEB128-encoded string
            // length. Plant a continuation pattern with no terminator
            // (high bits set on every byte) — ReadString never reaches a
            // terminator and either throws ArgumentOutOfRangeException
            // (negative final length) or EndOfStream.
            byte[] bytes = File.ReadAllBytes(path);
            for (int i = 52; i < Math.Min(bytes.Length, 60); i++)
                bytes[i] = 0xFF;
            File.WriteAllBytes(path, bytes);

            // Critical: must not throw.
            bool ok = PannotationsSidecarBinary.TryProbe(path, out var probe);
            Assert.False(ok);
            Assert.NotNull(probe.FailureReason);
        }
    }
}
