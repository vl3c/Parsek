using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Parsek.Rendering;

namespace Parsek
{
    /// <summary>
    /// Probe-result struct for the <c>.pann</c> annotation sidecar (design
    /// doc §17.3.1). Mirrors <see cref="TrajectorySidecarProbe"/> in shape so
    /// callers can use the same handshake (probe -> read).
    /// </summary>
    internal struct PannotationsSidecarProbe
    {
        public bool Success;
        public bool Supported;
        public int BinaryVersion;
        public int AlgorithmStampVersion;
        public int SourceSidecarEpoch;
        public int SourceRecordingFormatVersion;
        public byte[] ConfigurationHash; // 32 bytes
        public string RecordingId;
        public string FailureReason;
    }

    /// <summary>
    /// Tunable configuration that affects derived <c>.pann</c> output. The
    /// SHA-256 of its canonical encoding is the cache key (design doc
    /// §17.3.1, "Configuration Cache Key"). Phase 1 only populates the
    /// smoothing-related fields; later phases will append outlier and
    /// co-bubble tunables to the canonical encoding without reordering.
    /// </summary>
    internal struct SmoothingConfiguration
    {
        public byte SplineType;            // 0 = Catmull-Rom (Phase 1)
        public float Tension;              // 0.5 = canonical Catmull-Rom
        public int MinSamplesPerSection;   // gating threshold for Fit
        public int MaxKnotCount;           // 0 = unlimited (Phase 1 default)

        internal static SmoothingConfiguration Default => new SmoothingConfiguration
        {
            SplineType = 0,
            Tension = 0.5f,
            MinSamplesPerSection = 4,
            MaxKnotCount = 0,
        };
    }

    /// <summary>
    /// Reader / writer for the optional pipeline-annotation sidecar
    /// <c>&lt;id&gt;.pann</c> (design doc §17.3.1). Mirrors the
    /// probe/read/write shape of <see cref="TrajectorySidecarBinary"/>; the
    /// file is regenerable, so a probe failure or version mismatch causes
    /// the caller to discard and recompute (HR-10).
    /// </summary>
    internal static class PannotationsSidecarBinary
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PANN");
        private static readonly byte[] CanonicalMagic = Encoding.ASCII.GetBytes("PANC");

        internal const int PannotationsBinaryVersion = 1;
        // Bumped to 2 in Phase 4: ExoPropulsive / ExoBallistic splines are now
        // fitted in inertial-longitude space (FrameTag = 1) instead of body-
        // fixed. The .pann binary schema is unchanged — frameTag was already
        // serialized — but the algorithm output for the same input differs,
        // so HR-10 invalidates v1 .pann files via alg-stamp-drift.
        internal const int AlgorithmStampVersion = 2;
        private const int CanonicalEncoderVersion = 1;

        // Configuration-hash canonical encoding length: PANC(4) + encVer(4) +
        // splineType(1) + tension(4) + minSamples(4) + maxKnots(4) +
        // outlierAccelAtm(4) + outlierAccelExo(4) + anchorPriority(10) +
        // coBubbleBlendMaxWindow(8) + coBubbleResampleHz(4) = 51 bytes.
        private const int CanonicalEncodingLength = 51;

        /// <summary>
        /// Probes the file header. Returns <c>true</c> with
        /// <see cref="PannotationsSidecarProbe.Success"/> = true when the
        /// magic + version + recording-id can be read; <see cref="PannotationsSidecarProbe.Supported"/>
        /// indicates whether the binary version is one this build understands.
        /// </summary>
        internal static bool TryProbe(string path, out PannotationsSidecarProbe probe)
        {
            probe = default(PannotationsSidecarProbe);

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                probe.FailureReason = "file missing";
                return false;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (stream.Length < Magic.Length + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(int) + 32)
                {
                    probe.FailureReason = "binary header truncated";
                    return false;
                }

                for (int i = 0; i < Magic.Length; i++)
                {
                    if (reader.ReadByte() != Magic[i])
                    {
                        probe.FailureReason = "binary magic mismatch";
                        return false;
                    }
                }

                int binaryVersion = reader.ReadInt32();
                int algorithmStampVersion = reader.ReadInt32();
                int sourceSidecarEpoch = reader.ReadInt32();
                int sourceRecordingFormatVersion = reader.ReadInt32();
                byte[] configurationHash = reader.ReadBytes(32);
                if (configurationHash == null || configurationHash.Length != 32)
                {
                    probe.FailureReason = "binary header truncated";
                    return false;
                }
                string recordingId;
                try
                {
                    recordingId = reader.ReadString();
                }
                catch (EndOfStreamException)
                {
                    probe.FailureReason = "binary header truncated";
                    return false;
                }

                probe.Success = true;
                probe.BinaryVersion = binaryVersion;
                probe.AlgorithmStampVersion = algorithmStampVersion;
                probe.SourceSidecarEpoch = sourceSidecarEpoch;
                probe.SourceRecordingFormatVersion = sourceRecordingFormatVersion;
                probe.ConfigurationHash = configurationHash;
                probe.RecordingId = recordingId;
                probe.Supported = IsSupportedBinaryVersion(binaryVersion);
                probe.FailureReason = probe.Supported
                    ? null
                    : $"unsupported pannotations version {binaryVersion}";
                return true;
            }
        }

        /// <summary>
        /// Reads the smoothing-spline list out of the file. Caller must have
        /// already probed and confirmed support; throws
        /// <see cref="InvalidOperationException"/> otherwise (mirrors
        /// <see cref="TrajectorySidecarBinary.Read"/>'s contract). Returns
        /// <c>false</c> on mid-stream corruption with a populated
        /// <paramref name="failureReason"/>; the caller treats that as a
        /// discard-and-recompute trigger.
        /// </summary>
        internal static bool TryRead(
            string path,
            PannotationsSidecarProbe probe,
            out List<KeyValuePair<int, SmoothingSpline>> splines,
            out string failureReason)
        {
            if (!probe.Success || !probe.Supported)
                throw new InvalidOperationException("Pannotations sidecar probe must succeed before read.");

            splines = new List<KeyValuePair<int, SmoothingSpline>>();
            failureReason = null;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    SkipHeader(reader);

                    // String table — unused in Phase 1 but reserved for parity
                    // with .prec layout so future blocks can reference it.
                    int tableCount = reader.ReadInt32();
                    var stringTable = new List<string>(tableCount);
                    for (int i = 0; i < tableCount; i++)
                        stringTable.Add(reader.ReadString());

                    // SmoothingSplineList
                    int splineCount = reader.ReadInt32();
                    for (int i = 0; i < splineCount; i++)
                    {
                        int sectionIndex = reader.ReadInt32();
                        byte splineType = reader.ReadByte();
                        float tension = reader.ReadSingle();
                        int knotCount = reader.ReadInt32();
                        if (knotCount < 0)
                        {
                            failureReason = $"negative knotCount at smoothing-spline entry {i}";
                            return false;
                        }

                        double[] knots = new double[knotCount];
                        for (int k = 0; k < knotCount; k++) knots[k] = reader.ReadDouble();
                        float[] cx = new float[knotCount];
                        for (int k = 0; k < knotCount; k++) cx[k] = reader.ReadSingle();
                        float[] cy = new float[knotCount];
                        for (int k = 0; k < knotCount; k++) cy[k] = reader.ReadSingle();
                        float[] cz = new float[knotCount];
                        for (int k = 0; k < knotCount; k++) cz[k] = reader.ReadSingle();
                        byte frameTag = reader.ReadByte();

                        var spline = new SmoothingSpline
                        {
                            SplineType = splineType,
                            Tension = tension,
                            KnotsUT = knots,
                            ControlsX = cx,
                            ControlsY = cy,
                            ControlsZ = cz,
                            FrameTag = frameTag,
                            IsValid = knotCount > 0,
                        };
                        splines.Add(new KeyValuePair<int, SmoothingSpline>(sectionIndex, spline));
                    }

                    // Reserved blocks — Phase 1 writes count = 0 for each;
                    // skip silently for forward-tolerance with future blocks.
                    int outlierCount = reader.ReadInt32();
                    if (outlierCount != 0)
                    {
                        failureReason = $"unexpected outlier-flags count {outlierCount} for binary version {probe.BinaryVersion}";
                        return false;
                    }
                    int anchorCount = reader.ReadInt32();
                    if (anchorCount != 0)
                    {
                        failureReason = $"unexpected anchor-candidate count {anchorCount} for binary version {probe.BinaryVersion}";
                        return false;
                    }
                    int coBubbleCount = reader.ReadInt32();
                    if (coBubbleCount != 0)
                    {
                        failureReason = $"unexpected co-bubble-trace count {coBubbleCount} for binary version {probe.BinaryVersion}";
                        return false;
                    }
                }
            }
            catch (EndOfStreamException ex)
            {
                failureReason = $"truncated pannotations payload: {ex.Message}";
                return false;
            }
            catch (IOException ex)
            {
                failureReason = $"io error reading pannotations: {ex.Message}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Writes a <c>.pann</c> sidecar atomically (tmp + rename via
        /// <see cref="FileIOUtils.SafeWriteBytes"/>). The header pins all
        /// cache-key fields; mid-stream blocks are written in the order
        /// declared in design doc §17.3.1 with reserved-zero counts for
        /// blocks Phase 1 does not populate.
        /// </summary>
        internal static void Write(
            string path,
            string recordingId,
            int sourceSidecarEpoch,
            int sourceRecordingFormatVersion,
            byte[] configurationHash,
            IList<KeyValuePair<int, SmoothingSpline>> splines)
        {
            if (configurationHash == null || configurationHash.Length != 32)
                throw new ArgumentException("configurationHash must be a 32-byte SHA-256 digest.", nameof(configurationHash));

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(PannotationsBinaryVersion);
                writer.Write(AlgorithmStampVersion);
                writer.Write(sourceSidecarEpoch);
                writer.Write(sourceRecordingFormatVersion);
                writer.Write(configurationHash);
                writer.Write(recordingId ?? string.Empty);

                // Empty string table for Phase 1 — kept for layout parity with
                // .prec so later blocks can index into it.
                writer.Write(0);

                int splineCount = splines?.Count ?? 0;
                writer.Write(splineCount);
                if (splines != null)
                {
                    for (int i = 0; i < splines.Count; i++)
                    {
                        int sectionIndex = splines[i].Key;
                        SmoothingSpline s = splines[i].Value;
                        int knotCount = s.KnotsUT?.Length ?? 0;

                        writer.Write(sectionIndex);
                        writer.Write(s.SplineType);
                        writer.Write(s.Tension);
                        writer.Write(knotCount);
                        for (int k = 0; k < knotCount; k++) writer.Write(s.KnotsUT[k]);
                        for (int k = 0; k < knotCount; k++) writer.Write(s.ControlsX[k]);
                        for (int k = 0; k < knotCount; k++) writer.Write(s.ControlsY[k]);
                        for (int k = 0; k < knotCount; k++) writer.Write(s.ControlsZ[k]);
                        writer.Write(s.FrameTag);
                    }
                }

                // Reserved blocks: Phase 1 emits count = 0 for each. Layout is
                // append-only (HR-11) so future phases bolt new blocks on
                // without bumping PannotationsBinaryVersion.
                writer.Write(0); // OutlierFlagsList
                writer.Write(0); // AnchorCandidatesList
                writer.Write(0); // CoBubbleOffsetTraces

                writer.Flush();
                FileIOUtils.SafeWriteBytes(stream.ToArray(), path, "Pipeline-Sidecar");
            }
        }

        /// <summary>
        /// Computes the SHA-256 cache key over a fully-pinned canonical
        /// encoding of <paramref name="cfg"/> (design doc §17.3.1
        /// "Configuration Cache Key"). The encoding is endian-fixed and
        /// field-order-fixed: future phases append fields, never reorder, so
        /// existing on-disk hashes remain comparable for the fields they
        /// covered. Phase 1 fills the smoothing-related fields and pads the
        /// reserved bytes with zero.
        /// </summary>
        internal static byte[] ComputeConfigurationHash(SmoothingConfiguration cfg)
        {
            byte[] buffer = new byte[CanonicalEncodingLength];
            using (var ms = new MemoryStream(buffer, writable: true))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CanonicalMagic);                   // [0..3]
                w.Write(CanonicalEncoderVersion);          // [4..7]
                w.Write(cfg.SplineType);                   // [8]
                w.Write(cfg.Tension);                      // [9..12]
                w.Write(cfg.MinSamplesPerSection);         // [13..16]
                w.Write(cfg.MaxKnotCount);                 // [17..20]
                w.Write((float)0);                         // [21..24] outlierAccelAtmospheric (reserved)
                w.Write((float)0);                         // [25..28] outlierAccelExo (reserved)
                for (int i = 0; i < 10; i++) w.Write((byte)0); // [29..38] anchorPriorityVector (reserved)
                w.Write((double)0);                        // [39..46] coBubbleBlendMaxWindow (reserved)
                w.Write((float)0);                         // [47..50] coBubbleResampleHz (reserved)
            }

            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(buffer);
            }
        }

        private static bool IsSupportedBinaryVersion(int version)
        {
            return version == PannotationsBinaryVersion;
        }

        private static void SkipHeader(BinaryReader reader)
        {
            for (int i = 0; i < Magic.Length; i++) reader.ReadByte();
            reader.ReadInt32(); // binaryVersion
            reader.ReadInt32(); // algorithmStampVersion
            reader.ReadInt32(); // sourceSidecarEpoch
            reader.ReadInt32(); // sourceRecordingFormatVersion
            reader.ReadBytes(32); // configurationHash
            reader.ReadString(); // recordingId
        }
    }
}
