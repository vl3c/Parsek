using System;
using System.Collections.Generic;
using System.IO;
using Parsek;
using Parsek.Rendering;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 5 round-trip + per-trace validation tests for the
    /// <c>CoBubbleOffsetTraces</c> block in the <c>.pann</c> binary
    /// (design doc §17.3.1, §10, §20.5 Phase 5 row).
    /// </summary>
    [Collection("Sequential")]
    public class CoBubbleSidecarRoundTripTests : IDisposable
    {
        private readonly string tempDir;
        private readonly List<string> logLines = new List<string>();

        public CoBubbleSidecarRoundTripTests()
        {
            tempDir = Path.Combine(Path.GetTempPath(),
                "parsek_pann_cobubble_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tempDir);
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            SmoothingPipeline.ResetForTesting();
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => true;
        }

        public void Dispose()
        {
            SmoothingPipeline.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        private static CoBubbleOffsetTrace MakeTrace(string peerId, int sampleCount = 4,
            byte frameTag = 0, double startUT = 100.0, double endUT = 110.0,
            string bodyName = "Kerbin")
        {
            var uts = new double[sampleCount];
            var dx = new float[sampleCount];
            var dy = new float[sampleCount];
            var dz = new float[sampleCount];
            double step = (endUT - startUT) / Math.Max(1, sampleCount - 1);
            for (int i = 0; i < sampleCount; i++)
            {
                uts[i] = startUT + i * step;
                dx[i] = 1.0f + i;
                dy[i] = 2.0f + i;
                dz[i] = 3.0f + i;
            }
            byte[] sig = new byte[32];
            for (int i = 0; i < 32; i++) sig[i] = (byte)(i + peerId.Length);
            return new CoBubbleOffsetTrace
            {
                PeerRecordingId = peerId,
                PeerSourceFormatVersion = 8,
                PeerSidecarEpoch = 3,
                PeerContentSignature = sig,
                StartUT = startUT,
                EndUT = endUT,
                FrameTag = frameTag,
                PrimaryDesignation = 0,
                UTs = uts,
                Dx = dx,
                Dy = dy,
                Dz = dz,
                BodyName = bodyName,
            };
        }

        [Fact]
        public void Write_Read_RoundTrip_PreservesAllTraceFields()
        {
            // What makes it fail: any silent mutation of trace fields on
            // save/load would feed the blender a different offset from the
            // one detected at commit (HR-3).
            string path = Path.Combine(tempDir, "rec.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);

            var traces = new List<CoBubbleOffsetTrace>
            {
                MakeTrace("peer-A", sampleCount: 5, frameTag: 0, startUT: 100.0, endUT: 120.0),
                MakeTrace("peer-B", sampleCount: 3, frameTag: 1, startUT: 200.0, endUT: 215.0),
                MakeTrace("peer-C", sampleCount: 2, frameTag: 0, startUT: 300.0, endUT: 305.0),
            };

            PannotationsSidecarBinary.Write(path, "rec-cobubble", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                coBubbleTraces: traces);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(probe.Success);
            Assert.True(probe.Supported);

            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out var splines, out var cands, out var read, out string failure));
            Assert.Null(failure);
            Assert.Equal(traces.Count, read.Count);
            for (int i = 0; i < traces.Count; i++)
            {
                Assert.Equal(traces[i].PeerRecordingId, read[i].PeerRecordingId);
                Assert.Equal(traces[i].PeerSourceFormatVersion, read[i].PeerSourceFormatVersion);
                Assert.Equal(traces[i].PeerSidecarEpoch, read[i].PeerSidecarEpoch);
                Assert.Equal(traces[i].PeerContentSignature, read[i].PeerContentSignature);
                Assert.Equal(traces[i].StartUT, read[i].StartUT);
                Assert.Equal(traces[i].EndUT, read[i].EndUT);
                Assert.Equal(traces[i].FrameTag, read[i].FrameTag);
                Assert.Equal(traces[i].PrimaryDesignation, read[i].PrimaryDesignation);
                Assert.Equal(traces[i].UTs, read[i].UTs);
                Assert.Equal(traces[i].Dx, read[i].Dx);
                Assert.Equal(traces[i].Dy, read[i].Dy);
                Assert.Equal(traces[i].Dz, read[i].Dz);
                // P1-A: BodyName must round-trip so the runtime body
                // resolver can drive the FrameTag=1 inertial→world lower.
                Assert.Equal(traces[i].BodyName, read[i].BodyName);
            }
        }

        [Fact]
        public void AlgStampVersion_BumpedToSixOrLater_ForBodyNameField()
        {
            // P1-A: the BodyName field is a new on-disk schema element. v5
            // .pann files lack it; the runtime can't lower their FrameTag=1
            // offsets correctly. Bumping the alg stamp invalidates them via
            // the existing alg-stamp-drift gate so they get recomputed on
            // first load (HR-10).
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 6,
                "AlgorithmStampVersion must be >= 6 after Phase 5 P1-A ships");
        }

        [Fact]
        public void Write_Read_NullBodyName_RoundTripsAsNull()
        {
            // Empty / null bodyName must round-trip through the on-disk
            // length-prefixed string. The reader normalises empty back to
            // null so the blender's null-body branch fires consistently.
            string path = Path.Combine(tempDir, "rec_null_body.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            CoBubbleOffsetTrace trace = MakeTrace("peer", bodyName: null);
            PannotationsSidecarBinary.Write(path, "rec-nobody", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                anchorCandidates: null,
                coBubbleTraces: new List<CoBubbleOffsetTrace> { trace });

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out var read, out _));
            Assert.Single(read);
            Assert.Null(read[0].BodyName);
        }

        [Fact]
        public void EmptyCoBubbleList_RoundTripsAsEmpty()
        {
            // The writer must still emit a valid count=0 header; the
            // reader produces a non-null empty list so callers can
            // distinguish "loaded but empty" from "field missing".
            string path = Path.Combine(tempDir, "rec_empty.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "rec-emp", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());
            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            Assert.True(PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out var coBubble, out _));
            Assert.NotNull(coBubble);
            Assert.Empty(coBubble);
        }

        [Fact]
        public void AlgStampVersion_IsAtLeastFive_AfterPhaseFiveShips()
        {
            // The alg-stamp bump (4 -> 5) is the mechanism that invalidates
            // pre-Phase-5 .pann files (those have CoBubbleOffsetTraces always
            // empty). The drift logic itself lives in SmoothingPipeline; this
            // test pins the constant so a future refactor can't silently
            // revert it.
            Assert.True(PannotationsSidecarBinary.AlgorithmStampVersion >= 5,
                "AlgorithmStampVersion must be >= 5 after Phase 5 ships");
        }

        [Fact]
        public void ClassifyTraceDrift_PeerMissing_ReturnsReason()
        {
            // What makes it fail: a peer that was deleted between commit and
            // load must drop the trace, not silently install stale offsets.
            SmoothingPipeline.CoBubblePeerResolverForTesting = id => null;
            CoBubbleOffsetTrace trace = MakeTrace("missing-peer");
            string reason = SmoothingPipeline.ClassifyTraceDrift(trace);
            Assert.Equal("peer-missing", reason);
        }

        [Fact]
        public void ClassifyTraceDrift_PeerFormatChanged_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            trace.PeerSourceFormatVersion = 8;
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording { RecordingId = id, RecordingFormatVersion = 7, SidecarEpoch = trace.PeerSidecarEpoch };
            Assert.Equal("peer-format-changed", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_PeerEpochChanged_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            trace.PeerSidecarEpoch = 3;
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording { RecordingId = id, RecordingFormatVersion = trace.PeerSourceFormatVersion, SidecarEpoch = 4 };
            Assert.Equal("peer-epoch-changed", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_PeerContentMismatch_ReturnsReason()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            byte[] otherSig = new byte[32];
            for (int i = 0; i < 32; i++) otherSig[i] = 0xAB;
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch
                };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => otherSig;
            Assert.Equal("peer-content-mismatch", SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void ClassifyTraceDrift_AllFieldsMatch_ReturnsNull()
        {
            CoBubbleOffsetTrace trace = MakeTrace("peer");
            SmoothingPipeline.CoBubblePeerResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch
                };
            SmoothingPipeline.CoBubblePeerSignatureRecomputeForTesting =
                (peer, startUT, endUT) => trace.PeerContentSignature;
            Assert.Null(SmoothingPipeline.ClassifyTraceDrift(trace));
        }

        [Fact]
        public void TryRead_CoBubbleCount_AboveCap_RejectedWithExceedsCap()
        {
            // The per-block cap MaxCoBubbleTraceEntries = 1000; counts above
            // it must be rejected before allocation.
            string path = Path.Combine(tempDir, "rec_overcap.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            PannotationsSidecarBinary.Write(path, "recX", 1, 8, hash,
                splines: new List<KeyValuePair<int, SmoothingSpline>>());

            byte[] bytes = File.ReadAllBytes(path);
            // Locate the coBubbleCount int (last int32 in header). The block
            // order is: stringTableCount(4) + splineCount(4) + outlierCount(4)
            // + anchorCount(4) + coBubbleCount(4). The 4th int32 from the end
            // is the coBubbleCount when no payload is present.
            // Easier: scan for the trailing 5 zero ints (each block emits 0
            // count when empty); coBubbleCount is the last one. We rewrite
            // the last 4 bytes of the file (since coBubbleCount is the final
            // field for an empty-payload write).
            int len = bytes.Length;
            bytes[len - 4] = 0xE9; bytes[len - 3] = 0x03; bytes[len - 2] = 0x00; bytes[len - 1] = 0x00;
            // 0x000003E9 = 1001 — exceeds cap of 1000.
            File.WriteAllBytes(path, bytes);

            Assert.True(PannotationsSidecarBinary.TryProbe(path, out var probe));
            bool ok = PannotationsSidecarBinary.TryRead(path, probe,
                out _, out _, out _, out string reason);
            Assert.False(ok);
            Assert.Contains("co-bubble-trace", reason);
        }

        [Fact]
        public void Write_NullSignature_Throws()
        {
            // Defensive: null signatures must be rejected at write so a
            // malformed trace can't slip into a .pann.
            string path = Path.Combine(tempDir, "rec_null_sig.pann");
            byte[] hash = PannotationsSidecarBinary.ComputeConfigurationHash(SmoothingConfiguration.Default);
            CoBubbleOffsetTrace bad = MakeTrace("peer");
            bad.PeerContentSignature = null;
            Assert.Throws<ArgumentException>(() =>
                PannotationsSidecarBinary.Write(path, "recBad", 1, 8, hash,
                    splines: new List<KeyValuePair<int, SmoothingSpline>>(),
                    anchorCandidates: null,
                    coBubbleTraces: new List<CoBubbleOffsetTrace> { bad }));
        }
    }
}
