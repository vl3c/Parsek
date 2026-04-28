using System;
using System.Collections.Generic;
using Parsek;
using Parsek.Rendering;
using UnityEngine;
using Xunit;

namespace Parsek.Tests.Rendering
{
    /// <summary>
    /// Phase 5 unit tests for <see cref="CoBubbleBlender"/> and
    /// <see cref="CoBubblePrimarySelector"/>. The full integration through
    /// <see cref="ParsekFlight.InterpolateAndPosition"/> is exercised by the
    /// in-game tests; these xUnit tests cover the pure-static gate, the
    /// primary-selection rules, and the crossfade math.
    /// </summary>
    [Collection("Sequential")]
    public class CoBubbleBlenderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public CoBubbleBlenderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            RenderSessionState.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => true;
            CoBubbleBlender.ResetForTesting();
            CoBubbleOverlapDetector.ResetForTesting();
            ParsekSettings.CurrentOverrideForTesting = new ParsekSettings();
        }

        public void Dispose()
        {
            ParsekSettings.CurrentOverrideForTesting = null;
            CoBubbleOverlapDetector.ResetForTesting();
            CoBubbleBlender.ResetForTesting();
            SmoothingPipeline.ResetForTesting();
            SectionAnnotationStore.ResetForTesting();
            RenderSessionState.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // ---------- Helpers ----------

        private static CoBubbleOffsetTrace MakeTrace(string peerId, double startUT, double endUT,
            Vector3d offset, byte frameTag = 0)
        {
            int n = 5;
            var uts = new double[n];
            var dx = new float[n];
            var dy = new float[n];
            var dz = new float[n];
            double step = (endUT - startUT) / (n - 1);
            for (int i = 0; i < n; i++)
            {
                uts[i] = startUT + i * step;
                dx[i] = (float)offset.x;
                dy[i] = (float)offset.y;
                dz[i] = (float)offset.z;
            }
            byte[] sig = new byte[32];
            for (int i = 0; i < 32; i++) sig[i] = (byte)i;
            return new CoBubbleOffsetTrace
            {
                PeerRecordingId = peerId,
                PeerSourceFormatVersion = 8,
                PeerSidecarEpoch = 1,
                PeerContentSignature = sig,
                StartUT = startUT,
                EndUT = endUT,
                FrameTag = frameTag,
                PrimaryDesignation = 0,
                UTs = uts,
                Dx = dx,
                Dy = dy,
                Dz = dz,
            };
        }

        // ---------- TryEvaluateOffset gate tests ----------

        [Fact]
        public void TryEvaluateOffset_NullPeerId_ReturnsMissNotInTrace()
        {
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                null, 100.0, out Vector3d offset, out CoBubbleBlendStatus status, out string primary);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissNotInTrace, status);
            Assert.Null(primary);
            Assert.Equal(0.0, offset.x);
            Assert.Equal(0.0, offset.y);
            Assert.Equal(0.0, offset.z);
        }

        [Fact]
        public void TryEvaluateOffset_FlagOff_ReturnsMissDisabledByFlag()
        {
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => false;
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer", 100.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissDisabledByFlag, status);
        }

        [Fact]
        public void TryEvaluateOffset_PrimaryNotResolved_ReturnsMissPrimaryNotResolved()
        {
            // Peer has no entry in the primary map.
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "lonely-peer", 100.0, out _, out CoBubbleBlendStatus status, out string primary);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissPrimaryNotResolved, status);
            Assert.Null(primary);
        }

        [Fact]
        public void TryEvaluateOffset_PeerIsItselfPrimary_ReturnsMissRecursionGuard()
        {
            // Recording X is primary for some peer Y. A query for X must
            // be guarded — primaries always render standalone (§6.5).
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-Y", "rec-X");
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "rec-X", 100.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissRecursionGuard, status);
        }

        [Fact]
        public void TryEvaluateOffset_NoTraceForPeer_ReturnsMissNotInTrace()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 100.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissNotInTrace, status);
        }

        [Fact]
        public void TryEvaluateOffset_OutsideWindow_ReturnsMissOutsideWindow()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(1, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 50.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissOutsideWindow, status);
        }

        [Fact]
        public void TryEvaluateOffset_MidWindow_HitFullMagnitude()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(7, 8, 9));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out Vector3d offset, out CoBubbleBlendStatus status, out string primary);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.Hit, status);
            Assert.Equal("primary-B", primary);
            Assert.Equal(7.0, offset.x, 5);
            Assert.Equal(8.0, offset.y, 5);
            Assert.Equal(9.0, offset.z, 5);
        }

        [Fact]
        public void TryEvaluateOffset_InCrossfadeTail_HitCrossfadeRamp()
        {
            // Crossfade duration = 1.5s. At endUT - 0.5s the blend factor
            // is 0.5/1.5 = 0.333 of the full magnitude.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(3, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 109.5, out Vector3d offset, out CoBubbleBlendStatus status, out _);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.HitCrossfade, status);
            // 0.5 / 1.5 = 1/3 of the full 3.0 = 1.0 (with float32 trace
            // values and double Vector3d, exact).
            Assert.InRange(offset.x, 0.99, 1.01);
        }

        [Fact]
        public void TryEvaluateOffset_PastWindowEnd_ReturnsMissCrossfadeOut()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(3, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            // 1 second past endUT but still inside endUT + crossfade (1.5s)
            // returns the crossfade ramp for the in-window portion of the
            // trace. Past endUT entirely → MissCrossfadeOut.
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 110.5, out Vector3d offset, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissCrossfadeOut, status);
            Assert.Equal(0.0, offset.x);
            Assert.Equal(0.0, offset.y);
            Assert.Equal(0.0, offset.z);
        }

        // ---------- CoBubblePrimarySelector tests ----------

        [Fact]
        public void PrimarySelection_LiveAlwaysWins()
        {
            // A is live-anchored, B is not. A must be primary regardless of
            // ordering or DAG ancestry.
            var rA = new Recording { RecordingId = "rec-A" };
            var rB = new Recording { RecordingId = "rec-B" };
            // Seed a LiveSeparation anchor on A's section 0, side Start.
            // Use the test seam so we don't need a full RebuildFromMarker.
            var ac = new AnchorCorrection("rec-A", 0, AnchorSide.Start, 100.0,
                new Vector3d(0, 0, 0), AnchorSource.LiveSeparation);
            // RenderSessionState.PutAnchorForTesting is the seed entry point.
            RenderSessionState.PutAnchorForTesting(ac);
            // Add a co-bubble trace so the pair is actually scored.
            SectionAnnotationStore.PutCoBubbleTrace("rec-A",
                MakeTrace("rec-B", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-B",
                MakeTrace("rec-A", 100.0, 110.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rA, rB }, marker: null);
            // Expect peer "rec-B" → primary "rec-A".
            Assert.True(map.ContainsKey("rec-B"));
            Assert.Equal("rec-A", map["rec-B"]);
        }

        [Fact]
        public void PrimarySelection_StableTieBreaker_LowerOrdinalIdWins()
        {
            // No live anchors, no DAG, equal StartUT, equal sample rate;
            // tie breaks by ordinal recording-id (HR-3).
            var rA = new Recording { RecordingId = "rec-A" };
            var rB = new Recording { RecordingId = "rec-B" };
            SectionAnnotationStore.PutCoBubbleTrace("rec-A",
                MakeTrace("rec-B", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-B",
                MakeTrace("rec-A", 100.0, 110.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rA, rB }, marker: null);
            Assert.Equal("rec-A", map["rec-B"]);
        }

        [Fact]
        public void PrimarySelection_DeterministicAcrossRebuilds()
        {
            // Same inputs twice → same primary map.
            var rA = new Recording { RecordingId = "rec-A" };
            var rB = new Recording { RecordingId = "rec-B" };
            SectionAnnotationStore.PutCoBubbleTrace("rec-A",
                MakeTrace("rec-B", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-B",
                MakeTrace("rec-A", 100.0, 110.0, new Vector3d(-1, 0, 0)));
            Dictionary<string, string> first = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rA, rB }, marker: null);
            Dictionary<string, string> second = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rB, rA }, marker: null);
            Assert.Equal(first.Count, second.Count);
            foreach (var kv in first)
                Assert.Equal(kv.Value, second[kv.Key]);
        }

        [Fact]
        public void PrimarySelection_MarkerActiveReFlyId_TreatedAsLive()
        {
            // The marker's active re-fly id is treated as live even if no
            // LiveSeparation anchor seed exists yet (covers the
            // first-frame-of-rebuild case before the anchor ε is computed).
            var rA = new Recording { RecordingId = "rec-A" };
            var rB = new Recording { RecordingId = "rec-B" };
            SectionAnnotationStore.PutCoBubbleTrace("rec-A",
                MakeTrace("rec-B", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-B",
                MakeTrace("rec-A", 100.0, 110.0, new Vector3d(-1, 0, 0)));
            var marker = new ReFlySessionMarker
            {
                SessionId = "sess",
                ActiveReFlyRecordingId = "rec-B",
                OriginChildRecordingId = "rec-B"
            };
            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rA, rB }, marker);
            Assert.Equal("rec-B", map["rec-A"]);
        }

        [Fact]
        public void Recursion_PrimariesAreGuarded()
        {
            // Even if a primary somehow has its own trace stored, the
            // blender's IsPrimary recursion guard short-circuits the call
            // before sampling. This covers the multi-tier formation case.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-X", "primary-Y");
            // Stash a trace for primary-Y too (as if it were a peer of
            // some upstream third recording in a different pair).
            SectionAnnotationStore.PutCoBubbleTrace("primary-Y",
                MakeTrace("upstream-Z", 100.0, 110.0, new Vector3d(99, 0, 0)));
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "primary-Y", 105.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissRecursionGuard, status);
        }
    }
}
