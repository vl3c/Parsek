using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Parsek
{
    // The atomic marker write: the provisional fork and its tree, the prior provisional reap for the rewind point, fork attach / detach and the recording lookups.
    internal static partial class RewindInvoker
    {
        /// <summary>
        /// §6.3 step 4 critical section. Runs synchronously; throws MUST
        /// leave the global state untouched (we roll back the provisional
        /// add before rethrowing). NO yield, NO await, NO deferred save.
        ///
        /// <para>
        /// Always creates a fresh provisional Recording (the "fork"). The
        /// recorder's first flush appends data to this fork; the origin
        /// recording is never mutated. Two flavors of fork:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     <b>In-place continuation</b> (issue #734) — the Limbo-restore
        ///     kept the origin recording alive in the restored tree and the
        ///     strip-selected vessel pid matches the origin's
        ///     <see cref="Recording.VesselPersistentId"/>. The fork inherits
        ///     the origin's vessel identity and freezes the origin's
        ///     trajectory under the fork's own pre-Re-Fly anchor snapshot
        ///     so resolver paths keyed by
        ///     <see cref="ReFlySessionMarker.ActiveReFlyRecordingId"/> still
        ///     see the original trajectory data without reading the live
        ///     origin recording. <see cref="ReFlySessionMarker.InPlaceContinuation"/>
        ///     is set so <see cref="ReFlySessionMarker.ResolveInPlaceContinuationTarget"/>
        ///     can swap the active recording id during restore.
        ///   </description></item>
        ///   <item><description>
        ///     <b>New-recording path</b> — origin tree is gone or the active
        ///     pid does not match the origin's pid. The fork is a blank
        ///     placeholder that the recorder populates as the player flies.
        ///   </description></item>
        /// </list>
        /// </summary>
        internal static void AtomicMarkerWrite(
            RewindPoint rp, ChildSlot selected,
            PostLoadStripResult stripResult, string sessionId)
        {
            if (rp == null) throw new ArgumentNullException(nameof(rp));
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            if (stripResult.SelectedPid == 0u)
                throw new InvalidOperationException("AtomicMarkerWrite: no selected vessel");

            var scenario = ParsekScenario.Instance;
            if (object.ReferenceEquals(null, scenario))
                throw new InvalidOperationException("AtomicMarkerWrite: no ParsekScenario instance");

            // Resolve the origin recording. If it survived the Limbo-restore,
            // is still the slot's effective tip, and its VesselPersistentId
            // matches the strip-selected pid, the recorder will continue
            // writing into THIS recording; we point the marker at it directly
            // with no placeholder. If the slot already supersedes origin, a
            // fresh placeholder is required so commit appends priorTip -> new
            // instead of creating origin -> priorTip -> origin cycles.
            //
            // Composite walker (Task A2 rewired ChildSlot.EffectiveRecordingId
            // to use EffectiveState.EffectiveTipRecordingId): traces
            // slot.OriginChildRecordingId → (chain) → (supersede) → final tip.
            // For nested Re-Fly chains the previous fork is reached via
            // chain-hop + supersede-hop in a single traversal — load-bearing
            // for fix-supersede-identity-scope plan §"Edge cases" #4.
            string priorTip = selected.EffectiveRecordingId(scenario.RecordingSupersedes);
            Recording originChild = FindRecordingById(selected.OriginChildRecordingId);
            bool originIsPriorTip = string.Equals(
                priorTip,
                selected.OriginChildRecordingId,
                StringComparison.Ordinal);
            // For a slot that has already been Re-flown but did not auto-seal
            // (e.g. crashed terminal => MergeState.CommittedProvisional), the
            // supersede chain has extended past origin to a prior Re-Fly's
            // recording. Inheriting from origin would lose the latest vessel
            // snapshot and identity that the previous Re-Fly's commit stamped
            // (and would also leave RestoreActiveTreeFromPending's marker-swap
            // path inactive — observed in 2026-05-06_2156 where the recorder
            // never armed and the live vessel stayed as the original full
            // rocket instead of the slot's probe). When priorTip resolves to a
            // valid committed recording with a matching vessel pid, fork from
            // it instead so the in-place path inherits the up-to-date state.
            Recording chainTipRec = originIsPriorTip
                ? originChild
                : FindRecordingById(priorTip);
            Recording inheritFrom = chainTipRec ?? originChild;
            // originChild was looked up via FindRecordingById which scans
            // CommittedRecordings, so a non-null originChild is by construction
            // a committed recording. The previous redundant
            // `IsCommittedRecording(originChild)` clause has been dropped.
            //
            // The third clause guards a degenerate case: priorTip references a
            // recording that is no longer in the committed list (orphan
            // supersede). With chainTipRec=null, inheritFrom falls back to
            // origin, but origin is no longer authoritative for the slot's
            // current state. In that case we keep the placeholder branch
            // (inPlaceContinuation=false) so the marker still routes through
            // the priorTip-aware supersede commit path.
            bool inPlaceContinuation =
                inheritFrom != null
                && inheritFrom.VesselPersistentId == stripResult.SelectedPid
                && (originIsPriorTip || chainTipRec != null);

            Recording provisional = null;
            string activeReFlyRecordingId = null;
            string treeIdForMarker = null;
            bool addedToPendingTree = false;
            RecordingTree pendingTreeForFork = null;
            TryResolveSelectedSlotRootPartPersistentId(
                rp,
                selected.SlotIndex,
                out uint selectedRootPartPersistentId);

            // Bug fix-refly-abandon-and-fork-persist §Bug1: before this
            // session adds its provisional, reap any prior NotCommitted
            // provisional that targets the same RewindPoint from a
            // different (now-abandoned) session. The user just clicked
            // Re-Fly on the same RP slot, so the prior attempt's
            // provisional is orphaned by construction. Without this
            // reap, the orphan survives in tree.Recordings dicts and
            // gets re-added to RecordingStore.CommittedRecordings by
            // FinalizeTreeCommit (RecordingStore.cs:1363-1386), and the
            // next merge's AppendRelations closure walk writes an
            // invalid `old=<NotCommitted-orphan> → new=<new-fork>`
            // supersede row.
            //
            // Hoisted OUTSIDE the try block below so the reap is an
            // independent cleanup step: if BuildProvisionalRecording
            // throws after a successful reap, the orphans stay gone
            // (which is correct — they were already invariant violations)
            // and the catch block does not need to undo the reap. A reap
            // failure propagates unhandled out of AtomicMarkerWrite,
            // which is the desired behavior — a corrupted store should
            // not silently fall through to writing a new marker.
            int reaped = ReapPriorProvisionalsForRp(rp.RewindPointId, sessionId);
            if (reaped > 0)
            {
                ParsekLog.Info(SessionTag,
                    $"AtomicMarkerWrite: reaped {reaped} prior NotCommitted provisional(s) " +
                    $"for rp={rp.RewindPointId} before new sess={sessionId}");
            }

            ReFlySessionMarker marker;
            try
            {
                // BuildProvisionalRecording previously also wrote
                // `SupersedeTargetId = selected.OriginChildRecordingId` as
                // a placeholder; that initial assignment was removed
                // because the marker path needs `priorTip` (which differs
                // from origin when the slot already supersedes origin),
                // and writing both would shadow the meaningful value.
                provisional = BuildProvisionalRecording(rp, selected, originChild, sessionId, stripResult);
                // The merge path consumes marker.SupersedeTargetId. This
                // recording-level copy is a transient diagnostic on the
                // NotCommitted placeholder and is cleared with the rest of
                // the provisional fields at merge time.
                provisional.SupersedeTargetId = priorTip;

                if (inPlaceContinuation)
                {
                    // Issue #734: the in-place attempt is forked into a
                    // separate provisional Recording instead of mutating the
                    // origin object. The fork inherits the inheritance source's
                    // vessel identity (so the recorder's per-vessel tracking
                    // continues unchanged), generation depth, and a defensive
                    // copy of its vessel/ghost snapshots so any code path that
                    // queries the active Re-Fly recording's snapshot before
                    // the recorder has refreshed it (ghost build, antenna
                    // registration) still sees a valid
                    // payload. The fork freezes the inheritance source's
                    // trajectory under the fork's own pre-Re-Fly anchor
                    // snapshot so resolver paths keyed by
                    // ActiveReFlyRecordingId still see the prior trajectory
                    // data. The inheritance source is left
                    // untouched: no live mutation, no session tagging, no
                    // rollback snapshot needed for Discard. Chain identity
                    // (ChainId/Index/Branch) is intentionally NOT copied so
                    // the supersede table is the only authority on chain-tip
                    // resolution, matching the non-in-place placeholder
                    // pattern.
                    //
                    // When the slot has prior CommittedProvisional Re-Fly
                    // recordings (priorTip != origin), the inheritance source
                    // is the chain tip's recording rather than origin so the
                    // new fork picks up the latest committed snapshot/identity
                    // — without that, the second-Re-Fly-of-an-unsealed-slot
                    // path falls through to the placeholder branch, the
                    // recorder never arms (RestoreActiveTreeFromPending's
                    // marker-swap is gated on inPlaceContinuation), and the
                    // live vessel stays as the original full assembly.
                    CopyInheritedIdentityForFork(provisional, inheritFrom);
                    TagForkInitialSegmentPhase(provisional, stripResult.SelectedVessel, sessionId);
                    bool snapshotRefreshedFromLive = TryRefreshForkSnapshotsFromLiveVessel(
                        provisional, stripResult.SelectedVessel, sessionId);
                    provisional.CapturePreReFlyAnchorTrajectoryFrom(inheritFrom, sessionId);
                    pendingTreeForFork = FindTreeForReFlyFork(inheritFrom.TreeId);
                    bool inheritedFromChainTip = !object.ReferenceEquals(inheritFrom, originChild)
                        && inheritFrom != null;
                    string snapshotProvenance = snapshotRefreshedFromLive ? "live-refresh" : "copied";
                    ParsekLog.Info(InvokeTag,
                        $"AtomicMarkerWrite: in-place continuation forked — fork " +
                        $"{provisional.RecordingId} supersedes priorTip {priorTip ?? "<none>"} " +
                        $"(origin={selected.OriginChildRecordingId ?? "<none>"} " +
                        $"inheritedFrom={(inheritedFromChainTip ? "chain-tip" : "origin")} " +
                        $"sourceRec={inheritFrom.RecordingId ?? "<no-id>"}; " +
                        $"source not mutated; pre-Re-Fly anchor snapshot captured on the fork; " +
                        $"vesselSnapshot={(provisional.VesselSnapshot != null ? snapshotProvenance : "<none>")}; " +
                        $"ghostSnapshot={(provisional.GhostVisualSnapshot != null ? snapshotProvenance : "<none>")}; " +
                        $"generation={provisional.Generation}; " +
                        $"treeAttach={(pendingTreeForFork != null ? "eager" : "deferred-to-restore")})");
                }

                activeReFlyRecordingId = provisional.RecordingId;
                treeIdForMarker = provisional.TreeId;

                CheckpointHookForTesting?.Invoke("CheckpointA:BeforeProvisional");
                RecordingStore.AddProvisional(provisional);
                if (pendingTreeForFork != null
                    && EnsureForkAttachedToTree(pendingTreeForFork, provisional, "AtomicMarkerWrite"))
                {
                    addedToPendingTree = true;
                }
                CheckpointHookForTesting?.Invoke("CheckpointA:AfterProvisional");

                marker = new ReFlySessionMarker
                {
                    SessionId = sessionId,
                    TreeId = treeIdForMarker,
                    ActiveReFlyRecordingId = activeReFlyRecordingId,
                    OriginChildRecordingId = selected.OriginChildRecordingId,
                    SupersedeTargetId = priorTip,
                    RewindPointId = rp.RewindPointId,
                    SelectedRootPartPersistentId = selectedRootPartPersistentId,
                    InvokedUT = SafeNow(),
                    // Stable RP cutoff captured directly from the RewindPoint.
                    // Decoupled from SafeNow() / onFlightReady deferrals so
                    // gates that need "before the rewind moment" semantics
                    // (see ReFlySessionMarker.RewindPointUT XML doc) have an
                    // exact reference UT rather than the drifted InvokedUT.
                    RewindPointUT = rp.UT,
                    InvokedRealTime = DateTime.UtcNow.ToString("o"),
                    PreSessionBranchPointIds = SnapshotTreeBranchPointIds(treeIdForMarker),
                    InPlaceContinuation = inPlaceContinuation,
                };

                CheckpointHookForTesting?.Invoke("CheckpointB:BeforeMarker");
                scenario.ActiveReFlySessionMarker = marker;
                scenario.BumpSupersedeStateVersion();
                // Re-enable the Esc-menu Revert button (KSP grays it out
                // because the loaded vessel is mid-flight, not PRELAUNCH;
                // see ReFlyRevertButtonGate doc-comment).
                ReFlyRevertButtonGate.Apply("AtomicMarkerWrite");
                CheckpointHookForTesting?.Invoke("CheckpointB:AfterMarker");
            }
            catch
            {
                // Roll back the provisional and the marker so no half-written
                // pair leaks out of the critical section. Both clears are
                // idempotent (RemoveCommittedInternal returns false if absent;
                // marker clear is a null-assignment). The in-place fork also
                // has to be removed from the pending tree if we attached it
                // before the throw.
                if (provisional != null)
                {
                    RecordingStore.RemoveCommittedInternal(provisional);
                    if (addedToPendingTree && pendingTreeForFork != null)
                        DetachForkFromTreeForRollback(pendingTreeForFork, provisional);
                }
                try
                {
                    if (ParsekScenario.Instance != null)
                        ParsekScenario.Instance.ClearActiveReFlySessionMarker("marker-cleared");
                    else
                        Parsek.Rendering.RenderSessionState.Clear("marker-cleared");
                    ReFlyRevertButtonGate.Apply("AtomicMarkerWrite:rollback");
                }
                catch { /* idempotent rollback; swallow secondary failure */ }
                throw;
            }

            // Nest the live fork under its tree's existing mission folder so the
            // recordings table renders it inside the group during the active
            // Re-Fly session instead of floating at the table root. Resolve the
            // tree from the eager in-place handle when present, else look it up
            // by the fork's TreeId (covers the placeholder branch that defers
            // tree attach to RestoreActiveTreeFromPending). No-op when the tree
            // has no auto-generated folder. Deliberately OUTSIDE the atomic
            // try/catch above: grouping is cosmetic, not part of the
            // provisional+marker critical section, so a thrown marker write
            // rolls back the provisional without leaking a created debris/crew
            // subgroup mapping.
            RecordingGroupStore.AssignTreeMemberToExistingAutoGroup(
                pendingTreeForFork ?? FindTreeForReFlyFork(provisional.TreeId),
                provisional);

            // The quickload-resume context can be armed before this method runs
            // on async FLIGHT loads. Refresh after the marker exists so recorder
            // resume consumes the Re-Fly active-only trim scope instead of the
            // pre-marker TreeWide fallback.
            try
            {
                ParsekScenario.RefreshPendingQuickloadTrimScope();
            }
            catch (Exception ex)
            {
                ParsekLog.Warn(InvokeTag,
                    $"AtomicMarkerWrite: quickload trim-scope refresh threw (non-fatal): {ex.Message}");
            }

            // A new session owns the retirement hand-over slot: drop anything an
            // earlier session left there so a stale Recording reference can never
            // outlive its session (TryTake also gates on SessionId, so this is
            // lifetime hygiene rather than correctness).
            ReFlyProvisionalRetirement.Clear("new-refly-session");

            ParsekLog.Info(SessionTag,
                $"Started sess={sessionId} rp={rp.RewindPointId} slot={selected.SlotIndex} " +
                $"provisional={activeReFlyRecordingId} " +
                $"origin={selected.OriginChildRecordingId ?? "<none>"} " +
                $"supersedeTarget={priorTip ?? "<none>"} " +
                $"selectedRootPartPid={selectedRootPartPersistentId} " +
                $"tree={treeIdForMarker ?? "<none>"} " +
                $"inPlaceContinuation={inPlaceContinuation}");
        }

        /// <summary>
        /// Locates the in-memory tree that owns <paramref name="treeId"/> so
        /// the in-place fork (issue #734) can attach itself to the tree the
        /// recorder will eventually flush into. AtomicMarkerWrite usually
        /// runs after <c>TryRestoreActiveTreeNode</c> stashes the saved tree
        /// as pending-Limbo, so <see cref="PendingTreeSlot.PendingTree"/> is
        /// the common target. The lookup additionally falls back to
        /// <see cref="RecordingStore.CommittedTrees"/> for sessions whose
        /// committed copy was never detached, and finally to
        /// <c>ParsekFlight.Instance.ActiveTreeForSerialization</c> for the
        /// async-load race where <c>RestoreActiveTreeFromPending</c> fired
        /// FIRST (popping the pending tree and starting the recorder)
        /// before AtomicMarkerWrite ran - in that window the tree no
        /// longer lives in PendingTree, and TryRestoreActiveTreeNode's
        /// <c>RemoveCommittedTreeById</c> already pulled the prior copy
        /// out of CommittedTrees. Without this fallback the fork would land
        /// only in <see cref="RecordingStore.CommittedRecordings"/> with no
        /// tree home, the recorder's flush would warn-and-stop, and the
        /// reconciliation pass at the top of RestoreActiveTreeFromPending
        /// has already completed and would not re-fire.
        /// </summary>
        internal static RecordingTree FindTreeForReFlyFork(string treeId)
        {
            if (string.IsNullOrEmpty(treeId))
                return null;

            RecordingTree pending = PendingTreeSlot.PendingTree;
            if (pending != null
                && string.Equals(pending.Id, treeId, StringComparison.Ordinal))
            {
                return pending;
            }

            var committedTrees = RecordingStore.CommittedTrees;
            if (committedTrees != null)
            {
                for (int i = 0; i < committedTrees.Count; i++)
                {
                    var committed = committedTrees[i];
                    if (committed == null) continue;
                    if (string.Equals(committed.Id, treeId, StringComparison.Ordinal))
                        return committed;
                }
            }

            // Live activeTree fallback for the AtomicMarkerWrite-late race.
            // ParsekFlight.Instance is null outside flight scenes (and in
            // tests that do not set it up), so guard defensively.
            var live = ParsekFlight.Instance?.ActiveTreeForSerialization;
            if (live != null
                && string.Equals(live.Id, treeId, StringComparison.Ordinal))
            {
                return live;
            }

            return null;
        }

        /// <summary>
        /// Bug fix-refly-abandon-and-fork-persist §Bug1: before a new Re-Fly
        /// session attaches its provisional to the same RewindPoint, reap any
        /// prior NotCommitted provisional from an earlier (abandoned) session
        /// targeting that RP. The retry path's marker clear (in
        /// <c>End reason=retry</c>) does not touch the prior provisional
        /// Recording, and <see cref="LoadTimeSweep.RemoveDiscardRecordings"/>
        /// historically only removed the recording from the flat
        /// <c>committedRecordings</c> list — its node in the owning tree's
        /// <c>Recordings</c> dict survived. <see cref="TreeCommitService.FinalizeTreeCommit"/>
        /// (<c>RecordingStore.cs:1363-1386</c>) then re-added it on the next
        /// commit pass, and the closure walk in
        /// <see cref="EffectiveState.EnqueuePidPeerSiblings"/> enqueued the
        /// orphan as a same-PID peer of the new TIP, ultimately writing an
        /// invalid <c>RECORDING_SUPERSEDE_RELATION</c> with
        /// <c>oldRecordingId</c> pointing at a NotCommitted recording — a
        /// data-model invariant violation.
        ///
        /// <para>
        /// This helper walks every collection that can hold a Recording:
        /// the flat <see cref="RecordingStore.CommittedRecordings"/> list,
        /// every committed tree's <c>Recordings</c> dict, the pending tree,
        /// and the live active tree (the s11 evidence case — the zombie was
        /// reachable only from <see cref="ParsekFlight.ActiveTreeForSerialization"/>).
        /// Sidecar files on disk are also removed so a future
        /// <c>ParsekFileCollector</c> pass does not warn about them.
        /// </para>
        ///
        /// <para>
        /// Idempotent: a second invocation finds no victims and returns 0.
        /// Crash-safe: the reap runs BEFORE the new session's marker is
        /// written, so a mid-reap crash leaves the next OnLoad's
        /// <see cref="LoadTimeSweep"/> to mop up any half-reaped orphans
        /// (their <c>CreatingSessionId</c> still names a session with no
        /// live marker).
        /// </para>
        /// </summary>
        internal static int ReapPriorProvisionalsForRp(string rpId, string newSessionId)
        {
            if (string.IsNullOrEmpty(rpId))
                return 0;

            // 1. Identify victims: NotCommitted, tagged to this RP, from a
            //    different session. Snapshot the ids first (don't mutate
            //    CommittedRecordings during iteration).
            var victimIds = new List<string>();
            var victimRecs = new List<Recording>();
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var rec = committed[i];
                if (rec == null) continue;
                if (rec.MergeState != MergeState.NotCommitted) continue;
                if (!string.Equals(rec.ProvisionalForRpId, rpId, StringComparison.Ordinal)) continue;
                if (string.Equals(rec.CreatingSessionId, newSessionId, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(rec.RecordingId)) continue;

                victimIds.Add(rec.RecordingId);
                victimRecs.Add(rec);
            }

            if (victimIds.Count == 0)
                return 0;

            // 2. Remove victims from every collection that can hold them.
            //    Track removal counts per collection for the per-victim log so
            //    a future regression can identify which collection retained
            //    the leak.
            int totalReaped = 0;
            for (int v = 0; v < victimIds.Count; v++)
            {
                string id = victimIds[v];
                var rec = victimRecs[v];
                string priorSess = rec.CreatingSessionId ?? "<no-sess>";

                // Flat list.
                bool fromFlatList = RecordingStore.RemoveCommittedById(id);

                // Every committed tree's Recordings dict + BackgroundMap.
                int fromCommittedTrees = 0;
                var committedTrees = RecordingStore.CommittedTrees;
                if (committedTrees != null)
                {
                    for (int t = 0; t < committedTrees.Count; t++)
                    {
                        var tree = committedTrees[t];
                        if (tree?.Recordings != null && tree.Recordings.Remove(id))
                        {
                            tree.RebuildBackgroundMap();
                            fromCommittedTrees++;
                        }
                    }
                }

                // Pending tree.
                bool fromPendingTree = false;
                var pending = PendingTreeSlot.PendingTree;
                if (pending?.Recordings != null && pending.Recordings.Remove(id))
                {
                    pending.RebuildBackgroundMap();
                    fromPendingTree = true;
                }

                // Live active tree (the s11 evidence case).
                bool fromActiveTree = false;
                var active = ParsekFlight.Instance?.ActiveTreeForSerialization;
                if (active?.Recordings != null && active.Recordings.Remove(id))
                {
                    active.RebuildBackgroundMap();
                    fromActiveTree = true;
                }

                // Sidecar files on disk.
                try { RecordingFileJanitor.DeleteRecordingFiles(rec); }
                catch (Exception ex)
                {
                    ParsekLog.Warn(SessionTag,
                        $"ReapPriorProvisional: DeleteRecordingFiles threw for rec={id} " +
                        $"priorSess={priorSess} newSess={newSessionId} rp={rpId}: " +
                        $"{ex.GetType().Name}: {ex.Message} " +
                        "(in-memory removal succeeded; sidecar files may leak on disk)");
                }

                ParsekLog.Info(SessionTag,
                    $"ReapPriorProvisional: removed orphan rec={id} priorSess={priorSess} " +
                    $"newSess={newSessionId} rp={rpId} " +
                    $"removedFromFlatList={fromFlatList} " +
                    $"removedFromCommittedTrees={fromCommittedTrees.ToString(CultureInfo.InvariantCulture)} " +
                    $"removedFromPendingTree={fromPendingTree} " +
                    $"removedFromActiveTree={fromActiveTree}");
                totalReaped++;
            }

            // 3. Bump the supersede-state version so ERS / ELS caches
            //    invalidate. RecordingStore.RemoveCommittedById has already
            //    bumped RecordingStore.StateVersion per victim; this bump
            //    is the scenario-level supersede version which is a
            //    separate signal that the closure walk depends on.
            //    Use ReferenceEquals to skip Unity's null-check override (so a
            //    test fixture scenario without a Unity lifecycle still works).
            if (totalReaped > 0)
            {
                var scenario = ParsekScenario.Instance;
                if (!object.ReferenceEquals(null, scenario))
                    scenario.BumpSupersedeStateVersion();
            }

            return totalReaped;
        }

        /// <summary>
        /// Idempotently inserts <paramref name="fork"/> into
        /// <paramref name="tree"/>'s <c>Recordings</c> dictionary and rebuilds
        /// the background map so the fork's pid does not double-list as both
        /// active recorder target and background entry. Returns true when the
        /// dictionary changed (fork was missing); false on no-op (already
        /// attached, or null inputs). The rebuild is skipped on a no-op so
        /// the restore-coroutine reconciliation path stays cheap on re-entry.
        /// Callers must only attach forks once <paramref name="tree"/>'s
        /// background-map shape is final for the marker write phase.
        /// </summary>
        internal static bool EnsureForkAttachedToTree(
            RecordingTree tree, Recording fork, string callSite,
            bool skipBackgroundMapRebuild = false)
        {
            if (tree == null || fork == null
                || string.IsNullOrEmpty(fork.RecordingId)
                || tree.Recordings == null)
            {
                return false;
            }
            bool overwroteStaleInstance = false;
            if (tree.Recordings.TryGetValue(fork.RecordingId, out var existing))
            {
                if (ReferenceEquals(existing, fork))
                    return false;
                // Expected for F5/F9 mid-Re-Fly: TryRestoreActiveTreeNode
                // deserialised a fresh tree-side Recording for the fork id,
                // but the committed list still holds the pre-load fork
                // object that the recorder is appending into. Convergence
                // is necessary so commit/discard see the populated
                // committed-list instance, not the empty deserialised
                // shadow. Log Info so the convergence is auditable but
                // doesn't signal corruption.
                overwroteStaleInstance = true;
                ParsekLog.Info(InvokeTag,
                    $"{callSite ?? "EnsureForkAttachedToTree"}: tree.Recordings[{fork.RecordingId}] " +
                    $"held a different Recording instance than the committed-list fork; " +
                    $"converging the tree slot to the committed instance so recorder flush, " +
                    $"merge, and discard agree on the same object");
            }
            tree.Recordings[fork.RecordingId] = fork;
            if (!skipBackgroundMapRebuild)
                tree.RebuildBackgroundMap();
            ParsekLog.Verbose(InvokeTag,
                $"{callSite ?? "EnsureForkAttachedToTree"}: attached in-place fork rec={fork.RecordingId} " +
                $"to tree '{tree.TreeName ?? "<unnamed>"}' (id={tree.Id ?? "<no-id>"})" +
                (overwroteStaleInstance ? " (replaced stale instance)" : "") +
                (skipBackgroundMapRebuild ? " (caller will rebuild background map)" : ""));
            return true;
        }

        private static void DetachForkFromTreeForRollback(RecordingTree tree, Recording fork)
        {
            if (tree == null || fork == null) return;
            if (tree.Recordings == null)
                return;
            if (tree.Recordings.Remove(fork.RecordingId))
            {
                tree.RebuildBackgroundMap();
                ParsekLog.Verbose(InvokeTag,
                    $"AtomicMarkerWrite rollback: detached in-place fork rec={fork.RecordingId} " +
                    $"from tree '{tree.TreeName ?? "<unnamed>"}' (id={tree.Id ?? "<no-id>"})");
            }
        }

        internal static Recording BuildProvisionalRecording(
            RewindPoint rp, ChildSlot selected, Recording originChild,
            string sessionId, PostLoadStripResult stripResult)
        {
            // SupersedeTargetId is intentionally NOT initialised here;
            // AtomicMarkerWrite assigns it to `priorTip` (the slot's
            // current effective tip) immediately after this call. Writing
            // it here as `selected.OriginChildRecordingId` was dead code
            // because the next statement at the call site overwrites it.
            var rec = new Recording
            {
                RecordingId = "rec_" + Guid.NewGuid().ToString("N"),
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessionId,
                ProvisionalForRpId = rp.RewindPointId,
                ParentBranchPointId = originChild?.ParentBranchPointId ?? rp.BranchPointId,
                TreeId = originChild?.TreeId,
                VesselPersistentId = stripResult.SelectedPid,
                VesselName = stripResult.SelectedVessel != null
                    ? stripResult.SelectedVessel.vesselName
                    : (originChild?.VesselName ?? "Re-Fly"),
                PlaybackEnabled = false,
            };
            return rec;
        }

        private static Recording FindRecordingById(string recordingId)
        {
            if (string.IsNullOrEmpty(recordingId)) return null;
            // NOTE: raw CommittedRecordings read — this is the only code path
            // that can locate the origin recording by id at invoke time, since
            // the Phase 1-5 types carry the recording id but not a pre-resolved
            // reference. Allowlisted per [ERS-exempt — Phase 6] file-level note.
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

        /// <summary>
        /// Snapshots the existing <see cref="BranchPoint.Id"/>s in the
        /// in-memory tree referenced by <paramref name="treeId"/>. The
        /// returned list is consumed by
        /// <see cref="SupersedeCommit.HasReFlySessionStructuralMutation"/>
        /// (auto-seal gate),
        /// <see cref="MergeDialog.PruneAttemptRecordingsFromCommittedTrees"/>
        /// (drops session-authored branch points on Discard), and
        /// <see cref="MergeDialog.AddSessionBranchPointDescendantAttemptIds"/>
        /// (collects attempt-authored debris children) to distinguish
        /// branch points authored DURING this Re-Fly from pre-existing
        /// ones. Returns an empty list (not null) when the tree is
        /// found with no branch points; returns null only when the tree
        /// id is empty or no in-memory tree matches.
        ///
        /// Lookup order matches <see cref="FindTreeForReFlyFork"/>:
        /// PendingTree first (the normal Re-Fly load shape after
        /// <c>TryRestoreActiveTreeNode</c> stashes the loaded tree as
        /// pending and `RemoveCommittedTreeById` drops the prior
        /// committed copy), then CommittedTrees, then the live
        /// <c>ParsekFlight.Instance.ActiveTreeForSerialization</c> (for
        /// the AtomicMarkerWrite-after-RestoreActiveTreeFromPending race).
        /// Restricting the lookup to CommittedTrees - as a prior
        /// implementation did - returned null on the normal pending-tree
        /// path and silently disabled structural-mutation auto-seal
        /// plus all session-BP-baseline-aware Discard cleanup.
        /// </summary>
        internal static List<string> SnapshotTreeBranchPointIds(string treeId)
        {
            RecordingTree tree = FindTreeForReFlyFork(treeId);
            if (tree == null || tree.BranchPoints == null)
                return null;

            var snapshot = new List<string>(tree.BranchPoints.Count);
            for (int i = 0; i < tree.BranchPoints.Count; i++)
            {
                var bp = tree.BranchPoints[i];
                if (bp == null) continue;
                if (string.IsNullOrEmpty(bp.Id)) continue;
                snapshot.Add(bp.Id);
            }
            return snapshot;
        }
    }
}
