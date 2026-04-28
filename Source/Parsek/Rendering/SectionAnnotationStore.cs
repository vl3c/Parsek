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
        /// Removes every section annotation for the given recording id. No-op
        /// if the recording has no entries. Clears both the spline map and
        /// the Phase 6 candidate map (HR-10: a recompute path must not leave
        /// stale candidates behind any more than stale splines).
        /// </summary>
        internal static void RemoveRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lock (Lock)
            {
                Splines.Remove(recordingId);
                Candidates.Remove(recordingId);
            }
        }

        /// <summary>Empties the entire store.</summary>
        internal static void Clear()
        {
            lock (Lock)
            {
                Splines.Clear();
                Candidates.Clear();
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
    }
}
