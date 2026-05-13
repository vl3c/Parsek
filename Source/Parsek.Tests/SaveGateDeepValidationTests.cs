using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// P2 follow-up on PR #838 (recording/rendering v0 reset): the save-time
    /// gate <see cref="RecordingStore.AreRecordingFilesCurrentForSave"/> used
    /// header-only probes for trajectory and snapshot sidecars. A .prec
    /// truncated past the header, or a .craft with a valid header but a bad
    /// payload checksum, still passed certification, so OnSave serialized
    /// tree metadata referencing a corrupt sidecar and the next load marked
    /// the recording SidecarLoadFailed and dropped it.
    ///
    /// The gate now runs a full-payload validation pass: trajectory via a
    /// scratch read into a throwaway Recording, snapshots via TryLoad which
    /// runs CRC32 over the decompressed payload (SnapshotSidecarCodec line
    /// 180). Failure surfaces as <c>trajectory-payload-invalid</c> /
    /// <c>snapshot-{label}-payload-invalid</c>, forcing the caller in
    /// ParsekScenario.EnsureRecordingFilesCurrentForSave to rewrite from
    /// the in-memory rec instead of certifying a known-bad sidecar.
    /// </summary>
    [Collection("Sequential")]
    public class SaveGateDeepValidationTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly string tempDir;

        public SaveGateDeepValidationTests()
        {
            RecordingStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = false;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            tempDir = Path.Combine(
                Path.GetTempPath(),
                "parsek-savegate-deep-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            RecordingStore.SuppressLogging = true;
            RecordingStore.WriteReadableSidecarMirrorsOverrideForTesting = null;
            RecordingStore.ResetForTesting();

            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); }
                catch { }
            }
        }

        private Recording BuildRecording(string id)
        {
            var rec = new Recording
            {
                RecordingId = id,
                RecordingFormatVersion = RecordingStore.CurrentRecordingFormatVersion,
                RecordingSchemaGeneration = RecordingStore.CurrentRecordingSchemaGeneration,
                SidecarEpoch = 1,
            };
            var p0 = new TrajectoryPoint
            {
                ut = 1.0, latitude = 0.0, longitude = 0.0, altitude = 0.0,
                bodyName = "Kerbin",
            };
            var p1 = new TrajectoryPoint
            {
                ut = 2.0, latitude = 0.0, longitude = 0.0, altitude = 100.0,
                bodyName = "Kerbin",
            };
            rec.Points.Add(p0);
            rec.Points.Add(p1);
            rec.TrackSections.Add(new TrackSection
            {
                environment = SegmentEnvironment.Atmospheric,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = p0.ut,
                endUT = p1.ut,
                source = TrackSectionSource.Active,
                frames = new List<TrajectoryPoint> { p0, p1 },
                checkpoints = new List<OrbitSegment>(),
            });
            return rec;
        }

        private (string precPath, string vesselPath, string ghostPath) WriteFreshSidecars(Recording rec)
        {
            string precPath = Path.Combine(tempDir, rec.RecordingId + ".prec");
            string vesselPath = Path.Combine(tempDir, rec.RecordingId + "_vessel.craft");
            string ghostPath = Path.Combine(tempDir, rec.RecordingId + "_ghost.craft");
            bool wrote = RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false);
            Assert.True(wrote, "fixture save must succeed");
            return (precPath, vesselPath, ghostPath);
        }

        [Fact]
        public void Intact_Sidecars_Pass_DeepValidation()
        {
            var rec = BuildRecording("intact");
            var (precPath, vesselPath, ghostPath) = WriteFreshSidecars(rec);

            bool current = RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string reason);

            Assert.True(current,
                "intact sidecars must pass save-gate certification; reason=" + (reason ?? "<null>"));
            Assert.Null(reason);
        }

        // --- Trajectory payload validation ---

        // Helper: pick a truncation length that preserves the entire .prec
        // header (so TryProbeTrajectorySidecar still succeeds) but cuts the
        // payload. Header layout is documented at
        // TrajectorySidecarBinary.Write: 4-byte magic + 4-byte version +
        // 4-byte schemaGeneration + 4-byte epoch + length-prefixed string
        // recording-id (BinaryReader-style 7-bit-encoded length). Probing
        // the file first tells us how many bytes the header consumed; the
        // section-auth flag byte and string-table-count int land
        // immediately after, so truncating to (headerEnd + 2) leaves the
        // header intact but cuts into the flag/string-table region.
        private static int FindPostHeaderTruncationLength(string precPath)
        {
            using (var fs = new FileStream(precPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new System.IO.BinaryReader(fs, System.Text.Encoding.UTF8))
            {
                fs.Position += 4;                // magic
                reader.ReadInt32();              // version
                reader.ReadInt32();              // schemaGeneration
                reader.ReadInt32();              // epoch
                reader.ReadString();             // recording id
                return (int)fs.Position + 2;
            }
        }

        [Fact]
        public void TruncatedTrajectoryPayload_PastHeader_FailsSaveGate()
        {
            var rec = BuildRecording("trunc-id");
            var (precPath, vesselPath, ghostPath) = WriteFreshSidecars(rec);

            int truncateTo = FindPostHeaderTruncationLength(precPath);
            long origLen = new FileInfo(precPath).Length;
            Assert.True(origLen > truncateTo,
                "fixture must produce a .prec longer than the header so truncation removes payload");
            using (var fs = new FileStream(precPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(truncateTo);
            }

            // Sanity: the header probe by itself should still succeed
            // (truncation didn't remove the header). The bug exists
            // because the save gate stopped at this probe.
            TrajectorySidecarProbe probe;
            bool probeOk = RecordingStore.TryProbeTrajectorySidecar(precPath, out probe);
            Assert.True(probeOk && probe.Supported,
                "header probe must still pass on a header-only truncation; this is the bug surface");

            // Save gate must now reject the truncated payload.
            bool current = RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string reason);

            Assert.False(current, "truncated trajectory payload must fail save-gate certification");
            Assert.NotNull(reason);
            Assert.StartsWith("trajectory-payload-invalid", reason);
        }

        [Fact]
        public void TryValidatePayload_DirectCall_DetectsTruncation()
        {
            var rec = BuildRecording("trunc-dir");
            var (precPath, _, _) = WriteFreshSidecars(rec);

            int truncateTo = FindPostHeaderTruncationLength(precPath);
            using (var fs = new FileStream(precPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(truncateTo);
            }

            TrajectorySidecarProbe probe;
            Assert.True(RecordingStore.TryProbeTrajectorySidecar(precPath, out probe));
            Assert.True(probe.Supported);

            bool ok = TrajectorySidecarBinary.TryValidatePayload(precPath, probe, out string reason);
            Assert.False(ok);
            Assert.NotNull(reason);
        }

        // --- Snapshot payload validation ---

        [Fact]
        public void BitFlippedSnapshotPayload_FailsSaveGate()
        {
            var rec = BuildRecording("snap-bitflip");
            // Vessel snapshot present so the save gate will check the vessel .craft.
            rec.VesselSnapshot = new ConfigNode("VESSEL_SNAPSHOT");
            rec.VesselSnapshot.AddValue("dummy", "value");
            var (precPath, vesselPath, ghostPath) = WriteFreshSidecars(rec);

            // Flip a byte deep inside the compressed payload. The snapshot
            // codec header is: 4-byte magic + 4-byte version + 4-byte
            // schemaGeneration + 4-byte codec + 4-byte uncompressedLen +
            // 4-byte compressedLen + 4-byte checksum = 28 bytes. Skip well
            // past that into the compressed-payload body so the header
            // probe still succeeds but TryLoad's CRC32 check fails.
            byte[] bytes = File.ReadAllBytes(vesselPath);
            Assert.True(bytes.Length > 40, "fixture snapshot must have compressed payload past the header");
            int flipOffset = 40;
            bytes[flipOffset] ^= 0xFF;
            File.WriteAllBytes(vesselPath, bytes);

            // Sanity: probe still passes (header is intact).
            SnapshotSidecarProbe probe;
            bool probeOk = RecordingStore.TryProbeSnapshotSidecar(vesselPath, out probe);
            Assert.True(probeOk && probe.Supported,
                "header probe must still pass on a payload-only bit flip; this is the bug surface");

            // Save gate must reject the bit-flipped snapshot payload.
            bool current = RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string reason);

            Assert.False(current, "bit-flipped snapshot payload must fail save-gate certification");
            Assert.NotNull(reason);
            Assert.StartsWith("snapshot-vessel-payload-invalid", reason);
        }

        [Fact]
        public void MissingSnapshot_WhenNotExpected_StillPasses()
        {
            // Recording has no vessel/ghost snapshots in memory, so the gate
            // should not require .craft files to exist.
            var rec = BuildRecording("snap-not-expected");
            var (precPath, vesselPath, ghostPath) = WriteFreshSidecars(rec);
            // Delete the snapshot files; they weren't expected to exist.
            if (File.Exists(vesselPath)) File.Delete(vesselPath);
            if (File.Exists(ghostPath)) File.Delete(ghostPath);

            bool current = RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string reason);

            Assert.True(current,
                "missing snapshots are acceptable when the recording carries no in-memory snapshot; reason=" + (reason ?? "<null>"));
        }

        // --- End-to-end: corrupt payload triggers rewrite, rewrite repairs ---

        [Fact]
        public void Resave_AfterCorruption_RewritesAndRecertifies()
        {
            var rec = BuildRecording("rewrite-heals");
            var (precPath, vesselPath, ghostPath) = WriteFreshSidecars(rec);

            // Corrupt the .prec payload.
            using (var fs = new FileStream(precPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(32);
            }

            // Verify the gate now rejects.
            Assert.False(RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string preReason));
            Assert.StartsWith("trajectory-payload-invalid", preReason);

            // Resave from in-memory rec (mirrors EnsureRecordingFilesCurrentForSave's
            // rewrite branch in ParsekScenario).
            Assert.True(RecordingStore.SaveRecordingFilesToPathsForTesting(
                rec, precPath, vesselPath, ghostPath, incrementEpoch: false));

            // Gate must now certify the rewritten sidecars.
            Assert.True(RecordingStore.AreRecordingFilesCurrentAtPathsForTesting(
                rec, precPath, vesselPath, ghostPath, out string postReason),
                "rewritten sidecars must re-certify; reason=" + (postReason ?? "<null>"));
            Assert.Null(postReason);
        }
    }
}
