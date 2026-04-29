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
    /// Phase 5 co-bubble blend tunables (design doc §6.5 / §10 / §22 / §17.3.1
    /// ConfigurationHash table). The two persisted values
    /// (<see cref="ResampleHz"/>, <see cref="BlendMaxWindowSeconds"/>) participate
    /// in the canonical encoding so changing them invalidates every cached
    /// <c>.pann</c>'s co-bubble traces (HR-10). The crossfade duration is
    /// purely a render-time visual decision (no stored state depends on it)
    /// and so is intentionally NOT in the hash.
    /// </summary>
    internal struct CoBubbleConfiguration
    {
        /// <summary>Target trace resample rate (Hz). 4 Hz matches the slowest
        /// typical active-vessel sampling cadence; oversampling burns sidecar
        /// bytes for sub-mm fidelity gain on common-mode-cancelled offsets.</summary>
        public float ResampleHz;

        /// <summary>Per-trace duration cap (seconds). Windows longer than
        /// this fall back to standalone via the §10.3 boundary set.</summary>
        public double BlendMaxWindowSeconds;

        /// <summary>Crossfade duration at window exit (seconds). Short enough
        /// to be imperceptible during exit, long enough to mask sub-meter
        /// snap. NOT persisted — a render-time visual decision only.</summary>
        public double CrossfadeDurationSeconds;

        internal static CoBubbleConfiguration Default => new CoBubbleConfiguration
        {
            ResampleHz = 4.0f,
            BlendMaxWindowSeconds = 600.0,
            CrossfadeDurationSeconds = 1.5,
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
        internal const int MaxCoBubbleTraceEntries = 1_000;

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

        internal const int PannotationsBinaryVersion = 1;
        // Bumped to 2 in Phase 4: ExoPropulsive / ExoBallistic splines are now
        // fitted in inertial-longitude space (FrameTag = 1) instead of body-
        // fixed. The .pann binary schema is unchanged — frameTag was already
        // serialized — but the algorithm output for the same input differs,
        // so HR-10 invalidates v1 .pann files via alg-stamp-drift.
        // Bumped to 3 in Phase 6: AnchorCandidatesList block transitions from
        // always-empty to populated by AnchorCandidateBuilder. Existing
        // AlgorithmStampVersion=2 .pann files lack the candidate payload and
        // would force a runtime fallback for every consumer; the bump triggers
        // the existing alg-stamp-drift path so stale files are discarded and
        // recomputed on first load (HR-10).
        // Bumped to 4 in Phase 6 follow-up (ultrareview P1-A): the
        // ConfigurationHash canonical encoding gained the `useAnchorTaxonomy`
        // tunable byte. A .pann written with v3 hashed without the flag, so
        // its ConfigurationHash will compare equal to the new v4 hash for
        // recordings whose flag happens to match the v3 default — that is a
        // false-cache-hit risk. The alg-stamp bump forces every existing
        // .pann to invalidate via alg-stamp-drift on first load after the
        // upgrade, regardless of the configured flag value.
        // Bumped to 5 in Phase 5: CoBubbleOffsetTraces transitions from
        // always-empty (count=0) to populated by CoBubbleOverlapDetector.
        // Existing v4 .pann files lack the populated block and would force
        // a runtime fallback for every consumer; the bump triggers the
        // existing alg-stamp-drift path so stale files are discarded and
        // recomputed on first load (HR-10).
        // Bumped to 6 in Phase 5 follow-up (P1-A fix): CoBubbleOffsetTrace
        // gained a BodyName field so the runtime blender can resolve the
        // body for the FrameTag=1 inertial→world rotation lower. v5 traces
        // lack the field; without invalidation, the loader would install
        // them with BodyName=null and the lower would silently become a
        // no-op for inertial offsets (mirroring the production bug fixed
        // by P1-A). Bumping the alg stamp drives every v5 .pann through
        // alg-stamp-drift on first load so the recompute writes the new
        // field (HR-10).
        // Bumped to 7 in Phase 5 review-pass-2 (P1-B + P2-B fixes): the
        // recompute path in SmoothingPipeline.LoadOrCompute now invokes
        // CoBubbleOverlapDetector.DetectAndStore so cache-miss / config-
        // drifted .pann files regenerate co-bubble traces (rather than
        // rewriting an empty block), and BuildTrace clamps each trace to
        // BlendMaxWindowSeconds so very long overlap windows no longer
        // store EndUTs without sample coverage. v6 .pann files written
        // before these fixes have semantically incorrect trace contents;
        // bumping the alg stamp drives them through alg-stamp-drift on
        // first load (HR-10). Phase 5 review-pass-3 (P1-A peer-content
        // signature deferral, P2-1 past-end clamp, P3-1 lazy-recompute
        // peer-pann symmetry) does NOT bump the stamp — those are
        // correctness fixes in validation logic and runtime dispatch,
        // not changes to persisted trace contents.
        //
        // PHASE 8 STACK COORDINATION (PR #644 rebase fixup): Phase 8
        // already bumped AlgorithmStampVersion to 7 in commit 8b9f623d
        // for OutlierFlagsList AND grew CanonicalEncodingLength 53 → 86
        // for additional ConfigurationHash bytes. After Phase 5 lands at
        // 7 here, the Phase 8 rebase needs BOTH bumps adjusted:
        //   • AlgorithmStampVersion 7 → 8
        //   • CanonicalEncodingLength stays at the Phase-8 value (86),
        //     since Phase 5 didn't grow the hash payload — it only
        //     reuses bytes already reserved at Phase 5.
        // Failing to bump AlgorithmStampVersion in the rebase fixup
        // would make Phase 8's OutlierFlagsList block read as cache-hit
        // against pre-Phase-8 (or Phase-5-only) .pann files (HR-10
        // freshness violation).
        internal const int AlgorithmStampVersion = 7;
        private const int CanonicalEncoderVersion = 1;

        // Configuration-hash canonical encoding length: PANC(4) + encVer(4) +
        // splineType(1) + tension(4) + minSamples(4) + maxKnots(4) +
        // outlierAccelAtm(4) + outlierAccelExo(4) + anchorPriority(10) +
        // coBubbleBlendMaxWindow(8) + coBubbleResampleHz(4) +
        // useAnchorTaxonomy(1) + useCoBubbleBlend(1) = 53 bytes. Phase 5
        // appended the useCoBubbleBlend flag at [52] so flag flips invalidate
        // cached .pann files via config-hash-drift (HR-10 freshness): when
        // the flag is off the writer emits an empty CoBubbleOffsetTraces
        // block; flipping to on without invalidating would let a stale
        // empty block masquerade as fresh.
        private const int CanonicalEncodingLength = 53;

        // Per-trace UT-array size cap (Phase 5). With 4 Hz resample × 600s
        // max-window the realistic ceiling per trace is 2400 samples; the
        // 100K cap is a defense-in-depth bound, not a tight steady-state
        // limit. ValidateCount uses min-bytes-per-entry (8 + 12 = 20 bytes
        // per UT row: double UT + 3×float dx/dy/dz) to gate stream-length
        // sanity.
        internal const int MaxCoBubbleSamplesPerTrace = 100_000;
        private const int BytesPerCoBubbleSampleRow = 20;

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
        /// Phase 5 overload: also reads the <c>CoBubbleOffsetTraces</c>
        /// block. Existing two-out callers (kept as the legacy overload
        /// above) still work; new callers should use this overload to access
        /// the per-trace data persisted by Phase 5.
        /// </summary>
        internal static bool TryRead(
            string path,
            PannotationsSidecarProbe probe,
            out List<KeyValuePair<int, SmoothingSpline>> splines,
            out List<KeyValuePair<int, AnchorCandidate[]>> anchorCandidates,
            out List<CoBubbleOffsetTrace> coBubbleTraces,
            out string failureReason)
        {
            if (!probe.Success || !probe.Supported)
                throw new InvalidOperationException("Pannotations sidecar probe must succeed before read.");

            splines = new List<KeyValuePair<int, SmoothingSpline>>();
            anchorCandidates = new List<KeyValuePair<int, AnchorCandidate[]>>();
            coBubbleTraces = new List<CoBubbleOffsetTrace>();
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

                    // Reserved blocks — Phase 1 writes count = 0 for each;
                    // tighter caps still apply so a future-version reader
                    // can't be tricked into a giant allocation by claiming
                    // a Phase-1 file has populated reserved blocks.
                    int outlierCount = reader.ReadInt32();
                    if (!ValidateCount(stream, outlierCount, MaxOutlierFlagsEntries,
                            1, "outlier-flags", out failureReason))
                        return false;
                    if (outlierCount != 0)
                    {
                        failureReason = $"unexpected outlier-flags count {outlierCount} for binary version {probe.BinaryVersion}";
                        return false;
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
                    // Phase 5 CoBubbleOffsetTraces block (design doc §17.3.1).
                    // Per-entry: peerRecordingId (length-prefixed UTF-8) +
                    // peerSourceFormatVersion (int32) + peerSidecarEpoch (int32) +
                    // peerContentSignature (32 bytes) + startUT (double) +
                    // endUT (double) + frameTag (byte) + sampleCount (int32) +
                    // uts[sampleCount] (double) + dx/dy/dz[sampleCount] (float) +
                    // primaryDesignation (byte) + bodyName (length-prefixed UTF-8,
                    // P1-A fix; alg-stamp v6 ensures legacy v5 files cannot
                    // reach this code path because alg-stamp-drift discards
                    // them ahead of the read). Per-entry minimum cost is
                    // header (1 + 4 + 4 + 32 + 8 + 8 + 1 + 4 + 1 + 1) = 64
                    // bytes assuming empty peerRecordingId + empty bodyName;
                    // ValidateCount uses 64 as the per-entry minimum so a
                    // count whose payload would exceed remaining stream
                    // bytes is rejected before allocation. The per-trace
                    // sample-count gate uses the dedicated
                    // MaxCoBubbleSamplesPerTrace.
                    const int MinBytesPerCoBubbleTraceEntry = 64;
                    int coBubbleCount = reader.ReadInt32();
                    if (!ValidateCount(stream, coBubbleCount, MaxCoBubbleTraceEntries,
                            MinBytesPerCoBubbleTraceEntry, "co-bubble-trace", out failureReason))
                        return false;
                    for (int i = 0; i < coBubbleCount; i++)
                    {
                        string peerRecordingId = reader.ReadString();
                        int peerFormatVersion = reader.ReadInt32();
                        int peerEpoch = reader.ReadInt32();
                        byte[] peerSignature = reader.ReadBytes(32);
                        if (peerSignature == null || peerSignature.Length != 32)
                        {
                            failureReason = $"co-bubble-trace[{i}] truncated peer signature";
                            return false;
                        }
                        double startUT = reader.ReadDouble();
                        double endUT = reader.ReadDouble();
                        byte frameTag = reader.ReadByte();
                        int sampleCount = reader.ReadInt32();
                        if (!ValidateCount(stream, sampleCount, MaxCoBubbleSamplesPerTrace,
                                BytesPerCoBubbleSampleRow, $"co-bubble-trace[{i}].samples", out failureReason))
                            return false;
                        double[] uts = new double[sampleCount];
                        for (int u = 0; u < sampleCount; u++) uts[u] = reader.ReadDouble();
                        float[] dx = new float[sampleCount];
                        for (int u = 0; u < sampleCount; u++) dx[u] = reader.ReadSingle();
                        float[] dy = new float[sampleCount];
                        for (int u = 0; u < sampleCount; u++) dy[u] = reader.ReadSingle();
                        float[] dz = new float[sampleCount];
                        for (int u = 0; u < sampleCount; u++) dz[u] = reader.ReadSingle();
                        byte primaryDesignation = reader.ReadByte();
                        // P1-A: BodyName persisted (v6 alg-stamp). Empty
                        // string accepted; legacy v5 files cannot land here
                        // because alg-stamp-drift gate rejects them.
                        string bodyName = reader.ReadString();

                        coBubbleTraces.Add(new CoBubbleOffsetTrace
                        {
                            PeerRecordingId = peerRecordingId,
                            PeerSourceFormatVersion = peerFormatVersion,
                            PeerSidecarEpoch = peerEpoch,
                            PeerContentSignature = peerSignature,
                            StartUT = startUT,
                            EndUT = endUT,
                            FrameTag = frameTag,
                            UTs = uts,
                            Dx = dx,
                            Dy = dy,
                            Dz = dz,
                            PrimaryDesignation = primaryDesignation,
                            BodyName = string.IsNullOrEmpty(bodyName) ? null : bodyName,
                        });
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
            IList<CoBubbleOffsetTrace> coBubbleTraces = null)
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

                // Phase 5 CoBubbleOffsetTraces block (design doc §17.3.1).
                // Each trace fully self-describes its peer cache key via
                // (peerSourceFormatVersion, peerSidecarEpoch, peerContentSignature)
                // so the per-trace validation pass in SmoothingPipeline can
                // drop a single stale trace without invalidating the whole
                // .pann file.
                int coBubbleEntryCount = coBubbleTraces?.Count ?? 0;
                writer.Write(coBubbleEntryCount);
                if (coBubbleTraces != null)
                {
                    for (int i = 0; i < coBubbleTraces.Count; i++)
                    {
                        CoBubbleOffsetTrace t = coBubbleTraces[i];
                        if (t == null)
                            throw new InvalidOperationException(
                                $"CoBubbleOffsetTraces[{i}] is null — caller must drop empty entries before write");
                        if (t.PeerContentSignature == null || t.PeerContentSignature.Length != 32)
                            throw new ArgumentException(
                                $"CoBubbleOffsetTraces[{i}] has invalid PeerContentSignature (must be 32 bytes)");
                        int sampleCount = t.UTs?.Length ?? 0;
                        int dxCount = t.Dx?.Length ?? 0;
                        int dyCount = t.Dy?.Length ?? 0;
                        int dzCount = t.Dz?.Length ?? 0;
                        if (sampleCount != dxCount || sampleCount != dyCount || sampleCount != dzCount)
                            throw new ArgumentException(
                                $"CoBubbleOffsetTraces[{i}] sample arrays length mismatch: " +
                                $"uts={sampleCount} dx={dxCount} dy={dyCount} dz={dzCount}");

                        writer.Write(t.PeerRecordingId ?? string.Empty);
                        writer.Write(t.PeerSourceFormatVersion);
                        writer.Write(t.PeerSidecarEpoch);
                        writer.Write(t.PeerContentSignature);
                        writer.Write(t.StartUT);
                        writer.Write(t.EndUT);
                        writer.Write(t.FrameTag);
                        writer.Write(sampleCount);
                        for (int u = 0; u < sampleCount; u++) writer.Write(t.UTs[u]);
                        for (int u = 0; u < sampleCount; u++) writer.Write(t.Dx[u]);
                        for (int u = 0; u < sampleCount; u++) writer.Write(t.Dy[u]);
                        for (int u = 0; u < sampleCount; u++) writer.Write(t.Dz[u]);
                        writer.Write(t.PrimaryDesignation);
                        // P1-A: BodyName drives the runtime blender's body
                        // resolution for FrameTag=1 inertial→world lower.
                        // Empty string is acceptable on disk (length-byte 0);
                        // the loader normalises empty back to null so the
                        // blender's null-body branch fires consistently.
                        writer.Write(t.BodyName ?? string.Empty);
                    }
                }

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
            // Backward-compatible overload: tests that don't care about the
            // Phase 6 / Phase 5 flags default them to true (matches the
            // shipped defaults). Production callers should use the
            // three-argument overload below so a flag flip invalidates
            // cached .pann files.
            return ComputeConfigurationHash(cfg, useAnchorTaxonomy: true, useCoBubbleBlend: true);
        }

        /// <summary>
        /// Phase 6 follow-up: two-argument overload kept for any caller that
        /// was wired before Phase 5. Defaults <c>useCoBubbleBlend</c> to
        /// true (matches the shipped default). Production callers should
        /// migrate to the three-argument overload so a Phase-5 flag flip
        /// invalidates the cache key.
        /// </summary>
        internal static byte[] ComputeConfigurationHash(
            SmoothingConfiguration cfg, bool useAnchorTaxonomy)
        {
            return ComputeConfigurationHash(cfg, useAnchorTaxonomy, useCoBubbleBlend: true);
        }

        /// <summary>
        /// Phase 5: the canonical encoding now wires the co-bubble blend
        /// tunables (<see cref="CoBubbleConfiguration.Default"/>) into bytes
        /// [39..46] (BlendMaxWindowSeconds) and [47..50] (ResampleHz), and
        /// appends the <see cref="ParsekSettings.useCoBubbleBlend"/> flag
        /// as a single byte at offset 52. Flipping the flag changes the
        /// derived <c>CoBubbleOffsetTraces</c> output (writer emits an
        /// empty block when off, populated when on), so HR-10 freshness
        /// requires the flag to participate in the cache key. The Phase 6
        /// <c>useAnchorTaxonomy</c> byte stays at offset 51.
        /// </summary>
        internal static byte[] ComputeConfigurationHash(
            SmoothingConfiguration cfg, bool useAnchorTaxonomy, bool useCoBubbleBlend)
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
                w.Write((double)CoBubbleConfiguration.Default.BlendMaxWindowSeconds); // [39..46] coBubbleBlendMaxWindow
                w.Write((float)CoBubbleConfiguration.Default.ResampleHz);             // [47..50] coBubbleResampleHz
                w.Write((byte)(useAnchorTaxonomy ? 1 : 0)); // [51] useAnchorTaxonomy (Phase 6)
                w.Write((byte)(useCoBubbleBlend ? 1 : 0));  // [52] useCoBubbleBlend (Phase 5)
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
