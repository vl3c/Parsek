using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KSP.UI.Screens;
using UnityEngine;

namespace Parsek
{
    // Active Re-Fly suppression of state-vector ghost ProtoVessels and the parent-chain walk
    // behind it.
    internal static partial class GhostMapPresence
    {
        /// <summary>
        /// Phase 7 of Rewind-to-Staging (design §3.3): shared helper — is the
        /// recording at this index in the active session's SessionSuppressedSubtree?
        /// Returns false when no session is active or the index is out of range.
        /// </summary>
        internal static bool IsSuppressedByActiveSession(int recordingIndex)
        {
            return SessionSuppressionState.IsSuppressedRecordingIndex(recordingIndex);
        }

        /// <summary>
        /// Bug #587 third facet (2026-04-25 playtest): the in-place continuation
        /// Re-Fly path leaves the *parent* of the active Re-Fly recording outside
        /// the SessionSuppressedSubtree closure (the closure walks child-ward
        /// from <c>OriginChildRecordingId</c>). When that parent recording's
        /// playback is mid-flight in a <see cref="ReferenceFrame.Relative"/>
        /// section anchored to the *active Re-Fly target's* persistent id, the
        /// state-vector-fallback code path in
        /// <see cref="CreateGhostVesselFromStateVectors"/> resolves a world
        /// position right next to the live active vessel and feeds it (with
        /// the recording's atmospheric-ascent velocity) to
        /// <see cref="Orbit.UpdateFromStateVectors"/>. The result is a
        /// degenerate orbit (sma=2 ecc=0.999999) AND a real registered
        /// <see cref="Vessel"/> colocated with the player's vessel — the
        /// "doubled upper-stage" the user reported.
        ///
        /// <para>This predicate gates that one create site without touching any
        /// other source path. Pure-static so xUnit can pin every branch
        /// without a live KSP scene.</para>
        ///
        /// <para><b>Scope (PR #574 review P2):</b> the suppression is also
        /// gated on the *recording relationship* between the recording being
        /// mapped (<paramref name="victimRecordingId"/>) and the active Re-Fly
        /// recording. The bug only manifests when the victim is a
        /// <em>parent</em> (in the BranchPoint topology sense) of the active
        /// Re-Fly recording — that is, the recording from which the active
        /// was decoupled or otherwise branched. A docking-target recording
        /// or any unrelated recording that happens to be Relative-anchored to
        /// the active vessel (legitimate #583/#584 docking/rendezvous map
        /// ghosts) is *not* suppressed. The parent walk uses
        /// <see cref="Recording.ParentBranchPointId"/> +
        /// <see cref="BranchPoint.ParentRecordingIds"/> via the committed
        /// trees — the same edge data
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/> walks
        /// child-ward, traversed in the opposite direction.</para>
        ///
        /// <para><b>Retry semantics (PR #574 review P2):</b> when the
        /// predicate returns true at the call site
        /// <see cref="CreateGhostVesselFromStateVectors"/>, the function
        /// returns null. The flight-scene caller
        /// <c>ParsekPlaybackPolicy.CheckPendingMapVessels</c> normally drops
        /// the pending-map entry on null return — that would mean the
        /// recording never re-attempts after the Re-Fly session ends. The
        /// caller now consults <paramref name="retryLater"/> via
        /// <see cref="CreateGhostVesselFromSource(int, IPlaybackTrajectory,
        /// TrackingStationGhostSource, OrbitSegment, TrajectoryPoint, double,
        /// out bool)"/> and keeps the pending entry alive when this gate
        /// fires, so suppression is "skip-this-tick", not "permanent-reject".</para>
        ///
        /// <para>Sister fixes (#587 and the #587 follow-up) targeted the
        /// strip-side leftover (a pre-existing in-scene <c>Vessel</c> the
        /// PostLoadStripper missed). This third facet targets the GhostMap-side
        /// *creation* of a fresh ProtoVessel during the same Re-Fly invocation
        /// — the strip side never sees this vessel because it is born after
        /// strip runs.</para>
        /// </summary>
        /// <param name="marker">Live re-fly marker, or null.</param>
        /// <param name="resolutionBranch">Branch label from
        /// <see cref="StateVectorWorldFrame.Branch"/>: <c>"relative"</c> AND
        /// <c>"body-fixed-primary"</c> both suppress, because both describe a
        /// RELATIVE track section. The body-fixed-primary branch is a retained
        /// v7 compatibility branch for callers that already selected the
        /// recorded shadow point; create-time lookahead no longer performs a
        /// live-PID anchor scan. Suppressing both labels preserves the
        /// doubled-ProtoVessel guard without reintroducing non-loop live-anchor
        /// map resolution (PR #613 review P2).</param>
        /// <param name="resolutionAnchorPid">Anchor pid from the resolution.</param>
        /// <param name="victimRecordingId">RecordingId of the recording being
        /// mapped. Suppression is rejected with
        /// <c>not-suppressed-not-parent-of-refly-target</c> when the victim is
        /// not in the active Re-Fly recording's parent chain.</param>
        /// <param name="committedRecordings">Snapshot of <see cref="RecordingStore.CommittedRecordings"/>
        /// (the flat committed list). Used as a secondary lookup source for
        /// the active Re-Fly recording's PID. At Re-Fly load time the active
        /// recording's tree has been moved into <c>PendingTree</c> so its
        /// recordings are NOT in this list (#611 P1 follow-up); the primary
        /// lookup now walks <paramref name="committedTrees"/>, which production
        /// composes from CommittedTrees ++ PendingTree via
        /// <see cref="ComposeSearchTreesForReFlySuppression"/>. Tests pass a
        /// list directly; production passes the live property.</param>
        /// <param name="committedTrees">Trees searched for both BranchPoint
        /// topology lookup AND the active Re-Fly recording's PID. Production
        /// composes <see cref="RecordingStore.CommittedTrees"/> ++
        /// <see cref="PendingTreeSlot.PendingTree"/> via
        /// <see cref="ComposeSearchTreesForReFlySuppression"/>; tests pass a
        /// list directly. Despite the legacy parameter name, this list MUST
        /// include the pending tree for the load-window predicate to fire
        /// (#611 P1 follow-up).</param>
        /// <param name="suppressReason">On true, a structured human-readable
        /// reason for the log line including the relationship scope. On false,
        /// set to "not-suppressed-..." describing which gate clause rejected
        /// the suppression.</param>
        /// <returns>True iff the state-vector ProtoVessel should NOT be
        /// created because its world position would land on top of the
        /// active Re-Fly target AND the victim recording is in the active
        /// Re-Fly recording's parent chain.</returns>
        internal static bool ShouldSuppressStateVectorProtoVesselForActiveReFly(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (marker == null)
            {
                suppressReason = "not-suppressed-no-marker";
                return false;
            }

            if (string.IsNullOrEmpty(marker.ActiveReFlyRecordingId)
                || string.IsNullOrEmpty(marker.OriginChildRecordingId))
            {
                suppressReason = "not-suppressed-marker-fields-empty";
                return false;
            }

            // Placeholder pattern (active != origin AND no InPlaceContinuation
            // flag): the live vessel in scene is a fresh strip-spawned
            // vessel different from origin, not the same physical craft, so
            // the parent-chain doubled-ProtoVessel risk does not apply.
            // The post-#734 fork case (InPlaceContinuation=true with active
            // != origin) DOES need suppression -- the player is still flying
            // the same physical vessel as origin, just routed through a
            // separate provisional Recording.
            if (!ReFlySessionMarker.IsInPlaceContinuation(marker))
            {
                suppressReason = "not-suppressed-placeholder-pattern";
                return false;
            }

            // Accept both "relative" and "body-fixed-primary" for legacy
            // suppression decisions. Phase D keeps this helper for caller
            // compatibility, but create-time lookahead no longer performs a
            // live-PID anchor scan.
            bool branchSuppresses =
                string.Equals(resolutionBranch, "relative", StringComparison.Ordinal)
                || string.Equals(resolutionBranch, "body-fixed-primary", StringComparison.Ordinal);
            if (!branchSuppresses)
            {
                suppressReason = "not-suppressed-not-relative-frame";
                return false;
            }

            if (resolutionAnchorPid == 0u)
            {
                suppressReason = "not-suppressed-no-anchor-pid";
                return false;
            }

            // #611 P1 follow-up: the PID lookup MUST search the composed trees
            // (committed ++ pending) and not just the flat CommittedRecordings
            // list. At Re-Fly load time TryRestoreActiveTreeNode has just
            // detached this tree from CommittedTrees (and therefore from
            // CommittedRecordings) and re-stashed it as PendingTree, so the
            // flat list lookup that ran first would silently bail with
            // not-suppressed-active-rec-pid-unknown -- before the new
            // pending-tree topology walk got a chance to run -- and the
            // doubled ProtoVessel still got created.
            uint activeReFlyPid = 0u;
            string activePidSource = "<not-found>";
            if (committedTrees != null)
            {
                // If the marker tree contains no usable PID, keep the legacy
                // fallback so older/partial marker state can still suppress
                // via the first tree that has a complete active recording.
                if (TryFindActiveReFlyRecordingInSearchTrees(
                        committedTrees,
                        marker.ActiveReFlyRecordingId,
                        marker.TreeId,
                        requireNonZeroPid: true,
                        out RecordingTree _,
                        out Recording activePidRecording,
                        out string treePidSource,
                        out int _))
                {
                    activeReFlyPid = activePidRecording.VesselPersistentId;
                    activePidSource = treePidSource;
                }
            }
            if (activeReFlyPid == 0u && committedRecordings != null)
            {
                for (int i = 0; i < committedRecordings.Count; i++)
                {
                    Recording rec = committedRecordings[i];
                    if (rec == null) continue;
                    if (!string.Equals(rec.RecordingId, marker.ActiveReFlyRecordingId,
                            StringComparison.Ordinal))
                        continue;
                    activeReFlyPid = rec.VesselPersistentId;
                    activePidSource = "committed-recordings-flat-list";
                    break;
                }
            }

            if (activeReFlyPid == 0u)
            {
                int treeCount = committedTrees != null ? committedTrees.Count : 0;
                int recCount = committedRecordings != null ? committedRecordings.Count : 0;
                suppressReason = "not-suppressed-active-rec-pid-unknown searchTrees="
                    + treeCount + " committedRecordings=" + recCount
                    + " activeRecId=" + (marker.ActiveReFlyRecordingId ?? "<null>");
                return false;
            }

            if (resolutionAnchorPid != activeReFlyPid)
            {
                suppressReason = "not-suppressed-anchor-not-active-refly";
                return false;
            }

            // PR #574 review P2: the anchor-equality predicate is necessary
            // but not sufficient. A docking-target / rendezvous recording or
            // any sibling recording could legitimately be Relative-anchored
            // to the active vessel (cf. #583 / #584). Restrict suppression
            // to the user's actual case: the recording being mapped is in
            // the active Re-Fly recording's *parent* chain (i.e. the
            // recording from which the active was decoupled or otherwise
            // branched).
            if (string.IsNullOrEmpty(victimRecordingId))
            {
                suppressReason = "not-suppressed-no-victim-id";
                return false;
            }

            if (string.Equals(victimRecordingId, marker.ActiveReFlyRecordingId,
                    StringComparison.Ordinal))
            {
                // The active recording itself is already covered by the
                // SessionSuppressedSubtree gate (IsSuppressedByActiveSession);
                // this branch keeps the predicate idempotent for the unlikely
                // case that the active recording reaches this code path.
                suppressReason = "not-suppressed-victim-is-active";
                return false;
            }

            if (!IsRecordingInParentChainOfActiveReFly(
                    victimRecordingId,
                    marker.ActiveReFlyRecordingId,
                    committedTrees,
                    marker.TreeId,
                    out string walkTrace))
            {
                // #611: append the BFS walk trace so the rejection log line
                // carries enough detail to diagnose missing-active-tree /
                // missing-parent-BP / topology-mismatch cases.
                suppressReason = "not-suppressed-not-parent-of-refly-target walkTrace=("
                    + walkTrace + ")";
                return false;
            }

            // Bubble the trace into the success reason too so the
            // create-state-vector-suppressed log line can show the chain
            // that was matched. Helps reviewers / playtest log scrapers
            // confirm the gate fired for the right relationship.
            // #611 P1 follow-up: also include where the active PID was
            // resolved (which tree, or the flat fallback list) so the
            // load-window vs steady-state distinction is auditable.
            suppressReason = "refly-relative-anchor=active relationship=parent activePidSource="
                + activePidSource + " walkTrace=(" + walkTrace + ")";
            return true;
        }

        internal static bool ShouldSuppressStateVectorProtoVesselForActiveReFlyAtCreateTime(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            IPlaybackTrajectory traj,
            double currentUT,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (ShouldSuppressStateVectorProtoVesselForActiveReFly(
                    marker,
                    resolutionBranch,
                    resolutionAnchorPid,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out suppressReason))
            {
                return true;
            }

            string directReason = suppressReason;
            if (TryFindActiveReFlyRelativeLookaheadSuppression(
                    marker,
                    traj,
                    currentUT,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out string lookaheadReason))
            {
                suppressReason = string.Format(ic,
                    "{0} currentBranch={1} direct=({2})",
                    lookaheadReason,
                    resolutionBranch ?? "(null)",
                    directReason ?? "(none)");
                return true;
            }

            suppressReason = string.Format(ic,
                "{0} lookahead=({1})",
                directReason ?? "not-suppressed",
                lookaheadReason ?? "(none)");
            return false;
        }

        internal static bool ShouldRemoveStateVectorProtoVesselForActiveReFlyOnUpdate(
            ReFlySessionMarker marker,
            string resolutionBranch,
            uint resolutionAnchorPid,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            if (!ShouldSuppressStateVectorProtoVesselForActiveReFly(
                    marker,
                    resolutionBranch,
                    resolutionAnchorPid,
                    victimRecordingId,
                    committedRecordings,
                    committedTrees,
                    out suppressReason))
            {
                return false;
            }

            suppressReason = TrackingStationGhostSkipActiveReFlyRelativeUpdate
                + " " + suppressReason;
            return true;
        }

        private static bool TryFindActiveReFlyRelativeLookaheadSuppression(
            ReFlySessionMarker marker,
            IPlaybackTrajectory traj,
            double currentUT,
            string victimRecordingId,
            IReadOnlyList<Recording> committedRecordings,
            IReadOnlyList<RecordingTree> committedTrees,
            out string suppressReason)
        {
            suppressReason = "lookahead-disabled-recorded-anchor-chain";
            return false;
        }

        private static string GetActiveReFlyDeferredSessionKey(ReFlySessionMarker marker)
        {
            if (marker == null)
                return null;
            if (!string.IsNullOrEmpty(marker.SessionId))
                return marker.SessionId;
            return marker.ActiveReFlyRecordingId;
        }

        private static void MarkStateVectorGhostDeferredForActiveReFly(int recordingIndex)
        {
            string sessionKey = GetActiveReFlyDeferredSessionKey(SessionSuppressionState.ActiveMarker);
            if (string.IsNullOrEmpty(sessionKey))
                return;
            activeReFlyDeferredStateVectorGhostSessions[recordingIndex] = sessionKey;
        }

        private static bool IsStateVectorGhostDeferredForActiveReFlySession(
            int recordingIndex,
            out string sessionKey)
        {
            sessionKey = null;
            if (!activeReFlyDeferredStateVectorGhostSessions.TryGetValue(
                    recordingIndex,
                    out string storedSessionKey))
            {
                return false;
            }

            string activeSessionKey =
                GetActiveReFlyDeferredSessionKey(SessionSuppressionState.ActiveMarker);
            if (string.IsNullOrEmpty(activeSessionKey)
                || !string.Equals(storedSessionKey, activeSessionKey, StringComparison.Ordinal))
            {
                activeReFlyDeferredStateVectorGhostSessions.Remove(recordingIndex);
                return false;
            }

            sessionKey = storedSessionKey;
            return true;
        }

        private static bool TryFindActiveReFlyRecordingInSearchTrees(
            IReadOnlyList<RecordingTree> searchTrees,
            string activeRecordingId,
            string activeTreeId,
            bool requireNonZeroPid,
            out RecordingTree matchedTree,
            out Recording matchedRecording,
            out string source,
            out int treesSearched)
        {
            matchedTree = null;
            matchedRecording = null;
            source = "<not-found>";
            treesSearched = 0;

            if (searchTrees == null || string.IsNullOrEmpty(activeRecordingId))
                return false;

            RecordingTree fallbackTree = null;
            Recording fallbackRecording = null;
            string fallbackSource = null;
            bool hasActiveTreeId = !string.IsNullOrEmpty(activeTreeId);

            for (int i = 0; i < searchTrees.Count; i++)
            {
                RecordingTree tree = searchTrees[i];
                if (tree?.Recordings == null) continue;
                treesSearched++;
                bool isMarkerTree = hasActiveTreeId
                    && string.Equals(tree.Id, activeTreeId, StringComparison.Ordinal);
                if (TryAcceptActiveReFlyRecordingTree(
                        tree,
                        activeRecordingId,
                        requireNonZeroPid,
                        isMarkerTree ? "marker-tree" : "search-tree",
                        out matchedRecording,
                        out source))
                {
                    if (isMarkerTree)
                    {
                        matchedTree = tree;
                        return true;
                    }

                    if (fallbackTree == null)
                    {
                        fallbackTree = tree;
                        fallbackRecording = matchedRecording;
                        fallbackSource = source;
                    }

                    if (!hasActiveTreeId)
                        break;
                }
            }

            if (fallbackTree == null)
                return false;

            matchedTree = fallbackTree;
            matchedRecording = fallbackRecording;
            source = fallbackSource;
            return true;
        }

        private static bool TryAcceptActiveReFlyRecordingTree(
            RecordingTree tree,
            string activeRecordingId,
            bool requireNonZeroPid,
            string sourcePrefix,
            out Recording matchedRecording,
            out string source)
        {
            matchedRecording = null;
            source = "<not-found>";

            if (tree == null || tree.Recordings == null)
                return false;
            if (!tree.Recordings.TryGetValue(activeRecordingId, out Recording recording)
                || recording == null)
            {
                return false;
            }
            if (requireNonZeroPid && recording.VesselPersistentId == 0u)
                return false;

            matchedRecording = recording;
            source = sourcePrefix + ":" + (tree.Id ?? "<no-id>");
            return true;
        }

        /// <summary>
        /// Pure: walks the BranchPoint topology parent-ward from the active
        /// Re-Fly recording and returns true when <paramref name="victimRecordingId"/>
        /// is encountered in any parent BP's <see cref="BranchPoint.ParentRecordingIds"/>.
        /// Mirror of the child-ward closure in
        /// <see cref="EffectiveState.ComputeSessionSuppressedSubtree"/>, traversed
        /// in the opposite direction. Returns false on any structural defect
        /// (null trees / missing tree / missing recording / cycle), erring on
        /// the side of NOT suppressing.
        ///
        /// <para><b>Observability (#611):</b> emits a single Verbose
        /// <c>[GhostMap] parent-chain-walk</c> log line per call describing
        /// the search outcome (active-found-in tree id / source label,
        /// visitedBPs count + ids, parents-encountered count + ids, terminate
        /// reason). Mirrors the existing <c>SessionSuppressedSubtree</c>
        /// summary line so the same structured-grep tooling works for both
        /// closures.</para>
        ///
        /// <para><b>#611 fix:</b> at Re-Fly load time the active recording
        /// lives in <see cref="PendingTreeSlot.PendingTree"/>, NOT
        /// <see cref="RecordingStore.CommittedTrees"/> (the committed copy is
        /// removed by <c>TryRestoreActiveTreeNode</c>'s post-splice
        /// <c>RemoveCommittedTreeById</c>). Callers must therefore compose
        /// <paramref name="searchTrees"/> from BOTH committed + pending so
        /// the active-tree lookup succeeds whether the load just happened
        /// or the player is in steady-state flight.</para>
        ///
        /// <para><b>#614 fix:</b> the walk now follows BOTH BranchPoint-parents
        /// AND chain-predecessors. Optimizer splits
        /// (<see cref="RecordingStore"/> RunOptimizationSplitPass) connect
        /// chain segments via shared <see cref="Recording.ChainId"/> alone — the
        /// second half receives no <see cref="Recording.ParentBranchPointId"/>.
        /// The pre-#614 BP-only walk silently terminated at the first chain
        /// segment it reached, missing the root and any earlier segments. The
        /// fix seeds the walk with the active recording's chain predecessor
        /// (when present) and enqueues a chain predecessor for every recording
        /// reached during fan-out. <c>chainHops</c> in the walk trace counts
        /// these enqueues so a future regression shows up as <c>chainHops=0</c>
        /// on a topology that should have chain links.</para>
        /// </summary>
        /// <param name="searchTrees">Trees to search. Production callers
        /// compose <see cref="RecordingStore.CommittedTrees"/> ++
        /// <see cref="PendingTreeSlot.PendingTree"/> (when present). Tests
        /// pass an explicit list.</param>
        /// <param name="walkTrace">Diagnostic summary for the caller's
        /// structured log line. Always populated, even on early-return.</param>
        internal static bool IsRecordingInParentChainOfActiveReFly(
            string victimRecordingId,
            string activeRecordingId,
            IReadOnlyList<RecordingTree> searchTrees,
            out string walkTrace)
        {
            return IsRecordingInParentChainOfActiveReFly(
                victimRecordingId,
                activeRecordingId,
                searchTrees,
                activeTreeId: null,
                out walkTrace);
        }

        internal static bool IsRecordingInParentChainOfActiveReFly(
            string victimRecordingId,
            string activeRecordingId,
            IReadOnlyList<RecordingTree> searchTrees,
            string activeTreeId,
            out string walkTrace)
        {
            walkTrace = "no-input";
            if (string.IsNullOrEmpty(victimRecordingId)
                || string.IsNullOrEmpty(activeRecordingId)
                || searchTrees == null)
            {
                return false;
            }

            // Locate the tree containing the active recording. Search every
            // input tree so a Pending-Limbo-stashed tree (Re-Fly load window)
            // is found alongside committed trees.
            if (!TryFindActiveReFlyRecordingInSearchTrees(
                    searchTrees,
                    activeRecordingId,
                    activeTreeId,
                    requireNonZeroPid: false,
                    out RecordingTree tree,
                    out Recording active,
                    out string _,
                    out int treesSearched))
            {
                walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "active-not-found activeId={0} treeId={1} treesSearched={2}",
                    activeRecordingId, activeTreeId ?? "<none>", treesSearched);
                return false;
            }
            // #614: walk both BranchPoint-parents AND chain-predecessors. Optimizer
            // splits (CommittedListOptimizer.RunOptimizationSplitPass) connect chain segments
            // via shared ChainId / ChainIndex, NOT via ParentBranchPointId — so the
            // older BP-only walk silently terminated when it reached a chain segment
            // whose parent was a previous chain segment with no BP link, missing the
            // root and any earlier chain segments. Seed the walk with the active
            // recording's parent BP AND its chain predecessor; for every recording
            // reached, enqueue its parent BP AND its chain predecessor.
            var pendingBPs = new Queue<string>();
            var pendingRecs = new Queue<string>();
            var visitedBPs = new HashSet<string>();
            var visitedRecs = new HashSet<string>();
            var bpTrace = new List<string>();
            var parentTrace = new List<string>();
            int bpsNotFound = 0;
            int chainHopsEnqueued = 0;
            int chainHopsAncestorPredecessor = 0;

            // Seed: parent BP of the active recording (may be empty for chain
            // mid-segments and tree roots).
            if (!string.IsNullOrEmpty(active.ParentBranchPointId))
                pendingBPs.Enqueue(active.ParentBranchPointId);

            // Seed: chain predecessor of the active recording. The active itself
            // is not a victim (the victim-is-active short-circuit fires above),
            // but the active may sit mid-chain whose previous segment IS a victim.
            string activePredecessorId = TryFindChainPredecessor(tree, active);
            if (!string.IsNullOrEmpty(activePredecessorId))
            {
                pendingRecs.Enqueue(activePredecessorId);
                chainHopsEnqueued++;
            }

            // Bail if neither seed produced any work: the active is a true root
            // with no chain predecessor.
            if (pendingBPs.Count == 0 && pendingRecs.Count == 0)
            {
                walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "active-has-no-parent activeId={0} treeId={1}",
                    activeRecordingId, tree.Id ?? "<no-id>");
                return false;
            }

            // Helper-style local: process a parent recording id (whether reached
            // via BP-parent fan-out or chain-predecessor link). Returns true when
            // the victim is found.
            bool VisitRecordingId(string recId, string discoverySource)
            {
                if (string.IsNullOrEmpty(recId) || !visitedRecs.Add(recId))
                    return false;

                parentTrace.Add(recId);
                if (string.Equals(recId, victimRecordingId, StringComparison.Ordinal))
                    return true;

                if (!tree.Recordings.TryGetValue(recId, out Recording rec) || rec == null)
                    return false;

                // BP link to next ancestor up.
                if (!string.IsNullOrEmpty(rec.ParentBranchPointId))
                    pendingBPs.Enqueue(rec.ParentBranchPointId);

                // Chain link to previous segment (no BP). #614: this is the leg
                // the old walk missed — optimizer-split chain predecessors share
                // ChainId but have no ParentBranchPointId on the second half.
                string predId = TryFindChainPredecessor(tree, rec);
                if (!string.IsNullOrEmpty(predId))
                {
                    pendingRecs.Enqueue(predId);
                    chainHopsEnqueued++;
                    chainHopsAncestorPredecessor++;
                }

                return false;
            }

            while (pendingBPs.Count > 0 || pendingRecs.Count > 0)
            {
                // Drain any chain-predecessor recordings first so the walk
                // surfaces direct chain ancestry before fanning out via BPs.
                while (pendingRecs.Count > 0)
                {
                    string recId = pendingRecs.Dequeue();
                    if (VisitRecordingId(recId, "chain-predecessor"))
                    {
                        walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "found-victim-in-parent-chain activeId={0} treeId={1} victim={2} " +
                            "visitedBPs={3} parentsEncountered={4} chainHops={5} bps=[{6}] parents=[{7}]",
                            activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                            visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued,
                            string.Join(",", bpTrace.ToArray()),
                            string.Join(",", parentTrace.ToArray()));
                        return true;
                    }
                }

                if (pendingBPs.Count == 0) break;

                string bpId = pendingBPs.Dequeue();
                if (string.IsNullOrEmpty(bpId) || !visitedBPs.Add(bpId))
                    continue;

                BranchPoint bp = null;
                for (int i = 0; i < tree.BranchPoints.Count; i++)
                {
                    if (tree.BranchPoints[i] != null
                        && string.Equals(tree.BranchPoints[i].Id, bpId, StringComparison.Ordinal))
                    {
                        bp = tree.BranchPoints[i];
                        break;
                    }
                }
                if (bp == null)
                {
                    bpsNotFound++;
                    bpTrace.Add(bpId + ":not-found");
                    continue;
                }
                bpTrace.Add(bpId + ":parents=" + (bp.ParentRecordingIds?.Count ?? 0));
                if (bp.ParentRecordingIds == null) continue;

                for (int i = 0; i < bp.ParentRecordingIds.Count; i++)
                {
                    if (VisitRecordingId(bp.ParentRecordingIds[i], "bp-parent"))
                    {
                        walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "found-victim-in-parent-chain activeId={0} treeId={1} victim={2} " +
                            "visitedBPs={3} parentsEncountered={4} chainHops={5} bps=[{6}] parents=[{7}]",
                            activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                            visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued,
                            string.Join(",", bpTrace.ToArray()),
                            string.Join(",", parentTrace.ToArray()));
                        return true;
                    }
                }
            }

            // Ancestor-walk summary: chainHops counts every chain-predecessor
            // enqueue (active-seed + every parent recording's chain predecessor)
            // so a future regression of the #614-style bug shows up here as a
            // suspicious chainHops=0 on a topology that should have chain links.
            walkTrace = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "exhausted-without-victim activeId={0} treeId={1} victim={2} " +
                "visitedBPs={3} parentsEncountered={4} chainHops={5} chainHopsViaAncestors={6} " +
                "bpsNotFound={7} bps=[{8}] parents=[{9}]",
                activeRecordingId, tree.Id ?? "<no-id>", victimRecordingId,
                visitedBPs.Count, visitedRecs.Count, chainHopsEnqueued, chainHopsAncestorPredecessor,
                bpsNotFound,
                string.Join(",", bpTrace.ToArray()),
                string.Join(",", parentTrace.ToArray()));
            return false;
        }

        /// <summary>
        /// #614: returns the recording-id of <paramref name="rec"/>'s chain
        /// predecessor in <paramref name="tree"/>, or null when the recording
        /// is standalone, is the first chain segment (ChainIndex == 0), or
        /// when no matching predecessor exists in the tree.
        /// </summary>
        /// <remarks>
        /// Optimizer splits (<see cref="RecordingStore"/> RunOptimizationSplitPass)
        /// link chain segments via shared <see cref="Recording.ChainId"/> and
        /// monotonically increasing <see cref="Recording.ChainIndex"/> with the
        /// same <see cref="Recording.ChainBranch"/>. The second half does NOT
        /// receive a <see cref="Recording.ParentBranchPointId"/> — the chain id
        /// is the only ancestry link.
        /// </remarks>
        internal static string TryFindChainPredecessor(RecordingTree tree, Recording rec)
        {
            if (tree?.Recordings == null || rec == null) return null;
            if (string.IsNullOrEmpty(rec.ChainId)) return null;
            if (rec.ChainIndex <= 0) return null;

            int targetIdx = rec.ChainIndex - 1;
            foreach (var kvp in tree.Recordings)
            {
                Recording other = kvp.Value;
                if (other == null) continue;
                if (!string.Equals(other.ChainId, rec.ChainId, StringComparison.Ordinal))
                    continue;
                if (other.ChainBranch != rec.ChainBranch) continue;
                if (other.ChainIndex != targetIdx) continue;
                return other.RecordingId;
            }
            return null;
        }
    }
}
