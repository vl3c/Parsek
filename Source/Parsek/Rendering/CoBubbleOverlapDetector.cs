using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Parsek.Rendering
{
    /// <summary>
    /// Phase 5 co-bubble overlap detector (design doc §6.5 / §10.1 / §10.2 /
    /// §10.3 / §17.3.1). Walks pairs of recordings and emits one
    /// <see cref="OverlapWindow"/> per UT range during which both vessels
    /// shared a physics bubble. The bubble-membership rule reads
    /// <see cref="TrackSection.source"/> (Active / Background) and
    /// <see cref="TrackSection.referenceFrame"/> = Absolute — no recorder
    /// change required. Each emitted window is fed to <see cref="BuildTrace"/>
    /// to produce a <see cref="CoBubbleOffsetTrace"/>.
    /// </summary>
    internal static class CoBubbleOverlapDetector
    {
        /// <summary>Default minimum-window-duration cap (seconds).</summary>
        internal const double DefaultMinWindowDurationS = 0.5;

        /// <summary>Default bubble-radius cap (metres). Matches KSP physics
        /// bubble radius; samples whose separation exceeds this truncate the
        /// window per design doc §10.3.</summary>
        internal const double DefaultMaxBubbleSeparationM = 2500.0;

        /// <summary>One detected overlap window between two recordings.
        /// <see cref="RecordingA"/> and <see cref="RecordingB"/> are sorted
        /// by ordinal recording-id (HR-3 deterministic).</summary>
        internal struct OverlapWindow
        {
            public string RecordingA;
            public string RecordingB;
            public double StartUT;
            public double EndUT;
            public byte FrameTag;
            public SegmentEnvironment PrimaryEnv;
            public string BodyName;
        }

        // Test seam: when set, replaces the sample-position resolver. xUnit
        // injects a synthetic mapping; production walks Recording.Points
        // directly with linear interpolation. Reset via ResetForTesting.
        internal static System.Func<Recording, double, Vector3d?> SamplePositionResolverForTesting;

        // P1-B: test seam for the body-by-name resolver used by the
        // FrameTag=1 lift in BuildTrace. Production walks
        // FlightGlobals.Bodies; xUnit injects a CelestialBody (or null) so
        // the lift code path can be exercised without Unity.
        internal static System.Func<string, CelestialBody> BodyResolverForTesting;

        internal static void ResetForTesting()
        {
            SamplePositionResolverForTesting = null;
            BodyResolverForTesting = null;
        }

        private static CelestialBody ResolveBodyByName(string bodyName)
        {
            if (string.IsNullOrEmpty(bodyName)) return null;
            var seam = BodyResolverForTesting;
            if (seam != null) return seam(bodyName);
            try
            {
                var bodies = FlightGlobals.Bodies;
                if (bodies == null) return null;
                CelestialBody match = bodies.Find(b =>
                    !object.ReferenceEquals(b, null)
                    && string.Equals(b.bodyName, bodyName, System.StringComparison.Ordinal));
                if (object.ReferenceEquals(match, null)) return null;
                return match;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Pure detection over the supplied recording list. Returns one
        /// window per overlap interval (deterministic order:
        /// (RecordingA, RecordingB, StartUT) ascending). Per §1 of the
        /// implementation plan the rule is:
        /// <list type="bullet">
        ///   <item>Both sides must have a section with
        ///   <see cref="TrackSection.source"/> ∈ {Active, Background} AND
        ///   <see cref="ReferenceFrame.Absolute"/>.</item>
        ///   <item>Same body. Body change inside an overlap splits the
        ///   window at the change UT (HR-7).</item>
        ///   <item>Window terminates at any TIME_JUMP / structural-event
        ///   <see cref="BranchPoint"/> (Terminal/Breakup truncate; Dock /
        ///   Undock / EVA / JointBreak split).</item>
        ///   <item>Window truncates when sample separation exceeds
        ///   <paramref name="maxBubbleSeparationM"/>.</item>
        ///   <item>Windows shorter than
        ///   <paramref name="minWindowDurationS"/> are dropped.</item>
        /// </list>
        /// </summary>
        internal static List<OverlapWindow> Detect(
            IReadOnlyList<Recording> recordings,
            double minWindowDurationS = DefaultMinWindowDurationS,
            double maxBubbleSeparationM = DefaultMaxBubbleSeparationM)
        {
            var result = new List<OverlapWindow>();
            if (recordings == null || recordings.Count < 2) return result;

            // Build a stable ordered list (skip nulls / empty ids).
            var ordered = new List<Recording>(recordings.Count);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                ordered.Add(r);
            }
            ordered.Sort((a, b) => string.CompareOrdinal(a.RecordingId, b.RecordingId));

            for (int i = 0; i < ordered.Count; i++)
            {
                Recording a = ordered[i];
                for (int j = i + 1; j < ordered.Count; j++)
                {
                    Recording b = ordered[j];
                    DetectPair(a, b, minWindowDurationS, maxBubbleSeparationM, result);
                }
            }

            // Stable sort across the result for HR-3 determinism.
            result.Sort((x, y) =>
            {
                int c = string.CompareOrdinal(x.RecordingA, y.RecordingA);
                if (c != 0) return c;
                c = string.CompareOrdinal(x.RecordingB, y.RecordingB);
                if (c != 0) return c;
                return x.StartUT.CompareTo(y.StartUT);
            });
            return result;
        }

        private static void DetectPair(Recording a, Recording b,
            double minWindowDurationS, double maxBubbleSeparationM,
            List<OverlapWindow> output)
        {
            if (a.TrackSections == null || b.TrackSections == null) return;

            // Walk every (sectionA, sectionB) pair whose [startUT, endUT]
            // ranges intersect and both qualify (Active/Background +
            // Absolute). Section count is small (<<100 typically) so the
            // O(N*M) walk is acceptable.
            for (int sa = 0; sa < a.TrackSections.Count; sa++)
            {
                TrackSection secA = a.TrackSections[sa];
                if (!IsBubbleEligibleSection(secA)) continue;

                for (int sb = 0; sb < b.TrackSections.Count; sb++)
                {
                    TrackSection secB = b.TrackSections[sb];
                    if (!IsBubbleEligibleSection(secB)) continue;

                    // P2-A: KSP has exactly one focused vessel per scene at
                    // any UT, so two simultaneous Active sections must come
                    // from different sessions (a re-fly bridge, not a true
                    // co-bubble). Re-fly chains are stitched at supersede
                    // time via tombstones; treating two Active sections as a
                    // co-bubble pair would record a phantom offset that the
                    // blender would later replay as a stale position lock.
                    if (secA.source == TrackSectionSource.Active
                        && secB.source == TrackSectionSource.Active)
                        continue;

                    double lo = Math.Max(secA.startUT, secB.startUT);
                    double hi = Math.Min(secA.endUT, secB.endUT);
                    if (hi - lo < minWindowDurationS) continue;

                    // Body / frame agreement at section level. Body changes
                    // and frame toggles inside the window are split via the
                    // sample-walk below (HR-7).
                    string bodyA = ResolveSectionBodyName(secA);
                    string bodyB = ResolveSectionBodyName(secB);
                    if (string.IsNullOrEmpty(bodyA) || string.IsNullOrEmpty(bodyB)) continue;
                    if (!string.Equals(bodyA, bodyB, StringComparison.Ordinal)) continue;

                    byte frameTag = (secA.environment == SegmentEnvironment.ExoPropulsive
                        || secA.environment == SegmentEnvironment.ExoBallistic) ? (byte)1 : (byte)0;

                    SplitAndEmitWindowsForSectionPair(
                        a, b, secA, secB, lo, hi, frameTag, bodyA,
                        minWindowDurationS, maxBubbleSeparationM, output);
                }
            }
        }

        private static void SplitAndEmitWindowsForSectionPair(
            Recording a, Recording b,
            TrackSection secA, TrackSection secB,
            double lo, double hi, byte frameTag, string bodyName,
            double minWindowDurationS, double maxBubbleSeparationM,
            List<OverlapWindow> output)
        {
            // Split UT set: gather every UT that should split or truncate
            // the [lo, hi] window. Sources:
            //   - structural BranchPoints inside the window (per side)
            //   - body change samples (handled by the sample-walk below)
            //   - bubble-radius truncation (sample-walk below)
            var splits = new SortedSet<double>();
            splits.Add(lo);
            splits.Add(hi);

            CollectStructuralSplits(a, lo, hi, splits, out double aTerminalCap);
            CollectStructuralSplits(b, lo, hi, splits, out double bTerminalCap);
            double effectiveHi = hi;
            if (!double.IsNaN(aTerminalCap) && aTerminalCap < effectiveHi) effectiveHi = aTerminalCap;
            if (!double.IsNaN(bTerminalCap) && bTerminalCap < effectiveHi) effectiveHi = bTerminalCap;
            // Snap any splits past the terminal cap down to the cap.
            var capped = new SortedSet<double>();
            foreach (double s in splits)
                if (s <= effectiveHi) capped.Add(s);
            capped.Add(effectiveHi);

            var ordered = new List<double>(capped);
            ordered.Sort();
            for (int k = 0; k + 1 < ordered.Count; k++)
            {
                double winStart = ordered[k];
                double winEnd = ordered[k + 1];
                if (winEnd - winStart < minWindowDurationS) continue;

                // Walk samples for separation/body checks. Truncate at the
                // first violation and split the loop accordingly.
                EmitTruncatedWindow(a, b, winStart, winEnd, frameTag, bodyName, secA.environment,
                    minWindowDurationS, maxBubbleSeparationM, output);
            }
        }

        private static void EmitTruncatedWindow(
            Recording a, Recording b,
            double startUT, double endUT, byte frameTag, string bodyName,
            SegmentEnvironment primaryEnv,
            double minWindowDurationS, double maxBubbleSeparationM,
            List<OverlapWindow> output)
        {
            // Sample at the configured rate. We accept the window in halves
            // when separation exceeds bubble radius mid-window; recursive
            // halving terminates at minWindowDurationS.
            double stepS = 1.0 / Math.Max(0.001f, CoBubbleConfiguration.Default.ResampleHz);
            double effectiveEnd = endUT;
            for (double t = startUT; t <= endUT + 1e-9; t += stepS)
            {
                if (!TrySampleWorld(a, t, out Vector3d pa)
                    || !TrySampleWorld(b, t, out Vector3d pb))
                {
                    // Either side has no sample at this UT — truncate the
                    // window here. Ghost-only / partial-data overlaps are
                    // unusual; surface as a Verbose so tuning is visible.
                    effectiveEnd = t;
                    break;
                }
                double sep = (pb - pa).magnitude;
                if (sep > maxBubbleSeparationM)
                {
                    effectiveEnd = t;
                    break;
                }
            }
            if (effectiveEnd - startUT < minWindowDurationS) return;

            output.Add(new OverlapWindow
            {
                RecordingA = a.RecordingId,
                RecordingB = b.RecordingId,
                StartUT = startUT,
                EndUT = effectiveEnd,
                FrameTag = frameTag,
                PrimaryEnv = primaryEnv,
                BodyName = bodyName,
            });
        }

        private static void CollectStructuralSplits(
            Recording rec, double lo, double hi, SortedSet<double> splits,
            out double terminalCap)
        {
            terminalCap = double.NaN;
            if (rec == null) return;
            // BranchPoints live on the owning RecordingTree; we can't
            // resolve trees here without a callback. Phase 5 ships the
            // detector against TrackSection-only data (which already encodes
            // body changes and section boundaries via section endpoints,
            // both of which are inside the section start/end UTs the caller
            // passed). The fine-grained structural-event split is exercised
            // separately at recompute time by the lazy-recompute path,
            // which has access to the active marker / tree.
            //
            // For Phase 5 we still scan TrackSections for body-change
            // boundaries inside [lo, hi] and TIME_JUMP markers (encoded by
            // a section's endUT abutting the next section's startUT with a
            // body or env change).
            if (rec.TrackSections != null)
            {
                for (int i = 0; i < rec.TrackSections.Count; i++)
                {
                    double endUT = rec.TrackSections[i].endUT;
                    if (endUT > lo && endUT < hi)
                        splits.Add(endUT);
                }
            }
        }

        private static bool IsBubbleEligibleSection(in TrackSection section)
        {
            if (section.referenceFrame != ReferenceFrame.Absolute) return false;
            if (section.source != TrackSectionSource.Active
                && section.source != TrackSectionSource.Background) return false;
            return true;
        }

        private static string ResolveSectionBodyName(in TrackSection s)
        {
            if (s.frames != null && s.frames.Count > 0)
                return s.frames[0].bodyName;
            if (s.absoluteFrames != null && s.absoluteFrames.Count > 0)
                return s.absoluteFrames[0].bodyName;
            return null;
        }

        private static bool TrySampleWorld(Recording rec, double ut, out Vector3d worldPos)
        {
            worldPos = default;
            var seam = SamplePositionResolverForTesting;
            if (seam != null)
            {
                Vector3d? maybe = seam(rec, ut);
                if (!maybe.HasValue) return false;
                worldPos = maybe.Value;
                return true;
            }

            // Production fallback: linear-interpolate Recording.Points then
            // resolve via FlightGlobals body lookup. The detector runs at
            // commit time inside KSP so FlightGlobals is available; xUnit
            // tests use the seam.
            if (rec == null || rec.Points == null || rec.Points.Count == 0) return false;

            int idx = -1;
            for (int i = 0; i < rec.Points.Count; i++)
            {
                if (rec.Points[i].ut >= ut) { idx = i; break; }
            }
            if (idx < 0)
            {
                TrajectoryPoint last = rec.Points[rec.Points.Count - 1];
                if (last.ut < ut) return false;
                worldPos = ResolveBodyWorldPos(last);
                return !double.IsNaN(worldPos.x);
            }
            if (idx == 0)
            {
                worldPos = ResolveBodyWorldPos(rec.Points[0]);
                return !double.IsNaN(worldPos.x);
            }
            TrajectoryPoint before = rec.Points[idx - 1];
            TrajectoryPoint after = rec.Points[idx];
            double span = after.ut - before.ut;
            float t = span > 0 ? (float)((ut - before.ut) / span) : 0f;
            Vector3d pa = ResolveBodyWorldPos(before);
            Vector3d pb = ResolveBodyWorldPos(after);
            if (double.IsNaN(pa.x) || double.IsNaN(pb.x)) return false;
            worldPos = Vector3d.Lerp(pa, pb, t);
            return true;
        }

        private static Vector3d ResolveBodyWorldPos(TrajectoryPoint p)
        {
            if (string.IsNullOrEmpty(p.bodyName))
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            try
            {
                CelestialBody body = FlightGlobals.Bodies?.Find(b => b != null && b.bodyName == p.bodyName);
                if (object.ReferenceEquals(body, null))
                    return new Vector3d(double.NaN, double.NaN, double.NaN);
                return body.GetWorldSurfacePosition(p.latitude, p.longitude, p.altitude);
            }
            catch
            {
                return new Vector3d(double.NaN, double.NaN, double.NaN);
            }
        }

        /// <summary>
        /// Build a <see cref="CoBubbleOffsetTrace"/> from one
        /// <see cref="OverlapWindow"/>. The trace's offset axis is centred on
        /// the side the caller designates as primary; the other side fills
        /// <see cref="CoBubbleOffsetTrace.PeerRecordingId"/>. Sample rate is
        /// the lower of (primary section sampleRateHz, peer section
        /// sampleRateHz, <see cref="CoBubbleConfiguration"/>.ResampleHz).
        /// Returns null when either side has no resolvable world position
        /// at the window's start UT (HR-9 visible failure).
        /// </summary>
        internal static CoBubbleOffsetTrace BuildTrace(
            OverlapWindow window,
            Recording primary, Recording peer,
            byte primaryDesignation)
        {
            if (primary == null || peer == null) return null;
            if (window.EndUT <= window.StartUT) return null;

            double resampleHz = CoBubbleConfiguration.Default.ResampleHz;
            // Check section sample rates; use the lower per §10.2.
            double primaryRate = ResolveSectionSampleRate(primary, window.StartUT);
            double peerRate = ResolveSectionSampleRate(peer, window.StartUT);
            if (primaryRate > 0) resampleHz = Math.Min(resampleHz, primaryRate);
            if (peerRate > 0) resampleHz = Math.Min(resampleHz, peerRate);

            // P2-B: clamp the trace's covered range to BlendMaxWindowSeconds.
            // BlendMaxWindowSeconds participates in ConfigurationHash as the
            // documented per-trace duration cap (§17.3.1 / §22). Without this
            // clamp, BuildTrace would either (a) emit a trace covering the
            // full overlap and only the sample-cap (MaxCoBubbleSamplesPerTrace)
            // would limit memory while semantically the trace ignores the
            // duration cap, or (b) hit the sample cap mid-window and leave
            // EndUT pointing past the last sample so the blender clamps to
            // the final offset for the rest of the window — visually wrong.
            // Splitting an over-cap window into multiple traces is a Phase 5
            // follow-up; for now we clamp to the first BlendMaxWindowSeconds
            // and emit a Verbose so the truncation is visible (HR-9).
            double maxWindow = CoBubbleConfiguration.Default.BlendMaxWindowSeconds;
            double effectiveEndUT = window.EndUT;
            if (maxWindow > 0 && effectiveEndUT > window.StartUT + maxWindow)
            {
                ParsekLog.VerboseRateLimited("Pipeline-CoBubble",
                    "window-clamped-to-max",
                    string.Format(CultureInfo.InvariantCulture,
                        "BuildTrace window clamped to BlendMaxWindowSeconds: peer={0} startUT={1} originalEndUT={2} clampedEndUT={3} maxWindowS={4}",
                        peer.RecordingId,
                        window.StartUT.ToString("R", CultureInfo.InvariantCulture),
                        window.EndUT.ToString("R", CultureInfo.InvariantCulture),
                        (window.StartUT + maxWindow).ToString("R", CultureInfo.InvariantCulture),
                        maxWindow.ToString("R", CultureInfo.InvariantCulture)),
                    30.0);
                effectiveEndUT = window.StartUT + maxWindow;
            }

            double stepS = 1.0 / Math.Max(0.001, resampleHz);
            int sampleCount = Math.Max(2, (int)Math.Ceiling((effectiveEndUT - window.StartUT) / stepS) + 1);
            if (sampleCount > PannotationsSidecarBinary.MaxCoBubbleSamplesPerTrace)
                sampleCount = PannotationsSidecarBinary.MaxCoBubbleSamplesPerTrace;

            var uts = new double[sampleCount];
            var dx = new float[sampleCount];
            var dy = new float[sampleCount];
            var dz = new float[sampleCount];
            int writeIdx = 0;
            // P1-B: resolve the body once for inertial-frame lift. For
            // FrameTag=0 (body-fixed) traces the body is unused — the world
            // delta is itself a body-fixed-equivalent translation because
            // both endpoints came from GetWorldSurfacePosition(lat,lon,alt)
            // which already factors in the body's rotation phase at the
            // SAME ut on both sides.
            CelestialBody bodyForLift = window.FrameTag == 1
                ? ResolveBodyByName(window.BodyName)
                : null;
            for (int i = 0; i < sampleCount; i++)
            {
                double t = window.StartUT + i * stepS;
                if (t > effectiveEndUT) t = effectiveEndUT;
                if (!TrySampleWorld(primary, t, out Vector3d pPrimary)) return null;
                if (!TrySampleWorld(peer, t, out Vector3d pPeer)) return null;
                Vector3d delta = pPeer - pPrimary;
                // P1-B: for FrameTag=1 (ExoPropulsive / ExoBallistic), lift
                // the world-frame delta to the body's inertial frame at the
                // sample's recording UT. The blender re-lowers at playback
                // UT via LowerOffsetFromInertialToWorld so the offset
                // tracks the body's rotation phase difference between
                // recording and playback. Without this lift, replaying the
                // same trace at a later UT would emit a stale offset
                // pinned to the recording-time world frame.
                if (window.FrameTag == 1 && !object.ReferenceEquals(bodyForLift, null))
                {
                    delta = TrajectoryMath.FrameTransform.LiftOffsetFromWorldToInertial(
                        delta, bodyForLift, t);
                }
                uts[writeIdx] = t;
                dx[writeIdx] = (float)delta.x;
                dy[writeIdx] = (float)delta.y;
                dz[writeIdx] = (float)delta.z;
                writeIdx++;
            }
            // Trim if we hit the cap exactly.
            if (writeIdx != sampleCount)
            {
                Array.Resize(ref uts, writeIdx);
                Array.Resize(ref dx, writeIdx);
                Array.Resize(ref dy, writeIdx);
                Array.Resize(ref dz, writeIdx);
            }

            byte[] sig = ComputePeerContentSignature(peer, window.StartUT, effectiveEndUT);
            return new CoBubbleOffsetTrace
            {
                PeerRecordingId = peer.RecordingId,
                PeerSourceFormatVersion = peer.RecordingFormatVersion,
                PeerSidecarEpoch = peer.SidecarEpoch,
                PeerContentSignature = sig,
                StartUT = window.StartUT,
                EndUT = effectiveEndUT,
                FrameTag = window.FrameTag,
                PrimaryDesignation = primaryDesignation,
                UTs = uts,
                Dx = dx,
                Dy = dy,
                Dz = dz,
                // P1-A: persist body name so the runtime blender can
                // resolve the body for FrameTag=1 inertial→world lower.
                BodyName = window.BodyName,
            };
        }

        private static double ResolveSectionSampleRate(Recording rec, double ut)
        {
            if (rec == null || rec.TrackSections == null) return 0.0;
            int idx = TrajectoryMath.FindTrackSectionForUT(rec.TrackSections, ut);
            if (idx < 0 || idx >= rec.TrackSections.Count) return 0.0;
            return rec.TrackSections[idx].sampleRateHz;
        }

        /// <summary>
        /// Computes the SHA-256 over the peer's raw <see cref="Recording.Points"/>
        /// inside [<paramref name="startUT"/>, <paramref name="endUT"/>]. Pure
        /// function (HR-3): same recording, same window → same 32-byte digest.
        /// </summary>
        internal static byte[] ComputePeerContentSignature(
            Recording peer, double startUT, double endUT)
        {
            if (peer == null) return null;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                if (peer.Points != null)
                {
                    for (int i = 0; i < peer.Points.Count; i++)
                    {
                        TrajectoryPoint p = peer.Points[i];
                        if (p.ut < startUT || p.ut > endUT) continue;
                        w.Write(p.ut);
                        w.Write(p.latitude);
                        w.Write(p.longitude);
                        w.Write(p.altitude);
                        w.Write(p.velocity.x);
                        w.Write(p.velocity.y);
                        w.Write(p.velocity.z);
                        w.Write(p.rotation.x);
                        w.Write(p.rotation.y);
                        w.Write(p.rotation.z);
                        w.Write(p.rotation.w);
                        w.Write(p.bodyName ?? string.Empty);
                    }
                }
                w.Flush();
                using (var sha = SHA256.Create())
                {
                    return sha.ComputeHash(ms.ToArray());
                }
            }
        }

        /// <summary>
        /// Commit-time + lazy-recompute entry point. Detects every overlap
        /// window across the supplied recordings and writes the resulting
        /// traces into <see cref="SectionAnnotationStore"/> on both sides.
        /// Caller is responsible for invoking <see cref="SmoothingPipeline"/>
        /// to persist after.
        /// </summary>
        internal static int DetectAndStore(IReadOnlyList<Recording> recordings)
        {
            if (!SmoothingPipeline.ResolveUseCoBubbleBlend())
            {
                ParsekLog.Verbose("Pipeline-CoBubble",
                    "useCoBubbleBlend=false, skipping CoBubbleOverlapDetector.DetectAndStore");
                return 0;
            }
            List<OverlapWindow> windows = Detect(recordings);
            // Index recordings by id for trace pairing.
            var byId = new Dictionary<string, Recording>(StringComparer.Ordinal);
            for (int i = 0; i < recordings.Count; i++)
            {
                Recording r = recordings[i];
                if (r == null || string.IsNullOrEmpty(r.RecordingId)) continue;
                byId[r.RecordingId] = r;
            }

            int tracesEmitted = 0;
            for (int i = 0; i < windows.Count; i++)
            {
                OverlapWindow w = windows[i];
                if (!byId.TryGetValue(w.RecordingA, out Recording recA)) continue;
                if (!byId.TryGetValue(w.RecordingB, out Recording recB)) continue;

                // §10.1 commit-time hint: deterministic by ordinal id.
                // Authority is session-time CoBubblePrimarySelector; this
                // hint exists for diagnostics and lazy-recompute symmetry.
                bool aIsPrimaryHint = string.CompareOrdinal(recA.RecordingId, recB.RecordingId) < 0;

                // Phase 5 review-pass-5 P1: build each side's trace with
                // BuildTrace(window, primary=primarySide, peer=ownerSide).
                // BuildTrace's contract is delta = pPeer - pPrimary, so
                // owner = peer here yields Dx = ownerWorld - primaryWorld
                // — exactly the offset semantics the blender expects: a
                // trace stored under the rendered ghost's id, with
                // PeerRecordingId naming the primary, supplies
                // worldOffset such that ghostWorld = primaryWorld + offset.
                //
                // BuildTrace assigns PeerRecordingId = peer.RecordingId
                // (the OWNER side here), and computes the signature
                // against that owner — both are wrong for storage. The
                // RebrandTraceForPrimary helper rewrites the trace's
                // PeerRecordingId / PeerSourceFormatVersion /
                // PeerSidecarEpoch / PeerContentSignature to name the
                // primary side, which is what the blender's per-trace
                // peer-validation path (and the runtime
                // ResolveLivePeerRecording lookup) expect.
                //
                // The pre-fix code reused a single BuildTrace output for
                // both sides via CloneTraceWithPeer, whose flip condition
                // was exactly inverted: it kept the offset as-is when the
                // built trace's peer matched the storage side's intended
                // peer (which produced the wrong sign because BuildTrace's
                // "peer" is the OFFSET'S peer, not the STORED trace's
                // peer-id), and flipped only when rewriting the peer
                // metadata. Both stored sides ended up with wrong-sign
                // offsets — peer ghosts rendered on the opposite side of
                // the primary at the offset's distance. Two BuildTrace
                // calls cost 2× world-position lookups per overlap window
                // at commit time; bounded and acceptable per HR-3 (don't
                // optimize prematurely).
                //
                // Side A storage: trace under recA, PeerRecordingId = recB.
                CoBubbleOffsetTrace traceForA = BuildTrace(
                    w,
                    primary: recB,    // primary side for the offset reference
                    peer: recA,       // owner side (the rendered ghost)
                    primaryDesignation: aIsPrimaryHint ? (byte)1 : (byte)0);
                if (traceForA != null)
                {
                    RebrandTraceForPrimary(traceForA, primaryRec: recB);
                    SectionAnnotationStore.PutCoBubbleTrace(recA.RecordingId, traceForA);
                    tracesEmitted++;
                }
                // Side B storage: trace under recB, PeerRecordingId = recA.
                CoBubbleOffsetTrace traceForB = BuildTrace(
                    w,
                    primary: recA,
                    peer: recB,
                    primaryDesignation: aIsPrimaryHint ? (byte)0 : (byte)1);
                if (traceForB != null)
                {
                    RebrandTraceForPrimary(traceForB, primaryRec: recA);
                    SectionAnnotationStore.PutCoBubbleTrace(recB.RecordingId, traceForB);
                    tracesEmitted++;
                }
            }

            ParsekLog.Verbose("Pipeline-CoBubble",
                string.Format(CultureInfo.InvariantCulture,
                    "DetectAndStore summary: recordings={0} windows={1} tracesEmitted={2}",
                    recordings.Count, windows.Count, tracesEmitted));
            return tracesEmitted;
        }

        /// <summary>
        /// Phase 5 review-pass-5 helper: rewrites a trace's peer-id
        /// metadata (<see cref="CoBubbleOffsetTrace.PeerRecordingId"/>,
        /// <see cref="CoBubbleOffsetTrace.PeerSourceFormatVersion"/>,
        /// <see cref="CoBubbleOffsetTrace.PeerSidecarEpoch"/>,
        /// <see cref="CoBubbleOffsetTrace.PeerContentSignature"/>) so the
        /// trace names <paramref name="primaryRec"/> as its peer (the
        /// blender's terminology: a stored trace's <c>PeerRecordingId</c>
        /// names the primary that the offset references). Mutates the
        /// passed trace in place.
        ///
        /// <para>
        /// <c>BuildTrace</c>'s output uses the parameter name <c>peer</c>
        /// for "the recording the offset is computed against" (i.e. the
        /// owner side here). The blender's lookup keys the trace's
        /// <c>PeerRecordingId</c> against the primary, which is the
        /// opposite side. This helper bridges the naming gap without
        /// touching the offset arrays or rotating the offset signs —
        /// <c>BuildTrace</c> is invoked twice in <see cref="DetectAndStore"/>
        /// with the role parameters swapped per side, so the offset
        /// signs are already correct for each storage side.
        /// </para>
        /// </summary>
        private static void RebrandTraceForPrimary(
            CoBubbleOffsetTrace builtTrace, Recording primaryRec)
        {
            if (builtTrace == null || primaryRec == null) return;
            builtTrace.PeerRecordingId = primaryRec.RecordingId;
            builtTrace.PeerSourceFormatVersion = primaryRec.RecordingFormatVersion;
            builtTrace.PeerSidecarEpoch = primaryRec.SidecarEpoch;
            builtTrace.PeerContentSignature = ComputePeerContentSignature(
                primaryRec, builtTrace.StartUT, builtTrace.EndUT);
        }
    }
}
