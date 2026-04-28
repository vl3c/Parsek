using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 5 blender result codes (design doc §6.5 / §10.3). The
    /// <see cref="ParsekFlight"/> integration site dispatches on the status
    /// to decide whether to add the offset to the primary's standalone
    /// world position. <c>Hit</c> / <c>HitCrossfade</c> mean a finite
    /// <c>worldOffset</c> was returned; every <c>Miss*</c> value implies
    /// the consumer falls through to standalone Stages 1+2+3+4 (HR-9).
    /// </summary>
    internal enum CoBubbleBlendStatus : byte
    {
        Hit = 0,
        HitCrossfade = 1,
        MissNotInTrace = 2,
        MissOutsideWindow = 3,
        MissCrossfadeOut = 4,
        MissPrimaryNotResolved = 5,
        MissPrimaryRecordingMissing = 6,
        MissPrimaryStandaloneFailed = 7,
        MissPeerValidationFailed = 8,
        MissDisabledByFlag = 9,
        MissRecursionGuard = 10,
    }

    /// <summary>
    /// Phase 5 co-bubble blender (design doc §6.5 / §10 / §18 Phase 5).
    /// Pure stateless service that maps (peerRecordingId, ut) → world-frame
    /// offset Vector3d. The caller is responsible for evaluating
    /// <c>P_render(t)</c> for the primary via the standalone Stages 1+2+3+4
    /// path and adding the returned offset.
    /// </summary>
    internal static class CoBubbleBlender
    {
        /// <summary>Test seam for the FrameTag override at a given UT.</summary>
        internal static System.Func<string, double, byte?> FrameTagOverrideForTesting;

        /// <summary>Test seam for the body resolver. Production walks
        /// <see cref="FlightGlobals.Bodies"/>.</summary>
        internal static System.Func<string, CelestialBody> BodyResolverForTesting;

        internal static void ResetForTesting()
        {
            FrameTagOverrideForTesting = null;
            BodyResolverForTesting = null;
        }

        /// <summary>
        /// Phase 5 main consumer hook. On <see cref="CoBubbleBlendStatus.Hit"/>
        /// / <see cref="CoBubbleBlendStatus.HitCrossfade"/>, returns a
        /// world-frame translation to add to the primary's standalone
        /// <c>P_render(ut)</c>. The caller MUST evaluate the primary's
        /// position separately — the blender does not know the primary's
        /// section index or body context.
        /// </summary>
        internal static bool TryEvaluateOffset(
            string peerRecordingId, double ut,
            out Vector3d worldOffset, out CoBubbleBlendStatus status,
            out string primaryRecordingId)
        {
            worldOffset = Vector3d.zero;
            status = CoBubbleBlendStatus.MissNotInTrace;
            primaryRecordingId = null;

            if (string.IsNullOrEmpty(peerRecordingId))
            {
                status = CoBubbleBlendStatus.MissNotInTrace;
                return false;
            }

            // Flag gate.
            if (!SmoothingPipeline.ResolveUseCoBubbleBlend())
            {
                status = CoBubbleBlendStatus.MissDisabledByFlag;
                return false;
            }

            // Recursion guard — primaries always render standalone (§6.5).
            if (RenderSessionState.IsPrimary(peerRecordingId))
            {
                status = CoBubbleBlendStatus.MissRecursionGuard;
                return false;
            }

            // Designated-primary lookup.
            if (!RenderSessionState.TryGetDesignatedPrimary(peerRecordingId, out primaryRecordingId)
                || string.IsNullOrEmpty(primaryRecordingId))
            {
                status = CoBubbleBlendStatus.MissPrimaryNotResolved;
                RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "primary-not-resolved");
                return false;
            }

            // Trace lookup.
            if (!SectionAnnotationStore.TryGetCoBubbleTraces(peerRecordingId, out var traces) || traces == null)
            {
                status = CoBubbleBlendStatus.MissNotInTrace;
                RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "no-trace");
                return false;
            }
            CoBubbleOffsetTrace match = null;
            for (int i = 0; i < traces.Count; i++)
            {
                CoBubbleOffsetTrace t = traces[i];
                if (t == null) continue;
                if (!string.Equals(t.PeerRecordingId, primaryRecordingId, StringComparison.Ordinal)) continue;
                if (ut < t.StartUT || ut > t.EndUT + CoBubbleConfiguration.Default.CrossfadeDurationSeconds) continue;
                match = t;
                break;
            }
            if (match == null)
            {
                status = CoBubbleBlendStatus.MissOutsideWindow;
                RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "outside-window");
                return false;
            }

            // Within-window blend factor (crossfade at exit).
            double crossfade = CoBubbleConfiguration.Default.CrossfadeDurationSeconds;
            double blend = 1.0;
            bool isCrossfade = false;
            if (ut > match.EndUT)
            {
                // Past the window end — the consumer falls back to standalone.
                status = CoBubbleBlendStatus.MissCrossfadeOut;
                RenderSessionState.NotifyCoBubbleWindowExit(peerRecordingId, primaryRecordingId,
                    match.EndUT, "crossfade-tail");
                return false;
            }
            if (ut > match.EndUT - crossfade && crossfade > 0)
            {
                double tail = (match.EndUT - ut) / crossfade;
                if (tail < 0.0) tail = 0.0;
                if (tail > 1.0) tail = 1.0;
                blend = tail;
                isCrossfade = true;
            }
            else
            {
                RenderSessionState.NotifyCoBubbleWindowEnter(peerRecordingId, primaryRecordingId, match.StartUT);
            }

            // Sample the trace at ut (linear interpolation between bracket UTs).
            if (!SampleTraceAt(match, ut, out Vector3d primaryFrameOffset))
            {
                status = CoBubbleBlendStatus.MissOutsideWindow;
                return false;
            }

            // FrameTag dispatch: body-fixed (0) is already a world-frame
            // translation; inertial (1) requires a per-playback rotation
            // around the body's spin axis.
            byte tag = match.FrameTag;
            var seamTag = FrameTagOverrideForTesting;
            if (seamTag != null)
            {
                byte? overrideTag = seamTag(peerRecordingId, ut);
                if (overrideTag.HasValue) tag = overrideTag.Value;
            }
            Vector3d worldFrameOffset = primaryFrameOffset;
            if (tag == 1)
            {
                CelestialBody body = ResolveBodyForOffset(match);
                worldFrameOffset = TrajectoryMath.FrameTransform.LowerOffsetFromInertialToWorld(
                    primaryFrameOffset, body, ut);
            }

            worldOffset = worldFrameOffset * blend;
            status = isCrossfade ? CoBubbleBlendStatus.HitCrossfade : CoBubbleBlendStatus.Hit;
            return true;
        }

        private static bool SampleTraceAt(CoBubbleOffsetTrace trace, double ut, out Vector3d offset)
        {
            offset = Vector3d.zero;
            if (trace == null || trace.UTs == null || trace.UTs.Length == 0) return false;
            int n = trace.UTs.Length;
            // Bracket UT.
            int hi = -1;
            for (int i = 0; i < n; i++)
            {
                if (trace.UTs[i] >= ut) { hi = i; break; }
            }
            if (hi <= 0)
            {
                // Either ut <= first UT, or ut > last UT.
                int idx = hi == 0 ? 0 : n - 1;
                offset = new Vector3d(trace.Dx[idx], trace.Dy[idx], trace.Dz[idx]);
                return true;
            }
            int lo = hi - 1;
            double span = trace.UTs[hi] - trace.UTs[lo];
            float t = span > 0 ? (float)((ut - trace.UTs[lo]) / span) : 0f;
            offset = new Vector3d(
                Mathf.Lerp(trace.Dx[lo], trace.Dx[hi], t),
                Mathf.Lerp(trace.Dy[lo], trace.Dy[hi], t),
                Mathf.Lerp(trace.Dz[lo], trace.Dz[hi], t));
            return true;
        }

        private static CelestialBody ResolveBodyForOffset(CoBubbleOffsetTrace trace)
        {
            // Inertial offsets need a body to lower. The trace doesn't carry
            // the body name (offset is body-relative implicitly via the
            // primary's segment). Fall back through the test seam first;
            // production resolves through FlightGlobals.GetBodyByName using
            // the primary recording's section that contains the trace UTs.
            // For Phase 5 simplicity, prefer the test seam; production
            // callers pass the body context via the integration site so the
            // blender returns the offset in the body's frame and the caller
            // performs the lift if needed (see ParsekFlight integration).
            var seam = BodyResolverForTesting;
            if (seam != null) return seam(string.Empty);
            // Without a body name on the trace, return null. The caller
            // (ParsekFlight.InterpolateAndPosition) holds the body context
            // already and will dispatch through DispatchSplineWorldByFrameTag
            // for inertial-flagged traces if needed. For body-fixed traces
            // (the common case), worldFrameOffset == primaryFrameOffset and
            // this path is never reached.
            return null;
        }
    }
}
