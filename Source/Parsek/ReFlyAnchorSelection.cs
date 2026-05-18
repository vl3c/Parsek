using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Parsek
{
    /// <summary>
    /// Anchor-selection helper for Re-Fly provisional recordings. Reads the
    /// active <see cref="ReFlySessionMarker"/> and returns the supersede
    /// target recording id (with <c>OriginChildRecordingId</c> fallback) for
    /// use as <c>TrackSection.anchorRecordingId</c>. Both recorder sites
    /// (<c>BackgroundRecorder.UpdateBackgroundAnchorDetection</c> and
    /// <c>FlightRecorder.UpdateAnchorDetection</c>) consult this helper at the
    /// head of their anchor-detection logic and bypass the generic
    /// nearest-search when the active recording is the live re-fly provisional.
    ///
    /// <para>The selector deliberately uses the marker (not a recording-level
    /// field) so no schema bump is required. The recording on disk continues
    /// to use the existing <c>TrackSection.anchorRecordingId</c> field, so a
    /// future parent-anchored-contract extension can swap the marker lookup
    /// for a recording-level field without changing wire format.</para>
    /// </summary>
    internal static class ReFlyAnchorSelection
    {
        /// <summary>Maximum supersede-chain walk depth before the helper returns false with a Warn.</summary>
        internal const int CycleWalkDepthCap = 8;

        /// <summary>Depth at which the helper emits an Info breadcrumb so deep chains remain observable.</summary>
        internal const int CycleWalkDeepBreadcrumb = 4;

        internal const string SourceSupersedeTarget = "supersede-target";
        internal const string SourceOriginChild = "origin-child";

        /// <summary>
        /// Production overload. Looks up the active marker through
        /// <see cref="ParsekScenario.Instance"/> and resolves recordings by
        /// id through the supplied active tree first, then the committed-
        /// recording list. Returns true when the bypass should fire and the
        /// caller should pin <c>TrackSection.anchorRecordingId</c> to
        /// <paramref name="anchorRecordingId"/>.
        /// </summary>
        internal static bool TryResolveReFlyProvisionalAnchor(
            RecordingTree activeTree,
            string activeRecordingId,
            out string anchorRecordingId,
            out string source)
        {
            ReFlySessionMarker marker = ParsekScenario.Instance?.ActiveReFlySessionMarker;
            return TryResolveReFlyProvisionalAnchor(
                marker,
                activeRecordingId,
                MakeDefaultResolver(activeTree),
                out anchorRecordingId,
                out source);
        }

        /// <summary>
        /// Pure overload — marker and recording resolver are injected so
        /// xUnit fixtures can pin every branch (no-marker, mismatch,
        /// supersede-target, origin-child fallback, unknown id, cycle, depth
        /// cap) without touching live KSP state.
        /// </summary>
        internal static bool TryResolveReFlyProvisionalAnchor(
            ReFlySessionMarker marker,
            string activeRecordingId,
            Func<string, Recording> resolveRecording,
            out string anchorRecordingId,
            out string source)
        {
            anchorRecordingId = null;
            source = null;

            if (marker == null)
            {
                // No marker; the caller falls through to its existing logic.
                // Silent — logging here would spam every recorder tick.
                return false;
            }

            if (string.IsNullOrEmpty(activeRecordingId)
                || !string.Equals(
                    marker.ActiveReFlyRecordingId,
                    activeRecordingId,
                    StringComparison.Ordinal))
            {
                ParsekLog.VerboseRateLimited(
                    "Anchor",
                    "refly-bypass-skipped",
                    "re-fly bypass skipped: activeRecId=" + (activeRecordingId ?? "(none)") +
                    " markerActiveRecId=" + (marker.ActiveReFlyRecordingId ?? "(none)"),
                    5.0);
                return false;
            }

            string candidate = !string.IsNullOrEmpty(marker.SupersedeTargetId)
                ? marker.SupersedeTargetId
                : marker.OriginChildRecordingId;
            string candidateSource = !string.IsNullOrEmpty(marker.SupersedeTargetId)
                ? SourceSupersedeTarget
                : SourceOriginChild;

            // When the bypass declines, the caller falls back to the existing
            // anchor-selection path (nearest-search at both recorders).
            // Wording avoids claiming "recording as Absolute" because the
            // caller's nearest-search may still open a Relative section
            // against a different anchor candidate. The log surfaces the
            // bypass-decline with enough context to identify which fall-back
            // path will actually run.
            const string BypassDeclineSuffix = " -> bypass declined, falling back to nearest-search";

            if (string.IsNullOrEmpty(candidate))
            {
                ParsekLog.Warn(
                    "Anchor",
                    "re-fly anchor unavailable: provisionalRecId=" +
                    (activeRecordingId ?? "(none)") +
                    " supersedeTargetId=(none) originChildRecordingId=(none)" +
                    BypassDeclineSuffix);
                return false;
            }

            if (resolveRecording == null)
            {
                ParsekLog.Warn(
                    "Anchor",
                    "re-fly anchor walk: no resolver provided provisionalRecId=" +
                    (activeRecordingId ?? "(none)") +
                    " candidate=" + candidate +
                    BypassDeclineSuffix);
                return false;
            }

            if (!TryWalkSupersedeChain(
                    activeRecordingId,
                    candidate,
                    resolveRecording,
                    out string failureReason))
            {
                ParsekLog.Warn(
                    "Anchor",
                    "re-fly anchor walk: " + failureReason +
                    " provisionalRecId=" + (activeRecordingId ?? "(none)") +
                    " candidate=" + candidate +
                    " source=" + candidateSource +
                    BypassDeclineSuffix);
                return false;
            }

            // Confirm the candidate itself resolves to a known recording. If
            // the resolver returns null, the candidate is referenced but not
            // loaded — bypass declines and the caller falls back to
            // nearest-search.
            Recording candidateRec = resolveRecording(candidate);
            if (candidateRec == null)
            {
                ParsekLog.Warn(
                    "Anchor",
                    "re-fly anchor walk: candidate recording not resolvable provisionalRecId=" +
                    (activeRecordingId ?? "(none)") +
                    " candidate=" + candidate +
                    " source=" + candidateSource +
                    BypassDeclineSuffix);
                return false;
            }

            anchorRecordingId = candidate;
            source = candidateSource;
            return true;
        }

        private static bool TryWalkSupersedeChain(
            string provisionalRecId,
            string startCandidate,
            Func<string, Recording> resolveRecording,
            out string failureReason)
        {
            failureReason = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            visited.Add(provisionalRecId ?? string.Empty);

            string currentId = startCandidate;
            int depth = 0;

            while (!string.IsNullOrEmpty(currentId))
            {
                if (!visited.Add(currentId))
                {
                    failureReason = "cycle detected"
                        + " visited=" + FormatVisited(visited)
                        + " depth=" + depth.ToString(CultureInfo.InvariantCulture);
                    return false;
                }

                if (depth == CycleWalkDeepBreadcrumb)
                {
                    ParsekLog.Info(
                        "Anchor",
                        "re-fly anchor walk: deep chain provisionalRecId=" +
                        (provisionalRecId ?? "(none)") +
                        " depth=" + depth.ToString(CultureInfo.InvariantCulture) +
                        " currentAnchor=" + currentId +
                        " -> continuing");
                }

                Recording rec = resolveRecording(currentId);
                if (rec == null)
                {
                    // Walk ends at an unresolvable id; not a cycle and not a
                    // depth-cap hit. The caller's candidate-existence check
                    // handles whether the bypass actually fires; this walker
                    // just guarantees no cycle.
                    return true;
                }

                currentId = string.IsNullOrEmpty(rec.SupersedeTargetId) ? null : rec.SupersedeTargetId;
                depth++;

                if (depth > CycleWalkDepthCap)
                {
                    failureReason = "depth cap exceeded"
                        + " visited=" + FormatVisited(visited)
                        + " depth=" + depth.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
            }

            return true;
        }

        private static string FormatVisited(HashSet<string> visited)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (string id in visited)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!first) sb.Append(",");
                sb.Append(id);
                first = false;
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static Func<string, Recording> MakeDefaultResolver(RecordingTree activeTree)
        {
            return recordingId =>
            {
                if (string.IsNullOrEmpty(recordingId)) return null;

                // Prefer the supplied recorder tree first — the provisional
                // and its supersede target both live there during an in-flight
                // Re-Fly.
                if (activeTree?.Recordings != null
                    && activeTree.Recordings.TryGetValue(recordingId, out Recording fromTree)
                    && fromTree != null)
                {
                    return fromTree;
                }

                // Fall back to the committed-recording list for ids referenced
                // from outside the active recorder tree (e.g. background-only
                // continuation cases).
                return RecordingStore.TryFindCommittedRecordingById(recordingId);
            };
        }
    }
}
