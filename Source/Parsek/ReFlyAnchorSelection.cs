using System;
using System.Collections.Generic;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Anchor-selection helpers for Re-Fly provisional recordings. The default
    /// path is the narrowed-gate filter
    /// <see cref="FilterCandidatesForReFlyProvisional(ReFlySessionMarker, string, System.Collections.Generic.ICollection{string}, System.Collections.Generic.IReadOnlyList{RecordingAnchorCandidate})"/>:
    /// while a re-fly is active, drop every nearest-search candidate whose
    /// recording id is a member of the same <see cref="RecordingTree"/> as
    /// the provisional, so the recorder authors Relative against real
    /// out-of-tree anchors only (stations / bases / live loop anchors). See
    /// <c>docs/dev/plans/narrow-refly-relative-gate.md</c>.
    ///
    /// <para><see cref="IsActiveRecordingReFlyProvisional(ReFlySessionMarker, string)"/>
    /// is the predicate the filter uses to decide whether the recording being
    /// authored IS the live re-fly provisional.</para>
    ///
    /// <para>The filter deliberately uses the marker (not a recording-level
    /// field) so no schema bump is required. The recording on disk continues
    /// to use the existing <c>TrackSection.anchorRecordingId</c> field, so a
    /// future parent-anchored-contract extension can swap the marker lookup
    /// for a recording-level field without changing wire format.</para>
    /// </summary>
    internal static class ReFlyAnchorSelection
    {
        /// <summary>
        /// Returns true iff the active recording is the live re-fly
        /// provisional. Used by the narrowed-gate filter to decide whether to
        /// drop same-tree anchor candidates.
        ///
        /// <para>Parent-anchored re-fly provisionals (controlled-decoupled
        /// child being re-flown, with <c>DebrisParentRecordingId</c> set on the
        /// provisional) are treated the same as top-level re-fly provisionals:
        /// the predicate does not read <c>DebrisParentRecordingId</c>.</para>
        /// </summary>
        internal static bool IsActiveRecordingReFlyProvisional(
            ReFlySessionMarker marker,
            string activeRecordingId)
        {
            if (marker == null) return false;
            if (string.IsNullOrEmpty(activeRecordingId)) return false;
            if (!string.Equals(
                    marker.ActiveReFlyRecordingId,
                    activeRecordingId,
                    StringComparison.Ordinal))
                return false;
            return true;
        }

        /// <summary>
        /// Production wrapper. Derives marker + active recording id from live
        /// scenario state and the supplied active tree.
        /// </summary>
        internal static bool IsActiveRecordingReFlyProvisional(
            RecordingTree activeTree)
        {
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            string activeRecordingId = activeTree?.ActiveRecordingId;
            return IsActiveRecordingReFlyProvisional(marker, activeRecordingId);
        }

        /// <summary>
        /// Narrowed-gate filter for re-fly provisional anchor selection. When a
        /// re-fly session is active and the recording being authored IS the
        /// provisional, drops every candidate whose recording id is a member
        /// of the same <see cref="RecordingTree.Recordings"/> keyset as the
        /// provisional. Real persistent vessels / stations / bases live in
        /// other trees (or no tree at all), so they pass through and remain
        /// eligible for the nearest-search.
        ///
        /// <para>The narrowed gate keeps the nearest-search behavior for real
        /// anchors (preserving Relative-against-live-station for
        /// docking-mid-rewind and Relative-against-live-loop-anchor for
        /// loop-anchored re-fly forks) while never authoring Relative against
        /// a same-tree (superseded) sibling.</para>
        ///
        /// <para>Pure overload: marker and same-tree recording-id set are
        /// injected so xUnit fixtures can pin every branch without touching
        /// live <see cref="ParsekScenario.Instance"/> state.</para>
        /// </summary>
        internal static IReadOnlyList<RecordingAnchorCandidate> FilterCandidatesForReFlyProvisional(
            ReFlySessionMarker marker,
            string activeRecordingId,
            ICollection<string> sameTreeRecordingIds,
            IReadOnlyList<RecordingAnchorCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return candidates;
            if (!IsActiveRecordingReFlyProvisional(marker, activeRecordingId))
                return candidates;
            if (sameTreeRecordingIds == null || sameTreeRecordingIds.Count == 0)
                return candidates;

            int dropped = 0;
            List<RecordingAnchorCandidate> filtered = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                RecordingAnchorCandidate c = candidates[i];
                bool drop = !string.IsNullOrEmpty(c.RecordingId)
                    && sameTreeRecordingIds.Contains(c.RecordingId);
                if (drop)
                {
                    if (filtered == null)
                    {
                        filtered = new List<RecordingAnchorCandidate>(candidates.Count);
                        for (int j = 0; j < i; j++) filtered.Add(candidates[j]);
                    }
                    dropped++;
                    continue;
                }
                filtered?.Add(c);
            }

            if (dropped == 0) return candidates;

            ParsekLog.VerboseRateLimited(
                "Anchor",
                "refly-anchor-filter",
                "FilterCandidatesForReFlyProvisional: dropped=" +
                dropped.ToString(CultureInfo.InvariantCulture) +
                " kept=" + (filtered?.Count ?? 0).ToString(CultureInfo.InvariantCulture) +
                " provisionalRecId=" + (activeRecordingId ?? "(none)"),
                5.0);

            return filtered;
        }

        /// <summary>
        /// Production overload. Reads the marker from live scenario state and
        /// derives the same-tree recording-id set from <paramref name="activeTree"/>.
        /// </summary>
        internal static IReadOnlyList<RecordingAnchorCandidate> FilterCandidatesForReFlyProvisional(
            RecordingTree activeTree,
            IReadOnlyList<RecordingAnchorCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return candidates;
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            string activeRecordingId = activeTree?.ActiveRecordingId;
            if (!IsActiveRecordingReFlyProvisional(marker, activeRecordingId))
                return candidates;
            ICollection<string> sameTreeRecordingIds = activeTree?.Recordings?.Keys;
            return FilterCandidatesForReFlyProvisional(
                marker,
                activeRecordingId,
                sameTreeRecordingIds,
                candidates);
        }
    }
}
