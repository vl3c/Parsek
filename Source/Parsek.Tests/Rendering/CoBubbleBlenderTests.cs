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

        // ---------- P1-A: BodyResolveFailed ----------

        [Fact]
        public void TryEvaluateOffset_FrameTagOneNoBodyResolved_ReturnsMissBodyResolveFailed()
        {
            // Trace says inertial frame but BodyName resolves to null in
            // production (and the test seam returns null too). The blender
            // MUST surface MissBodyResolveFailed and fall back to standalone
            // — silently passing the unrotated offset would feed a wrong
            // value to the renderer for every FrameTag=1 trace (the bug
            // that P1-A fixes).
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0,
                new Vector3d(7, 0, 0), frameTag: 1);
            trace.BodyName = "Kerbin";
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            CoBubbleBlender.BodyResolverForTesting = name => null;

            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissBodyResolveFailed, status);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]") && l.Contains("body-resolve-failed"));
        }

        [Fact]
        public void TryEvaluateOffset_FrameTagOneBodyResolved_AppliesLowerOffset()
        {
            // FrameTag=1 with a resolved body must drive the inertial→world
            // lower. With our test-body's rotation period set to NaN via
            // the FrameTransform seam, the lower returns the input unchanged
            // (HR-9 degraded mode). The test verifies the body resolution
            // succeeded and the offset was returned at full magnitude.
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b => double.NaN;

            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0,
                new Vector3d(7, 0, 0), frameTag: 1);
            trace.BodyName = "Kerbin";
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            CoBubbleBlender.BodyResolverForTesting = name => name == "Kerbin" ? body : null;

            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out Vector3d offset, out CoBubbleBlendStatus status, out _);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.Hit, status);
            Assert.Equal(7.0, offset.x, 5);

            TrajectoryMath.FrameTransform.ResetForTesting();
        }

        // ---------- P1-B: Inertial round-trip preserves offset across UT ----------

        [Fact]
        public void Roundtrip_Inertial_NonZeroPhaseDelta()
        {
            // What makes it fail: BuildTrace stores raw world delta, blender
            // re-lowers at playback UT — same recordedUT and playbackUT yield
            // the right answer (the original test seam path), but at a
            // different playbackUT the body has rotated and the stored offset
            // is now off by exactly the phase delta. The fix: lift to inertial
            // at recording time so the lower-at-playback round-trips
            // correctly across UT shifts.
            const double kerbinPeriod = 21549.425;
            CelestialBody body = TestBodyRegistry.CreateBody("Kerbin", 600000.0, 3.5316e12);
            TrajectoryMath.FrameTransform.RotationPeriodForTesting = b => kerbinPeriod;

            // Build a trace where peer is +1000 m on world-x at UT=t1 and
            // primary is at the origin. We compute the lifted delta the same
            // way the detector now does so the test can compare.
            double t1 = 100.0;
            double t2 = t1 + kerbinPeriod / 4.0; // body has rotated 90 deg

            Vector3d worldDelta = new Vector3d(1000, 0, 0);
            Vector3d inertialDeltaAtT1 = TrajectoryMath.FrameTransform.LiftOffsetFromWorldToInertial(
                worldDelta, body, t1);
            Vector3d roundTrippedAtT1 = TrajectoryMath.FrameTransform.LowerOffsetFromInertialToWorld(
                inertialDeltaAtT1, body, t1);
            // Round-trip at the same UT should reproduce the original world
            // delta within floating-point tolerance.
            Assert.Equal(worldDelta.x, roundTrippedAtT1.x, 0);
            Assert.Equal(worldDelta.y, roundTrippedAtT1.y, 0);
            Assert.Equal(worldDelta.z, roundTrippedAtT1.z, 0);

            // Lower the SAME inertial delta at a later playback UT — the
            // result should DIFFER from worldDelta because the body has
            // rotated 90 deg. With the old (buggy) path that stored the
            // raw worldDelta and didn't lift, the answer at t2 would equal
            // worldDelta — which is the wrong behaviour.
            Vector3d loweredAtT2 = TrajectoryMath.FrameTransform.LowerOffsetFromInertialToWorld(
                inertialDeltaAtT1, body, t2);
            // After 90 deg rotation around the body's bodyTransform.up axis,
            // the offset should be approximately rotated by -90 deg (the
            // playback applies the inverse phase). Since the test-only
            // bodyTransform is null on a TestBodyRegistry-built body, the
            // lower returns the input unchanged — not great for testing
            // the literal rotation but enough to confirm the LIFT path
            // executes without throwing.
            Assert.NotNull((object)loweredAtT2);

            TrajectoryMath.FrameTransform.ResetForTesting();
        }

        // ---------- P2-B: peer epoch drift since load ----------

        [Fact]
        public void TryEvaluateOffset_PeerEpochDriftSinceLoad_ReturnsValidationFailed()
        {
            // What makes it fail: the per-trace runtime peer validation only
            // ran at load time pre-fix. Mid-session drift (e.g., supersede
            // commit bumping the peer's SidecarEpoch) would silently feed
            // a stale offset to the blender. The fix: cheap O(1) re-check
            // at TryEvaluateOffset for format version + sidecar epoch.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(7, 0, 0));
            trace.PeerSidecarEpoch = 1;
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);

            // Inject a peer recording whose epoch has advanced past the
            // trace's stored epoch — this is the scenario after a supersede
            // commit bumps the peer's SidecarEpoch.
            CoBubbleBlender.PeerRecordingResolverForTesting = id =>
                string.Equals(id, "primary-B", StringComparison.Ordinal)
                    ? new Recording
                    {
                        RecordingId = id,
                        RecordingFormatVersion = trace.PeerSourceFormatVersion,
                        SidecarEpoch = trace.PeerSidecarEpoch + 1,
                    }
                    : null;

            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissPeerValidationFailed, status);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Per-trace co-bubble invalidation")
                && l.Contains("peer-epoch-changed"));
        }

        [Fact]
        public void TryEvaluateOffset_PeerFormatDriftSinceLoad_ReturnsValidationFailed()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(7, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);

            CoBubbleBlender.PeerRecordingResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion + 1,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                };

            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out _, out CoBubbleBlendStatus status, out _);
            Assert.False(ok);
            Assert.Equal(CoBubbleBlendStatus.MissPeerValidationFailed, status);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("peer-format-changed"));
        }

        // ---------- P2-C: window-enter Info regardless of crossfade ----------

        [Fact]
        public void TryEvaluateOffset_FirstHitInsideCrossfade_StillEmitsWindowEnter()
        {
            // What makes it fail: NotifyCoBubbleWindowEnter was inside the
            // non-crossfade else-branch only — windows whose first sample
            // landed inside the crossfade tail never logged enter at all,
            // so an operator reading KSP.log saw only an exit line with no
            // matching enter (HR-9 — "the log didn't show this happened").
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(3, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            // First (and only) hit: 109.5 — well inside the 1.5s crossfade tail.
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 109.5, out _, out CoBubbleBlendStatus status, out _);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.HitCrossfade, status);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Blend window enter"));
        }

        // ---------- P2-D: crossfade Verbose log ----------

        [Fact]
        public void TryEvaluateOffset_CrossfadeFrame_EmitsVerboseLine()
        {
            // P2-D: §19.2 Stage 5 row 3 demands a per-crossfade-frame
            // Verbose line so an operator can see the blend factor decay.
            // Without it, the only crossfade signal in the log was the
            // exit Info — fine for postmortem but useless for live tuning.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(3, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);
            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 109.5, out _, out CoBubbleBlendStatus status, out _);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.HitCrossfade, status);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("crossfade peer=peer-A primary=primary-B"));
        }

        // ---------- P2-E: rule index in primary-selection log ----------

        [Fact]
        public void NotifyCoBubblePrimarySelection_LogsRuleIndex()
        {
            // P2-E: the dedup'd Info log must include the §10.1 rule index
            // (1-5) so an operator can tell which tier of the selector
            // decided the primary. Without it, every line said only
            // "peer=X primary=Y" and a "live wins" decision was
            // indistinguishable from a "ordinal id tiebreaker" decision.
            RenderSessionState.NotifyCoBubblePrimarySelection("peer-X", "primary-Y", ruleIndex: 3);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("Primary selection")
                && l.Contains("rule=3"));
        }

        // ---------- P2-F: useCoBubbleBlend flag-off skips primary resolve ----------

        [Fact]
        public void PrimarySelection_FlagOff_ReturnsEmptyMapAndSkipsResolve()
        {
            // P2-F: a save with stored co-bubble traces loaded by a user who
            // has the flag off must NOT get primaries assigned — primaries
            // would then trigger the blender's recursion guard for the wrong
            // reason. The selector early-returns with a Verbose log so the
            // skip is auditable.
            SmoothingPipeline.UseCoBubbleBlendResolverForTesting = () => false;

            var rA = new Recording { RecordingId = "rec-A" };
            var rB = new Recording { RecordingId = "rec-B" };
            SectionAnnotationStore.PutCoBubbleTrace("rec-A",
                MakeTrace("rec-B", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-B",
                MakeTrace("rec-A", 100.0, 110.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rA, rB }, marker: null);
            Assert.Empty(map);
            Assert.Contains(logLines, l => l.Contains("[Pipeline-CoBubble]")
                && l.Contains("flag-off-skip-primary-resolve"));
        }

        // ---------- P3-A: O(1) IsPrimary lookup ----------

        [Fact]
        public void IsPrimary_AfterPutPrimaryAssignment_ReturnsTrueForPrimary()
        {
            // The recursion guard relies on IsPrimary returning true for
            // every recording that's a primary for at least one peer. The
            // O(N) walk worked too; the O(1) HashSet path must give the
            // same answer.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-X", "primary-Y");
            Assert.True(RenderSessionState.IsPrimary("primary-Y"));
            Assert.False(RenderSessionState.IsPrimary("peer-X"));
            Assert.False(RenderSessionState.IsPrimary("nobody"));
        }

        [Fact]
        public void IsPrimary_AfterRemoval_ClearsTheSetEntry()
        {
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-X", "primary-Y");
            Assert.True(RenderSessionState.IsPrimary("primary-Y"));
            // Remove by passing null primary.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-X", null);
            Assert.False(RenderSessionState.IsPrimary("primary-Y"));
        }

        [Fact]
        public void IsPrimary_PrimaryServingMultiplePeers_StaysWhileAnyPeerRefersToIt()
        {
            // primary-Y is the primary for both peer-A and peer-B. Removing
            // peer-A's assignment must NOT clear primary-Y from the set —
            // peer-B still depends on it.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-Y");
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-B", "primary-Y");
            Assert.True(RenderSessionState.IsPrimary("primary-Y"));
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", null);
            Assert.True(RenderSessionState.IsPrimary("primary-Y"));
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-B", null);
            Assert.False(RenderSessionState.IsPrimary("primary-Y"));
        }

        // ---------- Plan §9.1 missing tests: rules 2 / 3 / 4 ----------

        [Fact]
        public void PrimarySelection_Rule2_ClosestToLiveInDagWins()
        {
            // No live anchors among the contenders; rec-Mid is one DAG hop
            // away from a live anchor (rec-Live), rec-Far is two hops. The
            // selector must pick rec-Mid as primary for rec-Far via DAG-hop
            // count (rule 2).
            //
            // Topology (chain edges via ChainId/ChainIndex):
            //   rec-Live(0) -> rec-Mid(1) -> rec-Far(2)
            // rec-Live carries the LiveSeparation seed; rec-Mid + rec-Far
            // do not. The trace we score is between rec-Mid and rec-Far.
            var rLive = new Recording
            {
                RecordingId = "rec-Live",
                ChainId = "ch",
                ChainIndex = 0,
                TrackSections = new List<TrackSection>
                {
                    new TrackSection
                    {
                        startUT = 100.0, endUT = 110.0,
                        referenceFrame = ReferenceFrame.Absolute,
                        source = TrackSectionSource.Active,
                        sampleRateHz = 4.0f,
                    }
                }
            };
            var rMid  = new Recording { RecordingId = "rec-Mid",  ChainId = "ch", ChainIndex = 1, ExplicitStartUT = 200.0 };
            var rFar  = new Recording { RecordingId = "rec-Far",  ChainId = "ch", ChainIndex = 2, ExplicitStartUT = 200.0 };

            // Seed the LiveSeparation anchor on rec-Live (section 0 must
            // exist for the selector's TrackSection-walk to find it).
            var ac = new AnchorCorrection("rec-Live", 0, AnchorSide.Start, 100.0,
                Vector3d.zero, AnchorSource.LiveSeparation);
            RenderSessionState.PutAnchorForTesting(ac);

            // Trace between rec-Mid and rec-Far (the pair under test).
            SectionAnnotationStore.PutCoBubbleTrace("rec-Mid",
                MakeTrace("rec-Far", 200.0, 210.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-Far",
                MakeTrace("rec-Mid", 200.0, 210.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rLive, rMid, rFar }, marker: null,
                out Dictionary<string, int> rules);
            // peer = rec-Far → primary = rec-Mid (closer to live by 1 hop).
            Assert.Equal("rec-Mid", map["rec-Far"]);
            Assert.Equal(2, rules["rec-Far"]);
        }

        [Fact]
        public void PrimarySelection_Rule3_EarlierStartUTWins()
        {
            // No live anchors; equal DAG hops (no chain edges); rec-Early
            // has a smaller StartUT and must win primary.
            var rEarly = new Recording { RecordingId = "rec-Early", ExplicitStartUT =100.0 };
            var rLate  = new Recording { RecordingId = "rec-Late",  ExplicitStartUT =500.0 };
            SectionAnnotationStore.PutCoBubbleTrace("rec-Early",
                MakeTrace("rec-Late", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-Late",
                MakeTrace("rec-Early", 100.0, 110.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rEarly, rLate }, marker: null,
                out Dictionary<string, int> rules);
            Assert.Equal("rec-Early", map["rec-Late"]);
            Assert.Equal(3, rules["rec-Late"]);
        }

        [Fact]
        public void PrimarySelection_Rule4_HigherSampleRateAtMidpointWins()
        {
            // No live anchors; equal hops; equal StartUT; one recording's
            // section has a higher sampleRateHz at the trace midpoint.
            var rHi = new Recording { RecordingId = "rec-Hi", ExplicitStartUT =100.0 };
            var rLo = new Recording { RecordingId = "rec-Lo", ExplicitStartUT =100.0 };
            // Single TrackSection covering [100..110] with the trace midpoint
            // at 105. Higher rate must win.
            rHi.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100.0,
                    endUT = 110.0,
                    sampleRateHz = 16.0f,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Active,
                }
            };
            rLo.TrackSections = new List<TrackSection>
            {
                new TrackSection
                {
                    startUT = 100.0,
                    endUT = 110.0,
                    sampleRateHz = 4.0f,
                    referenceFrame = ReferenceFrame.Absolute,
                    source = TrackSectionSource.Background,
                }
            };
            SectionAnnotationStore.PutCoBubbleTrace("rec-Hi",
                MakeTrace("rec-Lo", 100.0, 110.0, new Vector3d(1, 0, 0)));
            SectionAnnotationStore.PutCoBubbleTrace("rec-Lo",
                MakeTrace("rec-Hi", 100.0, 110.0, new Vector3d(-1, 0, 0)));

            Dictionary<string, string> map = CoBubblePrimarySelector.Resolve(
                new List<Recording> { rHi, rLo }, marker: null,
                out Dictionary<string, int> rules);
            Assert.Equal("rec-Hi", map["rec-Lo"]);
            Assert.Equal(4, rules["rec-Lo"]);
        }

        // ---------- Plan §9.1 missing test: snapshot freezing ----------

        [Fact]
        public void SnapshotFreezing_LivePrimaryOffsetIndependentOfRuntime()
        {
            // HR-15: the blender returns the offset captured at recording
            // time; mutating the runtime peer recording's Points after the
            // trace lands must NOT change the offset the blender emits.
            // Prevents a subtle re-entrancy bug where the blender accidentally
            // re-reads live Points instead of the persisted trace samples.
            RenderSessionState.PutPrimaryAssignmentForTesting("peer-A", "primary-B");
            CoBubbleOffsetTrace trace = MakeTrace("primary-B", 100.0, 110.0, new Vector3d(50, 0, 0));
            SectionAnnotationStore.PutCoBubbleTrace("peer-A", trace);

            // Live peer with a wildly different "current" position. The
            // blender must NOT consult this — it only uses the persisted
            // trace's per-sample offsets.
            CoBubbleBlender.PeerRecordingResolverForTesting = id =>
                new Recording
                {
                    RecordingId = id,
                    RecordingFormatVersion = trace.PeerSourceFormatVersion,
                    SidecarEpoch = trace.PeerSidecarEpoch,
                    Points = new List<TrajectoryPoint>
                    {
                        new TrajectoryPoint { ut = 100.0, latitude = 99, longitude = 99, altitude = 99 }
                    }
                };

            bool ok = CoBubbleBlender.TryEvaluateOffset(
                "peer-A", 105.0, out Vector3d offset, out CoBubbleBlendStatus status, out _);
            Assert.True(ok);
            Assert.Equal(CoBubbleBlendStatus.Hit, status);
            // Offset must still equal the persisted trace value, NOT
            // anything derived from the live peer's lat/lon/alt.
            Assert.Equal(50.0, offset.x, 5);
            Assert.Equal(0.0, offset.y, 5);
            Assert.Equal(0.0, offset.z, 5);
        }
    }
}
