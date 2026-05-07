using System;
using System.Collections.Generic;
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

        /// <summary>
        /// Root part persistentId for the selected Re-Fly slot at invocation
        /// time. When present, playback uses this live part position as the
        /// active Re-Fly anchor instead of the vessel-level world position.
        /// </summary>
        public uint SelectedRootPartPersistentId;

        /// <summary>Planetarium UT at which the session was invoked (design §5.7).</summary>
        public double InvokedUT;

        /// <summary>Wall-clock timestamp at invocation (ISO 8601 UTC; design §5.7).</summary>
        public string InvokedRealTime;

        /// <summary>
        /// True when the Re-Fly session was invoked in the in-place
        /// continuation case: the player keeps flying the same physical
        /// vessel as the slot's origin recording, so
        /// <see cref="OriginChildRecordingId"/> and the strip-selected
        /// vessel pid match. The active attempt is forked into a separate
        /// <see cref="ActiveReFlyRecordingId"/> recording that supersedes
        /// origin at commit; before this flag, in-place sessions pointed
        /// the marker at the origin id directly and mutated the origin
        /// recording in flight (issue #734).
        /// </summary>
        public bool InPlaceContinuation;

        /// <summary>
        /// Snapshot of <see cref="BranchPoint.Id"/>s that already existed
        /// in the marker's tree at session-creation time. Used by
        /// <see cref="SupersedeCommit.HasReFlySessionStructuralMutation"/>
        /// as a session-local baseline so structural-mutation auto-seal
        /// fires only on branch points authored DURING this Re-Fly —
        /// pre-existing BPs that the load-time
        /// <c>SpliceMissingCommittedRecordingsIntoLoadedTree</c> path
        /// re-grafts back into the loaded tree are excluded. Stored as
        /// repeated <c>preSessionBranchPointId</c> values; absent on
        /// markers created before this field shipped, in which case the
        /// structural-mutation gate is conservatively skipped.
        /// </summary>
        public List<string> PreSessionBranchPointIds;

        internal const string NodeName = "REFLY_SESSION_MARKER";

        /// <summary>
        /// Returns true when <paramref name="marker"/> represents an
        /// in-place Re-Fly continuation: <see cref="InPlaceContinuation"/>
        /// is set by <c>RewindInvoker.AtomicMarkerWrite</c> when it forks
        /// the attempt off the same physical vessel as origin (issue #734).
        /// All in-place gating across the codebase routes through this
        /// helper so call sites cannot drift to ad-hoc shape checks.
        /// </summary>
        public static bool IsInPlaceContinuation(ReFlySessionMarker marker)
        {
            if (marker == null || !marker.InPlaceContinuation)
                return false;
            return !string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                && !string.IsNullOrEmpty(marker.OriginChildRecordingId);
        }

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
            if (SelectedRootPartPersistentId != 0u)
                node.AddValue("selectedRootPartPersistentId", SelectedRootPartPersistentId.ToString(ic));
            node.AddValue("invokedUT", InvokedUT.ToString("R", ic));
            if (!string.IsNullOrEmpty(InvokedRealTime))
                node.AddValue("invokedRealTime", InvokedRealTime);
            if (InPlaceContinuation)
                node.AddValue("inPlaceContinuation", "true");
            if (PreSessionBranchPointIds != null)
            {
                // Always emit the sentinel so absent-vs-empty round-trips
                // safely. Round-trip readers distinguish "field present,
                // empty list" (post-fix marker on a tree with no BPs at
                // invocation) from "field absent" (legacy marker, gate
                // conservatively skipped).
                node.AddValue("preSessionBranchPointIdsPresent", "true");
                for (int i = 0; i < PreSessionBranchPointIds.Count; i++)
                {
                    string id = PreSessionBranchPointIds[i];
                    if (!string.IsNullOrEmpty(id))
                        node.AddValue("preSessionBranchPointId", id);
                }
            }
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

            string selectedRootPartPidStr = node.GetValue("selectedRootPartPersistentId");
            uint selectedRootPartPid;
            if (!string.IsNullOrEmpty(selectedRootPartPidStr)
                && uint.TryParse(
                    selectedRootPartPidStr,
                    NumberStyles.Integer,
                    ic,
                    out selectedRootPartPid))
            {
                m.SelectedRootPartPersistentId = selectedRootPartPid;
            }

            string utStr = node.GetValue("invokedUT");
            double ut;
            if (!string.IsNullOrEmpty(utStr) && double.TryParse(utStr, NumberStyles.Float, ic, out ut))
                m.InvokedUT = ut;

            m.InvokedRealTime = node.GetValue("invokedRealTime");

            string inPlaceFlag = node.GetValue("inPlaceContinuation");
            m.InPlaceContinuation = string.Equals(
                inPlaceFlag, "true", StringComparison.OrdinalIgnoreCase);

            string presentFlag = node.GetValue("preSessionBranchPointIdsPresent");
            if (string.Equals(presentFlag, "true", StringComparison.OrdinalIgnoreCase))
            {
                m.PreSessionBranchPointIds = new List<string>();
                string[] ids = node.GetValues("preSessionBranchPointId");
                if (ids != null)
                {
                    for (int i = 0; i < ids.Length; i++)
                    {
                        if (!string.IsNullOrEmpty(ids[i]))
                            m.PreSessionBranchPointIds.Add(ids[i]);
                    }
                }
            }

            return m;
        }

        /// <summary>
        /// In-place continuation Re-Fly marker carve-out for
        /// <c>RestoreActiveTreeFromPending</c>. When the marker represents
        /// an in-place continuation (<see cref="InPlaceContinuation"/>=true)
        /// and the marker's recording id is present in the freshly-popped
        /// tree, the restore coroutine MUST resolve the expected active
        /// vessel from the marker's recording -- NOT from the tree's stale
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
            if (!IsInPlaceContinuation(marker))
            {
                // Placeholder pattern (no InPlaceContinuation flag) -- the
                // tree's ActiveRecordingId still points at the live
                // pre-rewind vessel which is what we want; no swap.
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
