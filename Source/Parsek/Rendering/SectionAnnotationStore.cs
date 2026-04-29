using System;
using System.Collections.Generic;

namespace Parsek.Rendering
{
    /// <summary>
    /// In-memory store of derived per-section annotations keyed by recording id +
    /// section index. Phase 1 only stores <see cref="SmoothingSpline"/>; later
    /// phases will add outlier-flag and anchor-candidate accessors alongside.
    ///
    /// <para>
    /// Lifetime is process-scoped — the store is rebuilt by lazy compute
    /// (annotation-absent on load) or by reading <c>.pann</c> via
    /// <c>PannotationsSidecarBinary</c>. Tests reset state via
    /// <see cref="ResetForTesting"/>.
    /// </para>
    /// </summary>
    internal static class SectionAnnotationStore
    {
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, Dictionary<int, SmoothingSpline>> Splines
            = new Dictionary<string, Dictionary<int, SmoothingSpline>>(StringComparer.Ordinal);

        // Phase 6 (design doc §17.3.1, §18 Phase 6). Parallel store keyed
        // identically to the spline dict. AnchorCandidateBuilder writes
        // candidates per section; AnchorPropagator reads them at session
        // entry to resolve final AnchorCorrection ε values. Persisted to /
        // loaded from the .pann AnchorCandidatesList block.
        private static readonly Dictionary<string, Dictionary<int, AnchorCandidate[]>> Candidates
            = new Dictionary<string, Dictionary<int, AnchorCandidate[]>>(StringComparer.Ordinal);

        // Phase 5 (design doc §6.5 / §10 / §17.3.1). Per-recording list of
        // co-bubble offset traces. CoBubbleOverlapDetector writes traces at
        // commit time (and lazily on first co-render demand); CoBubbleBlender
        // reads them at playback. Persisted to / loaded from the .pann
        // CoBubbleOffsetTraces block.
        //
        // The map is recordingId -> List<trace>. Each trace's PeerRecordingId
        // names the OTHER side of the overlap pair. Both sides of a pair
        // store their own list independently so a permanently-missing peer
        // does not block trace reads on the surviving side.
        private static readonly Dictionary<string, List<CoBubbleOffsetTrace>> CoBubbleTraces
            = new Dictionary<string, List<CoBubbleOffsetTrace>>(StringComparer.Ordinal);

        // Phase 8 (design doc §14, §17.3.1, §18 Phase 8). Per-section outlier
        // bitmap built by OutlierClassifier before the spline fit, consumed
        // by TrajectoryMath.CatmullRomFit.Fit to skip rejected samples, and
        // persisted to / loaded from the .pann OutlierFlagsList block.
        // Sections without rejected samples are NOT inserted (the absence of
        // an entry is the canonical "no krakens here" representation —
        // mirrors the AnchorCandidates "drop empty arrays at write time"
        // pattern).
        private static readonly Dictionary<string, Dictionary<int, OutlierFlags>> OutlierFlagsByRecording
            = new Dictionary<string, Dictionary<int, OutlierFlags>>(StringComparer.Ordinal);

        /// <summary>
        /// Stores or overwrites the spline for a (recordingId, sectionIndex) pair.
        /// Silent overwrite — caller is expected to gate on cache-key freshness
        /// before calling.
        /// </summary>
        internal static void PutSmoothingSpline(string recordingId, int sectionIndex, SmoothingSpline spline)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lock (Lock)
            {
                if (!Splines.TryGetValue(recordingId, out var perSection))
                {
                    perSection = new Dictionary<int, SmoothingSpline>();
                    Splines[recordingId] = perSection;
                }
                perSection[sectionIndex] = spline;
            }
        }

        /// <summary>
        /// Looks up the spline for a (recordingId, sectionIndex) pair. Returns
        /// <c>false</c> when either key is absent.
        /// </summary>
        internal static bool TryGetSmoothingSpline(string recordingId, int sectionIndex, out SmoothingSpline spline)
        {
            spline = default(SmoothingSpline);
            if (string.IsNullOrEmpty(recordingId))
                return false;

            lock (Lock)
            {
                if (!Splines.TryGetValue(recordingId, out var perSection))
                    return false;
                return perSection.TryGetValue(sectionIndex, out spline);
            }
        }

        /// <summary>
        /// Phase 6: stores or overwrites the candidate array for a
        /// (recordingId, sectionIndex) pair. Caller passes a frozen array;
        /// silent overwrite mirrors <see cref="PutSmoothingSpline"/>.
        ///
        /// <para>
        /// Empty / null arrays are stored verbatim in-memory but are
        /// equivalent to "absent" on round-trip: the
        /// <see cref="SmoothingPipeline"/> writer drops sections whose
        /// candidate array is empty before persisting to <c>.pann</c>, and
        /// the persisted block only contains sections the writer included.
        /// Consumers that need a "scanned but empty" distinction (e.g.
        /// "this section was inspected and produced zero candidates") must
        /// hold that state outside this store.
        /// </para>
        /// </summary>
        internal static void PutAnchorCandidates(
            string recordingId, int sectionIndex, AnchorCandidate[] candidates)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lock (Lock)
            {
                if (!Candidates.TryGetValue(recordingId, out var perSection))
                {
                    perSection = new Dictionary<int, AnchorCandidate[]>();
                    Candidates[recordingId] = perSection;
                }
                perSection[sectionIndex] = candidates ?? new AnchorCandidate[0];
            }
        }

        /// <summary>
        /// Phase 6: looks up the candidate array for a (recordingId,
        /// sectionIndex) pair. Returns false when either key is absent.
        /// </summary>
        internal static bool TryGetAnchorCandidates(
            string recordingId, int sectionIndex, out AnchorCandidate[] candidates)
        {
            candidates = null;
            if (string.IsNullOrEmpty(recordingId))
                return false;

            lock (Lock)
            {
                if (!Candidates.TryGetValue(recordingId, out var perSection))
                    return false;
                return perSection.TryGetValue(sectionIndex, out candidates);
            }
        }

        /// <summary>
        /// Phase 5: stores or appends a co-bubble offset trace under the
        /// owning <paramref name="recordingId"/>. Multiple traces may exist
        /// per recording (one per overlap window with each peer); the same
        /// (peerRecordingId, startUT, endUT) tuple is overwritten in place
        /// so a re-detect does not double-emit the same window.
        /// </summary>
        internal static void PutCoBubbleTrace(string recordingId, CoBubbleOffsetTrace trace)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (trace == null) return;

            lock (Lock)
            {
                if (!CoBubbleTraces.TryGetValue(recordingId, out var list))
                {
                    list = new List<CoBubbleOffsetTrace>();
                    CoBubbleTraces[recordingId] = list;
                }
                // Replace existing entry with matching (peer, startUT, endUT) tuple.
                for (int i = 0; i < list.Count; i++)
                {
                    CoBubbleOffsetTrace existing = list[i];
                    if (existing == null) continue;
                    if (string.Equals(existing.PeerRecordingId, trace.PeerRecordingId, StringComparison.Ordinal)
                        && existing.StartUT == trace.StartUT
                        && existing.EndUT == trace.EndUT)
                    {
                        list[i] = trace;
                        return;
                    }
                }
                list.Add(trace);
            }
        }

        /// <summary>
        /// Phase 5: looks up the per-recording list of co-bubble offset
        /// traces. Returns false (with <paramref name="traces"/> = null) when
        /// the recording has no traces. The returned list is the live store
        /// reference — callers must not mutate it; reads under any lock-free
        /// access pattern accept that a concurrent write may add entries
        /// after the lookup, which is acceptable for the read-only blender.
        /// </summary>
        internal static bool TryGetCoBubbleTraces(string recordingId, out List<CoBubbleOffsetTrace> traces)
        {
            traces = null;
            if (string.IsNullOrEmpty(recordingId)) return false;

            lock (Lock)
            {
                return CoBubbleTraces.TryGetValue(recordingId, out traces);
            }
        }

        /// <summary>
        /// Phase 8: stores or overwrites the outlier-flag bitmap for a
        /// (recordingId, sectionIndex) pair. Caller passes a non-null
        /// <see cref="OutlierFlags"/>; silent overwrite mirrors
        /// <see cref="PutSmoothingSpline"/>.
        /// </summary>
        internal static void PutOutlierFlags(
            string recordingId, int sectionIndex, OutlierFlags flags)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            if (flags == null) return;

            lock (Lock)
            {
                if (!OutlierFlagsByRecording.TryGetValue(recordingId, out var perSection))
                {
                    perSection = new Dictionary<int, OutlierFlags>();
                    OutlierFlagsByRecording[recordingId] = perSection;
                }
                perSection[sectionIndex] = flags;
            }
        }

        /// <summary>
        /// Phase 8: looks up the outlier-flag bitmap for a (recordingId,
        /// sectionIndex) pair. Returns false (with <paramref name="flags"/>
        /// = null) when either key is absent — that's the "no krakens
        /// detected in this section" canonical state.
        /// </summary>
        internal static bool TryGetOutlierFlags(
            string recordingId, int sectionIndex, out OutlierFlags flags)
        {
            flags = null;
            if (string.IsNullOrEmpty(recordingId)) return false;
            lock (Lock)
            {
                if (!OutlierFlagsByRecording.TryGetValue(recordingId, out var perSection))
                    return false;
                return perSection.TryGetValue(sectionIndex, out flags);
            }
        }

        /// <summary>
        /// Test-only / diagnostics: number of sections with outlier flags
        /// stored for a recording (0 when the recording has none).
        /// </summary>
        internal static int GetOutlierFlagsCountForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return 0;
            lock (Lock)
            {
                if (!OutlierFlagsByRecording.TryGetValue(recordingId, out var perSection))
                    return 0;
                return perSection.Count;
            }
        }

        /// <summary>
        /// Phase 5: removes every co-bubble trace stored under
        /// <paramref name="recordingId"/>. Symmetric with
        /// <see cref="RemoveRecording"/>; called from the same recompute
        /// paths so stale traces do not survive a config-hash drift.
        /// </summary>
        internal static void RemoveCoBubbleTracesForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return;
            lock (Lock)
            {
                CoBubbleTraces.Remove(recordingId);
            }
        }

        /// <summary>
        /// Phase 5 review-pass-4: removes a single co-bubble trace
        /// matching <paramref name="recordingId"/> +
        /// <paramref name="peerRecordingId"/> + (<paramref name="startUT"/>,
        /// <paramref name="endUT"/>). Returns true when an entry was
        /// removed. Symmetric key with
        /// <see cref="PutCoBubbleTrace"/>'s "Replace existing entry with
        /// matching tuple" rule.
        ///
        /// <para>
        /// Called by
        /// <c>SmoothingPipeline.RevalidateDeferredCoBubbleTraces</c>:
        /// when a deferred trace's peer-content signature mismatches at
        /// post-hydration revalidation, the affected trace is dropped
        /// without touching the rest of the recording's traces.
        /// </para>
        /// </summary>
        internal static bool RemoveCoBubbleTrace(
            string recordingId, string peerRecordingId, double startUT, double endUT)
        {
            if (string.IsNullOrEmpty(recordingId) || string.IsNullOrEmpty(peerRecordingId))
                return false;
            lock (Lock)
            {
                if (!CoBubbleTraces.TryGetValue(recordingId, out var list) || list == null)
                    return false;
                for (int i = 0; i < list.Count; i++)
                {
                    CoBubbleOffsetTrace existing = list[i];
                    if (existing == null) continue;
                    if (string.Equals(existing.PeerRecordingId, peerRecordingId, StringComparison.Ordinal)
                        && existing.StartUT == startUT
                        && existing.EndUT == endUT)
                    {
                        list.RemoveAt(i);
                        // Drop the empty-list entry so callers using
                        // TryGetCoBubbleTraces see the recording as
                        // "no traces" rather than "non-null empty list".
                        if (list.Count == 0)
                            CoBubbleTraces.Remove(recordingId);
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Removes every section annotation for the given recording id. No-op
        /// if the recording has no entries. Clears the spline map, the
        /// Phase 6 candidate map, and the Phase 5 co-bubble trace list
        /// (HR-10: a recompute path must not leave stale annotations behind
        /// any more than stale splines).
        /// </summary>
        internal static void RemoveRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lock (Lock)
            {
                Splines.Remove(recordingId);
                Candidates.Remove(recordingId);
                CoBubbleTraces.Remove(recordingId);
                OutlierFlagsByRecording.Remove(recordingId);
            }
        }

        /// <summary>Empties the entire store.</summary>
        internal static void Clear()
        {
            lock (Lock)
            {
                Splines.Clear();
                Candidates.Clear();
                CoBubbleTraces.Clear();
                OutlierFlagsByRecording.Clear();
            }
        }

        /// <summary>
        /// Test-only: clears the store. Mirrors the project's
        /// <c>ResetForTesting</c> convention (e.g. <c>ParsekLog.ResetTestOverrides</c>).
        /// </summary>
        internal static void ResetForTesting()
        {
            Clear();
        }

        /// <summary>
        /// Test-only: number of section annotations stored under a recording id.
        /// </summary>
        internal static int GetSplineCountForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return 0;

            lock (Lock)
            {
                if (!Splines.TryGetValue(recordingId, out var perSection))
                    return 0;
                return perSection.Count;
            }
        }

        /// <summary>
        /// Test-only: number of Phase 6 candidate entries (sections with a
        /// candidate array stored, regardless of whether the array is empty).
        /// </summary>
        internal static int GetAnchorCandidateSectionCountForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return 0;

            lock (Lock)
            {
                if (!Candidates.TryGetValue(recordingId, out var perSection))
                    return 0;
                return perSection.Count;
            }
        }

        /// <summary>
        /// Test-only: number of Phase 5 co-bubble traces stored for a
        /// recording (0 when the key is absent).
        /// </summary>
        internal static int GetCoBubbleTraceCountForRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return 0;

            lock (Lock)
            {
                if (!CoBubbleTraces.TryGetValue(recordingId, out var list)) return 0;
                return list?.Count ?? 0;
            }
        }
    }
}
