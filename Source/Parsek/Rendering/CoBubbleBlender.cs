// [ERS-exempt] CoBubbleBlender.ResolveLivePeerRecording (P2-B) reads
// RecordingStore.CommittedRecordings to re-validate the trace's peer
// epoch / format version against the live peer at every TryEvaluateOffset
// call. ERS would filter the active NotCommitted provisional re-fly
// target out, masking peer drift during the very session whose
// supersede commit caused the drift. Read-only against trajectory
// metadata. See scripts/ers-els-audit-allowlist.txt for rationale.
using System;
using System.Collections.Generic;
using System.Globalization;
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
        // P1-A: FrameTag=1 trace's BodyName cannot be resolved against
        // FlightGlobals.Bodies (e.g. mod removed body, system definition
        // changed). Standalone fall-through is correct — the inertial
        // lower would silently produce a wrong offset otherwise.
        MissBodyResolveFailed = 11,
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

        /// <summary>Test seam for live peer-recording lookup used by the
        /// runtime per-trace peer validation (P2-B). Production walks
        /// <see cref="RecordingStore.CommittedRecordings"/>.</summary>
        internal static System.Func<string, Recording> PeerRecordingResolverForTesting;

        internal static void ResetForTesting()
        {
            FrameTagOverrideForTesting = null;
            BodyResolverForTesting = null;
            PeerRecordingResolverForTesting = null;
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

            // P2-B: runtime per-trace peer validation. Re-check the cheap
            // O(1) fields (format version, sidecar epoch) against the live
            // peer recording before applying the offset. Mid-session peer
            // drift (e.g., supersede commit, crew swap that bumps epoch)
            // would otherwise feed stale offsets into the renderer. The
            // SHA-256 content recompute stays load-time-only — too expensive
            // for a per-frame path. The status enum and dedup'd Info log
            // satisfy §19.2 Stage 5 "Per-trace co-bubble invalidation".
            Recording livePeer = ResolveLivePeerRecording(primaryRecordingId);
            if (livePeer != null)
            {
                if (livePeer.RecordingFormatVersion != match.PeerSourceFormatVersion)
                {
                    status = CoBubbleBlendStatus.MissPeerValidationFailed;
                    RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "peer-format-changed");
                    ParsekLog.Info("Pipeline-CoBubble", string.Format(CultureInfo.InvariantCulture,
                        "Per-trace co-bubble invalidation: peer={0} primary={1} reason=peer-format-changed expected={2} actual={3}",
                        peerRecordingId, primaryRecordingId,
                        match.PeerSourceFormatVersion, livePeer.RecordingFormatVersion));
                    return false;
                }
                if (livePeer.SidecarEpoch != match.PeerSidecarEpoch)
                {
                    status = CoBubbleBlendStatus.MissPeerValidationFailed;
                    RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "peer-epoch-changed");
                    ParsekLog.Info("Pipeline-CoBubble", string.Format(CultureInfo.InvariantCulture,
                        "Per-trace co-bubble invalidation: peer={0} primary={1} reason=peer-epoch-changed expected={2} actual={3}",
                        peerRecordingId, primaryRecordingId,
                        match.PeerSidecarEpoch, livePeer.SidecarEpoch));
                    return false;
                }
            }

            // P2-C: emit window-enter Info on the FIRST hit per (peer,
            // primary, startUT) regardless of whether it lands in the
            // crossfade tail. Previously the enter notify was inside the
            // non-crossfade else-branch only — windows whose first sample
            // happened to fall past the crossfade boundary never logged.
            // The dedup set in RenderSessionState already gates against
            // re-emission when subsequent samples enter the steady region.
            RenderSessionState.NotifyCoBubbleWindowEnter(peerRecordingId, primaryRecordingId, match.StartUT);

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
                // P1-A: resolve body via the trace's persisted BodyName.
                // The prior production fallback returned null and the lower
                // silently degraded to a no-op — every FrameTag=1 trace
                // bypassed the inertial→world rotation.
                CelestialBody body = ResolveBodyForOffset(match);
                if (object.ReferenceEquals(body, null))
                {
                    status = CoBubbleBlendStatus.MissBodyResolveFailed;
                    RenderSessionState.NotifyCoBubbleTraceMiss(peerRecordingId, "body-resolve-failed");
                    ParsekLog.VerboseRateLimited("Pipeline-CoBubble", "body-resolve-failed",
                        string.Format(CultureInfo.InvariantCulture,
                            "body-resolve-failed: peer={0} primary={1} body={2} ut={3}",
                            peerRecordingId, primaryRecordingId, match.BodyName ?? "<null>",
                            ut.ToString("R", CultureInfo.InvariantCulture)),
                        5.0);
                    return false;
                }
                worldFrameOffset = TrajectoryMath.FrameTransform.LowerOffsetFromInertialToWorld(
                    primaryFrameOffset, body, ut);
            }

            worldOffset = worldFrameOffset * blend;
            status = isCrossfade ? CoBubbleBlendStatus.HitCrossfade : CoBubbleBlendStatus.Hit;

            // P2-D: per §19.2 Stage 5, log every crossfade frame at Verbose
            // (rate-limited so a long crossfade tail can't flood). One key
            // shared across (peer, primary) so high-cadence playback shows
            // the blend factor decaying without per-frame spam.
            if (isCrossfade)
            {
                ParsekLog.VerboseRateLimited("Pipeline-CoBubble", "cobubble-crossfade",
                    string.Format(CultureInfo.InvariantCulture,
                        "crossfade peer={0} primary={1} blend={2:F3}",
                        peerRecordingId, primaryRecordingId, blend),
                    5.0);
            }
            return true;
        }

        /// <summary>
        /// P2-B: live peer recording lookup used by the per-trace runtime
        /// validation in <see cref="TryEvaluateOffset"/>. Test seam-first
        /// pattern (xUnit injects a deterministic mapping); production
        /// walks <see cref="RecordingStore.CommittedRecordings"/>. ERS-exempt
        /// (mirrors <see cref="RenderSessionState"/>'s rationale: the blender
        /// must see the peer's freshness even when ERS would filter it).
        /// </summary>
        private static Recording ResolveLivePeerRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            var seam = PeerRecordingResolverForTesting;
            if (seam != null) return seam(recordingId);
            try
            {
                IReadOnlyList<Recording> committed = RecordingStore.CommittedRecordings;
                if (committed != null)
                {
                    for (int i = 0; i < committed.Count; i++)
                    {
                        Recording r = committed[i];
                        if (r == null) continue;
                        if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                            return r;
                    }
                }
            }
            catch
            {
                // Mid-load mutation — fall through; the validation path
                // treats null as "no live peer" and skips the check. The
                // stale trace then drives the blend; a subsequent rebuild
                // (which captures the new epoch) will install a fresh map.
            }
            return null;
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
            // P1-A: the trace now persists BodyName at detect-time so the
            // production runtime can resolve the body for the inertial→world
            // rotation lower. The test seam still wins (xUnit cannot stand
            // up CelestialBody instances via FlightGlobals); production
            // walks FlightGlobals.Bodies by ordinal name match. ReferenceEquals
            // is used to bypass Unity's overloaded `==` so a TestBodyRegistry-
            // built CelestialBody (Unity-cached pointer zero) is treated as
            // a real reference.
            string bodyName = trace?.BodyName;
            var seam = BodyResolverForTesting;
            if (seam != null) return seam(bodyName ?? string.Empty);
            if (string.IsNullOrEmpty(bodyName)) return null;
            try
            {
                var bodies = FlightGlobals.Bodies;
                if (bodies == null) return null;
                CelestialBody match = bodies.Find(b =>
                    !object.ReferenceEquals(b, null)
                    && string.Equals(b.bodyName, bodyName, StringComparison.Ordinal));
                if (object.ReferenceEquals(match, null)) return null;
                return match;
            }
            catch
            {
                // FlightGlobals can throw mid-scene-transition (lookup
                // walks an unloaded body database); treat as missing so
                // the caller surfaces MissBodyResolveFailed and falls back
                // to standalone.
                return null;
            }
        }
    }
}
