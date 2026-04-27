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
        /// Removes every section annotation for the given recording id. No-op
        /// if the recording has no entries.
        /// </summary>
        internal static void RemoveRecording(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId))
                return;

            lock (Lock)
            {
                Splines.Remove(recordingId);
            }
        }

        /// <summary>Empties the entire store.</summary>
        internal static void Clear()
        {
            lock (Lock)
            {
                Splines.Clear();
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
    }
}
