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
    /// smoothing-related fields; later phases append outlier tunables to
    /// the canonical encoding without reordering.
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
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PNA0");
        private static readonly byte[] CanonicalMagic = Encoding.ASCII.GetBytes("PNC0");

        // P2#2: upper bounds on every count field read from a .pann file.
        // A real .pann has at most a few hundred entries per block; values
        // above these caps are malformed and trigger a discard-and-recompute.
        // Without the bounds, an attacker-controlled or randomly-corrupted
        // file with int.MaxValue could force a multi-GB allocation
        // (ArgumentOutOfRangeException for negative values, OutOfMemory for
        // huge ones) and either crash or lock the process before TryRead
        // returns.
        //
        // Per-block caps reflect realistic upper bounds for each block type;
        // the stream-length check below is the second line of defense — any
        // count whose payload would exceed the remaining file bytes is
        // rejected before allocation.
        internal const int MaxStringTableEntries = 10_000;
        internal const int MaxSplineEntries = 10_000;
        internal const int MaxKnotsPerSpline = 100_000;
        internal const int MaxOutlierFlagsEntries = 10_000;
        internal const int MaxAnchorCandidateEntries = 10_000;

        // Hard fallback retained for tests / future blocks that don't yet
        // have a tighter cap. Realistic blocks should always use one of the
        // per-block caps above.
        internal const int MaxReasonableCount = 10_000_000;

        // Minimum byte cost per element for the stream-length sanity check.
        // Each length-prefixed empty string in the BinaryReader format costs
        // 1 byte (a single LEB128 0). A spline entry's fixed header is 14
        // bytes (sectionIndex+splineType+tension+knotCount+frameTag) before
        // the variable arrays.
        private const int MinBytesPerStringTableEntry = 1;
        private const int MinBytesPerSplineEntry = 14;
        private const int BytesPerKnotRow = 20; // 1 double + 3 floats per knot

        // Phase 6 anchor-candidate per-section minimum: int sectionIndex (4) +
        // int utCount (4) + at least one UT double (8) + one type byte (1) = 17.
        // ValidateCount uses min-bytes-per-entry to gate stream-length sanity;
        // the per-section header alone is 8 bytes, so we use 9 (header + the
        // smallest possible utCount=1 emission). This matches the §17.3.1
        // schema exactly.
        private const int MinBytesPerAnchorCandidateEntry = 9;

        internal const int PannotationsBinaryVersion = 0;
        // The .pann schema generation was reset to 0 with the format-v0
        // recording reset; the historical 1 -> 12 bump trail is preserved
        // in git log. Any future change to the .pann algorithm output for
        // the same input must bump this stamp so existing files invalidate
        // via alg-stamp-drift on first load (HR-10). Bumped to 1 when the
        // useAnchorTaxonomy + useOutlierRejection rollout flags were removed
        // and their bytes dropped from the configuration-hash encoding.
        internal const int AlgorithmStampVersion = 1;
        private const int CanonicalEncoderVersion = 0;

        // Configuration-hash canonical encoding length: PANC(4) + encVer(4) +
        // splineType(1) + tension(4) + minSamples(4) + maxKnots(4) +
        // outlierAccelAtm(4) + outlierAccelExoPropulsive(4) +
        // anchorPriority(10) +
        // outlierAccelExoBallistic(4) + outlierAccelSurfaceMobile(4) +
        // outlierAccelSurfaceStationary(4) + outlierAccelApproach(4) +
        // outlierBubbleRadius(4) + outlierAltitudeFloor(4) +
        // outlierAltitudeCeilingMargin(4) + outlierClusterRate(4) = 71 bytes.
        // The useAnchorTaxonomy(1) + useOutlierRejection(1) rollout-flag bytes
        // were dropped when those flags were removed (the pipeline is now
        // unconditionally on); AlgorithmStampVersion bumped to 1 alongside so
        // every existing .pann invalidates via alg-stamp-drift on first load.
        // Any future change to the outlier thresholds or smoothing config also
        // drifts the cache key via config-hash-drift (HR-10).
        private const int CanonicalEncodingLength = 71;

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

            try
            {
                return TryProbeCore(path, ref probe);
            }
            catch (IOException ex)
            {
                // FileStream open errors (file locked by another process,
                // permission issue, mid-rename collision). The .pann is
                // regenerable — surface the failure as cache-miss rather
                // than letting it abort the recording load.
                probe.FailureReason = $"io error opening pannotations: {ex.Message}";
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                probe.FailureReason = $"access denied opening pannotations: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                // BinaryReader.ReadString() can throw ArgumentOutOfRangeException
                // on a negative LEB128 length byte; ReadBytes can throw on
                // truncated streams that the prelude length-check missed
                // because of slack space. Treat any malformed-payload
                // exception as cache-miss + recompute (matches TryRead's
                // broad catch and HR-9 visible-failure contract).
                probe.FailureReason = $"malformed pannotations header: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool TryProbeCore(string path, ref PannotationsSidecarProbe probe)
        {
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
            out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates,
            out string failureReason)
        {
            return TryRead(path, probe, out splines, out anchorCandidates,
                out _, out failureReason);
        }

        /// <summary>
        /// Phase 8 overload: also reads the <c>OutlierFlagsList</c> block.
        /// Returns the per-section <see cref="OutlierFlags"/> entries the
        /// writer persisted (sections without rejected samples are not
        /// emitted by the writer and so do not appear here).
        /// </summary>
        internal static bool TryRead(
            string path,
            PannotationsSidecarProbe probe,
            out List<KeyValuePair<int, SmoothingSpline>> splines,
            out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates,
            out List<KeyValuePair<int, OutlierFlags>> outlierFlags,
            out string failureReason)
        {
            if (!probe.Success || !probe.Supported)
                throw new InvalidOperationException("Pannotations sidecar probe must succeed before read.");

            splines = new List<KeyValuePair<int, SmoothingSpline>>();
            anchorCandidates = new List<KeyValuePair<int, AnchorCandidate[]>>();
            outlierFlags = new List<KeyValuePair<int, OutlierFlags>>();
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
                    if (!ValidateCount(stream, tableCount, MaxStringTableEntries,
                            MinBytesPerStringTableEntry, "string-table", out failureReason))
                        return false;
                    var stringTable = new List<string>(tableCount);
                    for (int i = 0; i < tableCount; i++)
                        stringTable.Add(reader.ReadString());

                    // SmoothingSplineList
                    int splineCount = reader.ReadInt32();
                    if (!ValidateCount(stream, splineCount, MaxSplineEntries,
                            MinBytesPerSplineEntry, "spline", out failureReason))
                        return false;
                    for (int i = 0; i < splineCount; i++)
                    {
                        int sectionIndex = reader.ReadInt32();
                        byte splineType = reader.ReadByte();
                        float tension = reader.ReadSingle();
                        int knotCount = reader.ReadInt32();
                        if (!ValidateCount(stream, knotCount, MaxKnotsPerSpline,
                                BytesPerKnotRow, $"knotCount[{i}]", out failureReason))
                            return false;

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

                    // Phase 8 OutlierFlagsList block (design doc §17.3.1).
                    // Schema: per-entry sectionIndex (int32) + classifierMask
                    // (byte) + packedBitmap (length-prefixed byte[]) +
                    // rejectedCount (int32). Per-entry minimum cost is 13
                    // bytes (4+1+4+0+4) when the bitmap is empty; a populated
                    // bitmap adds (sampleCount + 7) / 8 bytes.
                    const int MinBytesPerOutlierEntry = 13;
                    int outlierCount = reader.ReadInt32();
                    if (!ValidateCount(stream, outlierCount, MaxOutlierFlagsEntries,
                            MinBytesPerOutlierEntry, "outlier-flags", out failureReason))
                        return false;
                    for (int i = 0; i < outlierCount; i++)
                    {
                        int sectionIndex = reader.ReadInt32();
                        byte classifierMask = reader.ReadByte();
                        int bitmapLength = reader.ReadInt32();
                        // The packed bitmap is at most (MaxKnotsPerSpline+7)/8
                        // = 12500 bytes — generous upper bound so a corrupt
                        // length cannot force a multi-MB allocation.
                        int bitmapByteCap = (MaxKnotsPerSpline + 7) / 8;
                        if (!ValidateCount(stream, bitmapLength, bitmapByteCap,
                                1, $"outlier-flags[{i}].bitmap", out failureReason))
                            return false;
                        byte[] bitmap = bitmapLength > 0
                            ? reader.ReadBytes(bitmapLength)
                            : new byte[0];
                        if (bitmap == null || bitmap.Length != bitmapLength)
                        {
                            failureReason = $"outlier-flags[{i}] truncated bitmap (expected {bitmapLength} bytes)";
                            return false;
                        }
                        int rejectedCount = reader.ReadInt32();
                        outlierFlags.Add(new KeyValuePair<int, OutlierFlags>(
                            sectionIndex,
                            new OutlierFlags
                            {
                                SectionIndex = sectionIndex,
                                ClassifierMask = classifierMask,
                                PackedBitmap = bitmap,
                                RejectedCount = rejectedCount,
                                // SampleCount is not persisted; the loader
                                // (SmoothingPipeline.LoadOrCompute) refills
                                // it from the live section's frame count
                                // after install.
                                SampleCount = 0,
                            }));
                    }
                    // Phase 6 AnchorCandidatesList block (design doc §17.3.1).
                    // Schema: per-entry sectionIndex (int) + utCount (int) +
                    // uts[utCount] (double) + types[utCount] (byte). Side bit
                    // is packed into bit 7 of each type byte
                    // (AnchorCandidate.EndSideMask).
                    int anchorCount = reader.ReadInt32();
                    if (!ValidateCount(stream, anchorCount, MaxAnchorCandidateEntries,
                            MinBytesPerAnchorCandidateEntry, "anchor-candidate", out failureReason))
                        return false;
                    for (int i = 0; i < anchorCount; i++)
                    {
                        int sectionIndex = reader.ReadInt32();
                        int utCount = reader.ReadInt32();
                        // 9 bytes minimum per UT: 8-byte double + 1-byte type.
                        if (!ValidateCount(stream, utCount, MaxAnchorCandidateEntries,
                                9, $"anchor-candidate[{i}].uts", out failureReason))
                            return false;
                        var arr = new AnchorCandidate[utCount];
                        double[] uts = new double[utCount];
                        for (int u = 0; u < utCount; u++) uts[u] = reader.ReadDouble();
                        for (int u = 0; u < utCount; u++)
                        {
                            byte typeByte = reader.ReadByte();
                            AnchorCandidate.FromTypeByte(typeByte, out AnchorSource source, out AnchorSide side);
                            arr[u] = new AnchorCandidate(uts[u], source, side);
                        }
                        anchorCandidates.Add(new KeyValuePair<int, AnchorCandidate[]>(sectionIndex, arr));
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
            catch (Exception ex)
            {
                // P2#2: broad catch for malformed payloads. The .pann is a
                // regenerable cache (HR-9: failure visible, HR-10: drift triggers
                // recompute) so any malformed-payload exception (negative
                // ReadString length → ArgumentOutOfRangeException, oversized
                // pre-validated count slipping past the bound check via integer
                // overflow → OutOfMemoryException, etc.) is treated as cache
                // invalidation. The TryRead returns false → caller logs
                // "Pannotations payload corrupt: ... — recomputing" via
                // SmoothingPipeline.LoadOrCompute, and the recompute path
                // overwrites the bad file. Without this catch, a malformed file
                // would crash the load thread and block recording use entirely.
                failureReason = $"malformed payload: {ex.GetType().Name}: {ex.Message}";
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
            IList<KeyValuePair<int, SmoothingSpline>> splines,
            IList<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates = null,
            IList<KeyValuePair<int, OutlierFlags>> outlierFlags = null)
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

                // Phase 8 OutlierFlagsList block (design doc §17.3.1).
                // Per-entry: sectionIndex (int32) + classifierMask (byte) +
                // packedBitmap length-prefixed byte[] + rejectedCount (int32).
                // Caller-side gathering (in SmoothingPipeline.TryWritePann)
                // drops sections whose RejectedCount is zero so the block is
                // compact in the steady-state "no krakens detected" case.
                int outlierEntryCount = outlierFlags?.Count ?? 0;
                writer.Write(outlierEntryCount);
                if (outlierFlags != null)
                {
                    for (int i = 0; i < outlierFlags.Count; i++)
                    {
                        OutlierFlags f = outlierFlags[i].Value;
                        if (f == null)
                            throw new InvalidOperationException(
                                $"OutlierFlagsList[{i}] is null — caller must drop empty entries before write");
                        if (f.PackedBitmap == null)
                            throw new ArgumentException(
                                $"OutlierFlagsList[{i}].PackedBitmap is null");
                        writer.Write(outlierFlags[i].Key); // sectionIndex
                        writer.Write(f.ClassifierMask);
                        writer.Write(f.PackedBitmap.Length);
                        if (f.PackedBitmap.Length > 0)
                            writer.Write(f.PackedBitmap);
                        writer.Write(f.RejectedCount);
                    }
                }

                // Phase 6 AnchorCandidatesList block (design doc §17.3.1).
                // Per-section: sectionIndex (int) + utCount (int) +
                // uts[utCount] (double) + types[utCount] (byte). The side
                // bit is bit 7 of the type byte (AnchorCandidate.EndSideMask);
                // every AnchorSource value is < 128 so no collision today.
                int anchorEntryCount = anchorCandidates?.Count ?? 0;
                writer.Write(anchorEntryCount);
                if (anchorCandidates != null)
                {
                    for (int i = 0; i < anchorCandidates.Count; i++)
                    {
                        int sectionIndex = anchorCandidates[i].Key;
                        AnchorCandidate[] arr = anchorCandidates[i].Value ?? new AnchorCandidate[0];
                        writer.Write(sectionIndex);
                        writer.Write(arr.Length);
                        for (int u = 0; u < arr.Length; u++) writer.Write(arr[u].UT);
                        for (int u = 0; u < arr.Length; u++) writer.Write(arr[u].ToTypeByte());
                    }
                }

                writer.Flush();
                FileIOUtils.SafeWriteBytes(stream.ToArray(), path, "Pipeline-Sidecar");
            }
        }

        /// <summary>
        /// Computes the SHA-256 cache key over a fully-pinned canonical
        /// encoding of <paramref name="cfg"/> plus the outlier thresholds
        /// (design doc §17.3.1 "Configuration Cache Key"). The encoding is
        /// endian-fixed and field-order-fixed: future phases append fields,
        /// never reorder.
        /// </summary>
        internal static byte[] ComputeConfigurationHash(SmoothingConfiguration cfg)
        {
            return ComputeConfigurationHash(cfg, OutlierThresholds.Default);
        }

        /// <summary>
        /// Explicit-thresholds overload — used by tests that perturb
        /// individual outlier thresholds to verify HR-10 cache-key freshness.
        /// </summary>
        internal static byte[] ComputeConfigurationHash(
            SmoothingConfiguration cfg,
            OutlierThresholds outlier)
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
                w.Write(outlier.AccelCeilingAtmospheric);  // [21..24]
                w.Write(outlier.AccelCeilingExoPropulsive); // [25..28]
                for (int i = 0; i < 10; i++) w.Write((byte)0); // [29..38] reserved
                w.Write(outlier.AccelCeilingExoBallistic);     // [39..42]
                w.Write(outlier.AccelCeilingSurfaceMobile);    // [43..46]
                w.Write(outlier.AccelCeilingSurfaceStationary); // [47..50]
                w.Write(outlier.AccelCeilingApproach);         // [51..54]
                w.Write(outlier.MaxSingleTickPositionDeltaMeters); // [55..58]
                w.Write(outlier.AltitudeFloorMeters);          // [59..62]
                w.Write(outlier.AltitudeCeilingMargin);        // [63..66]
                w.Write(outlier.ClusterRateThreshold);         // [67..70]
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

        /// <summary>
        /// Validate a count read from a .pann file: must be non-negative, must
        /// not exceed the realistic per-block cap, and the count's payload
        /// must fit within the remaining stream bytes (assuming a minimum
        /// per-element size). Returns false with a populated failure reason
        /// when any check fails.
        /// </summary>
        /// <remarks>
        /// The stream-length check is essential: a per-block cap of 10K
        /// entries × 20 bytes/knot still permits a single corrupt spline
        /// with knotCount=100K to attempt a 2 MB allocation, but in
        /// practice the file would be much smaller. Comparing against
        /// remaining bytes catches the inconsistency before any allocation.
        /// Use long arithmetic for the multiply so int.MaxValue × N can't
        /// silently overflow back into a small positive bound.
        /// </remarks>
        private static bool ValidateCount(
            Stream stream,
            int count,
            int perBlockCap,
            int minBytesPerEntry,
            string fieldName,
            out string failureReason)
        {
            failureReason = null;
            if (count < 0)
            {
                failureReason = $"invalid {fieldName} count: {count} (negative)";
                return false;
            }
            if (count > perBlockCap)
            {
                failureReason = $"invalid {fieldName} count: {count} (exceeds cap {perBlockCap})";
                return false;
            }
            long requiredBytes = (long)count * (long)minBytesPerEntry;
            long remaining = stream.Length - stream.Position;
            if (requiredBytes > remaining)
            {
                failureReason = $"invalid {fieldName} count: {count} would need {requiredBytes} bytes but only {remaining} remain";
                return false;
            }
            return true;
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
