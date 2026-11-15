using System;
using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// Phase 7 of Rewind-to-Staging (design §3.3 + §10.3): thin static facade
    /// over <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/> and the
    /// live <see cref="ParsekScenario.ActiveReFlySessionMarker"/>. Physical-
    /// visibility subsystems (ghost playback, chain walker, map presence, watch
    /// mode) route their "is this recording suppressed right now?" questions
    /// through this helper so the closure is computed, cached, and logged in
    /// exactly one place.
    ///
    /// <para>Emits the §10.3 session-transition log lines once per transition
    /// (null -> marker, marker -> null, marker -> different marker) via lazy
    /// detection on every query. No per-frame log spam.</para>
    /// </summary>
    internal static class SessionSuppressionState
    {
        private const string Tag = "ReFlySession";

        // Tracks the identity of the last-observed marker so we can fire the
        // Start/End transition logs exactly once. Identity comparison is
        // reference-equal (each new marker instance is a distinct re-fly
        // session by §5.7), plus a SessionId guard for the case where the
        // scenario module is torn down and rebuilt with the same logical
        // session (e.g. scene reload mid-session).
        private static readonly object SyncRoot = new object();
        private static object lastObservedMarkerIdentity;
        private static string lastObservedSessionId;

        /// <summary>True iff an active re-fly session marker is present.</summary>
        internal static bool IsActive
        {
            get
            {
                CheckTransition();
                var scenario = ParsekScenario.Instance;
                if (object.ReferenceEquals(null, scenario)) return false;
                return scenario.ActiveReFlySessionMarker != null;
            }
        }

        /// <summary>
        /// Returns the active marker, or null if no session is live. The check
        /// also detects session transitions and fires the one-shot Start/End
        /// log lines (§10.3).
        /// </summary>
        internal static ReFlySessionMarker ActiveMarker
        {
            get
            {
                CheckTransition();
                var scenario = ParsekScenario.Instance;
                if (object.ReferenceEquals(null, scenario)) return null;
                return scenario.ActiveReFlySessionMarker;
            }
        }

        /// <summary>
        /// The closure of recording ids suppressed by the active session.
        /// Delegates to <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>
        /// (which caches). Returns an empty collection when no marker is active.
        /// </summary>
        internal static IReadOnlyCollection<string> SuppressedSubtreeIds
        {
            get
            {
                var marker = ActiveMarker;
                if (marker == null) return Array.Empty<string>();
                return EffectiveState.ComputeSessionSuppressedSubtree(marker);
            }
        }

        /// <summary>
        /// O(1) check: is the given recording id in the suppressed closure of
        /// the active session? False when no session is active.
        /// </summary>
        internal static bool IsSuppressed(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return false;
            var marker = ActiveMarker;
            if (marker == null) return false;
            var rec = FindRecordingById(recordingId);
            if (rec == null) return false;
            return EffectiveState.IsInSessionSuppressedSubtree(rec, marker);
        }

        /// <summary>
        /// O(1) check: is the recording at the given committed-list index
        /// suppressed by the active session? Used by legacy index-based
        /// consumers (ghost engine, GhostMapPresence) until the recording-id
        /// keying migration lands.
        /// </summary>
        internal static bool IsSuppressedRecordingIndex(int index)
        {
            if (index < 0) return false;
            var marker = ActiveMarker;
            if (marker == null) return false;
            // Raw read: the consumers calling this pass their own raw index
            // into the committed list. Routing through ERS would shift the
            // index space and break every caller. Allowlisted for this reason
            // — see scripts/ers-els-audit-allowlist.txt.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null || index >= committed.Count) return false;
            var rec = committed[index];
            if (rec == null) return false;
            return EffectiveState.IsInSessionSuppressedSubtree(rec, marker);
        }

        // Internal: surface the transition-detection routine so tests can
        // exercise the one-shot logging path without reflection.
        internal static void ObserveMarkerTransitionsForTesting() => CheckTransition();

        /// <summary>
        /// Resets the transition-observer state so tests can exercise the
        /// one-shot transition logs deterministically. Does NOT reset the
        /// underlying <see cref="ParsekScenario"/> marker.
        /// </summary>
        internal static void ResetForTesting()
        {
            lock (SyncRoot)
            {
                lastObservedMarkerIdentity = null;
                lastObservedSessionId = null;
            }
        }

        private static Recording FindRecordingById(string recordingId)
        {
            // Raw read, same justification as IsSuppressedRecordingIndex above.
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return null;
            for (int i = 0; i < committed.Count; i++)
            {
                var r = committed[i];
                if (r == null) continue;
                if (string.Equals(r.RecordingId, recordingId, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        // Compares the current marker identity against the last observed one
        // and emits a one-shot Start / End log line when the identity changes.
        // Called on every public accessor so the transition is caught lazily
        // from whichever subsystem queries first.
        private static void CheckTransition()
        {
            var scenario = ParsekScenario.Instance;
            ReFlySessionMarker current = object.ReferenceEquals(null, scenario)
                ? null
                : scenario.ActiveReFlySessionMarker;

            lock (SyncRoot)
            {
                bool identityChanged = !ReferenceEquals(lastObservedMarkerIdentity, current);
                string currentSessionId = current?.SessionId;
                bool sessionIdChanged = !string.Equals(lastObservedSessionId, currentSessionId, StringComparison.Ordinal);
                if (!identityChanged && !sessionIdChanged)
                    return;

                bool hadPrevious = lastObservedMarkerIdentity != null || lastObservedSessionId != null;
                bool hasCurrent = current != null;

                if (hadPrevious && !hasCurrent)
                {
                    ParsekLog.Info(Tag,
                        $"End reason=<cleared> sess={lastObservedSessionId ?? "<no-id>"}");
                }
                else if (!hadPrevious && hasCurrent)
                {
                    LogStart(current);
                }
                else if (hadPrevious && hasCurrent)
                {
                    // Marker swapped (retry or scene-reload rebuild with same/new
                    // session id). Emit End for the previous and Start for the new.
                    ParsekLog.Info(Tag,
                        $"End reason=<cleared> sess={lastObservedSessionId ?? "<no-id>"}");
                    LogStart(current);
                }

                lastObservedMarkerIdentity = current;
                lastObservedSessionId = currentSessionId;
            }
        }

        private static void LogStart(ReFlySessionMarker marker)
        {
            // Compute the subtree ids directly (not via SuppressedSubtreeIds
            // which would call CheckTransition recursively). Defensive copy is
            // fine — the closure is small (one re-fly subtree).
            var closure = EffectiveState.ComputeSessionSuppressedSubtree(marker);
            int count = closure?.Count ?? 0;
            string joined = count == 0
                ? "<empty>"
                : string.Join(",", closure);
            ParsekLog.Info(Tag,
                $"Start. sess={marker.SessionId ?? "<no-id>"} " +
                $"SuppressedSubtree=[{count} ids: {joined}]");
        }
    }
}
