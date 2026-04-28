using System;
using System.Globalization;

namespace Parsek
{
    /// <summary>
    /// Result of <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>.
    /// Pure data carrier for the swap decision so unit tests can pin the
    /// decision without touching live KSP state.
    /// </summary>
    internal struct InPlaceContinuationTarget
    {
        public bool ShouldSwap;
        public string TargetRecordingId;
        public string TargetVesselName;
        public uint TargetVesselPersistentId;
        public string Reason;
    }

    /// <summary>
    /// Singleton ScenarioModule entry marking that a re-fly session is live
    /// (design doc section 5.7). Written atomically in the same synchronous
    /// code path that creates the provisional re-fly recording; cleared on
    /// merge, return-to-Space-Center, quit-without-merge, retry (with a fresh
    /// session id), full-revert, and load-time validation failure.
    ///
    /// <para>Persisted as a single <c>REFLY_SESSION_MARKER</c> ConfigNode on
    /// ParsekScenario. This Phase 1 type defines only the shape; behavior
    /// wiring (validation, spare-set, zombie cleanup) lands in later phases.</para>
    /// </summary>
    public class ReFlySessionMarker
    {
        /// <summary>Unique GUID per invocation/retry (design §5.7).</summary>
        public string SessionId;

        /// <summary>RecordingTree this re-fly belongs to (design §5.7).</summary>
        public string TreeId;

        /// <summary>
        /// The NotCommitted provisional re-fly recording created at §6.3 step 1 (design §5.7).
        /// </summary>
        public string ActiveReFlyRecordingId;

        /// <summary>Supersede target — the child recording being re-flown (design §5.7).</summary>
        public string OriginChildRecordingId;

        /// <summary>
        /// Supersede target at invocation time: the slot's current effective
        /// recording id before this re-fly appends any new relation. Legacy
        /// markers load with null and merge falls back to
        /// <see cref="OriginChildRecordingId"/>.
        /// </summary>
        public string SupersedeTargetId;

        /// <summary>Invoked RewindPoint (design §5.7).</summary>
        public string RewindPointId;

        /// <summary>Planetarium UT at which the session was invoked (design §5.7).</summary>
        public double InvokedUT;

        /// <summary>Wall-clock timestamp at invocation (ISO 8601 UTC; design §5.7).</summary>
        public string InvokedRealTime;

        internal const string NodeName = "REFLY_SESSION_MARKER";

        /// <summary>Saves into a dedicated child node on the parent.</summary>
        public void SaveInto(ConfigNode parent)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            var ic = CultureInfo.InvariantCulture;
            ConfigNode node = parent.AddNode(NodeName);
            node.AddValue("sessionId", SessionId ?? "");
            node.AddValue("treeId", TreeId ?? "");
            node.AddValue("activeReFlyRecordingId", ActiveReFlyRecordingId ?? "");
            node.AddValue("originChildRecordingId", OriginChildRecordingId ?? "");
            node.AddValue("supersedeTargetId", SupersedeTargetId ?? "");
            node.AddValue("rewindPointId", RewindPointId ?? "");
            node.AddValue("invokedUT", InvokedUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(InvokedRealTime))
                node.AddValue("invokedRealTime", InvokedRealTime);
        }

        /// <summary>Loads from a single <see cref="NodeName"/> node (caller supplies the node directly).</summary>
        public static ReFlySessionMarker LoadFrom(ConfigNode node)
        {
            if (node == null) throw new ArgumentNullException(nameof(node));
            var ic = CultureInfo.InvariantCulture;
            var m = new ReFlySessionMarker();

            string sid = node.GetValue("sessionId");
            m.SessionId = string.IsNullOrEmpty(sid) ? null : sid;

            string tid = node.GetValue("treeId");
            m.TreeId = string.IsNullOrEmpty(tid) ? null : tid;

            string active = node.GetValue("activeReFlyRecordingId");
            m.ActiveReFlyRecordingId = string.IsNullOrEmpty(active) ? null : active;

            string origin = node.GetValue("originChildRecordingId");
            m.OriginChildRecordingId = string.IsNullOrEmpty(origin) ? null : origin;

            string supersedeTarget = node.GetValue("supersedeTargetId");
            m.SupersedeTargetId = string.IsNullOrEmpty(supersedeTarget) ? null : supersedeTarget;

            string rp = node.GetValue("rewindPointId");
            m.RewindPointId = string.IsNullOrEmpty(rp) ? null : rp;

            string utStr = node.GetValue("invokedUT");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                m.InvokedUT = ut;

            m.InvokedRealTime = node.GetValue("invokedRealTime");

            return m;
        }

        /// <summary>
        /// Bug #585: in-place continuation Re-Fly marker carve-out for
        /// <c>RestoreActiveTreeFromPending</c>. When the marker exists,
        /// represents an in-place continuation
        /// (<c>OriginChildRecordingId == ActiveReFlyRecordingId</c>), and the
        /// marker's recording id is present in the freshly-popped tree, the
        /// restore coroutine MUST resolve the expected active vessel from the
        /// marker's recording -- NOT from the tree's stale
        /// <c>ActiveRecordingId</c> (which still points at the pre-rewind
        /// active vessel, just stripped). Returns
        /// <c>ShouldSwap=true</c> with the new target identity when the swap
        /// applies; otherwise <c>ShouldSwap=false</c> with a Reason describing
        /// why the carve-out was skipped (telemetry).
        /// <para>
        /// Pure static so unit tests can drive every branch without a live
        /// KSP scene or a running coroutine.
        /// </para>
        /// </summary>
        /// <param name="marker">Live marker from <c>ParsekScenario.Instance.ActiveReFlySessionMarker</c>, or null.</param>
        /// <param name="treeId">Freshly-popped tree's id.</param>
        /// <param name="treeActiveRecordingId">Tree's stale <c>ActiveRecordingId</c> (read from the rewind quicksave's .sfs).</param>
        /// <param name="resolveRecording">Function that maps a recording id to its <c>(name, pid)</c> pair from the tree's <c>Recordings</c> map. Returns null when the id is not in the tree.</param>
        internal static InPlaceContinuationTarget ResolveInPlaceContinuationTarget(
            ReFlySessionMarker marker,
            string treeId,
            string treeActiveRecordingId,
            Func<string, (string vesselName, uint persistentId)?> resolveRecording)
        {
            if (marker == null)
            {
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "no-marker",
                };
            }
            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
            {
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "marker-fields-empty",
                };
            }
            if (!string.Equals(
                    marker.ActiveReFlyRecordingId,
                    marker.OriginChildRecordingId,
                    StringComparison.Ordinal))
            {
                // Placeholder pattern (origin != active) -- the tree's
                // ActiveRecordingId still points at the live pre-rewind
                // vessel which is what we want; no swap.
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "placeholder-pattern",
                };
            }
            if (!string.IsNullOrEmpty(marker.TreeId)
                && !string.IsNullOrEmpty(treeId)
                && !string.Equals(marker.TreeId, treeId, StringComparison.Ordinal))
            {
                // Tree id mismatch -- a previous session's stale marker, or a
                // different tree being restored. Don't swap.
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "marker-tree-id-mismatch",
                };
            }
            if (string.Equals(
                    marker.ActiveReFlyRecordingId,
                    treeActiveRecordingId,
                    StringComparison.Ordinal))
            {
                // Already pointing at the marker's recording (e.g., the rewind
                // quicksave was authored mid-session, after the previous Re-Fly).
                // No swap needed; existing wait-on-PID path is correct.
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "already-pointing-at-marker",
                };
            }
            if (resolveRecording == null)
            {
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "no-resolver",
                };
            }
            var resolved = resolveRecording(marker.ActiveReFlyRecordingId);
            if (!resolved.HasValue)
            {
                // Marker's recording is not in the freshly-popped tree --
                // either the tree was scoped down by sidecar hydration
                // failures, or this is a stale marker. Don't swap; the
                // existing wait-on-PID path will time out and the merge
                // dialog will surface the Limbo state.
                return new InPlaceContinuationTarget
                {
                    ShouldSwap = false,
                    Reason = "marker-recording-missing-from-tree",
                };
            }
            return new InPlaceContinuationTarget
            {
                ShouldSwap = true,
                TargetRecordingId = marker.ActiveReFlyRecordingId,
                TargetVesselName = resolved.Value.vesselName,
                TargetVesselPersistentId = resolved.Value.persistentId,
                Reason = "in-place-continuation",
            };
        }
    }
}
