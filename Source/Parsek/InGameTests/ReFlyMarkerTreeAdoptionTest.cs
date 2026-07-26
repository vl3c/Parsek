using System;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// R1-EMPTY-PROVISIONAL live-scene regression. The defect: a
    /// Rewind-to-Separation followed by a re-fly bound NO recorder to the
    /// session's provisional, so the re-flight landed in a brand-new unrelated
    /// tree and the merge wrote zero supersede rows - the original branch and the
    /// re-flown branch both stayed live in ERS.
    ///
    /// <para>
    /// The pure decisions are covered headlessly by
    /// <c>Parsek.Tests.R1EmptyProvisionalAdoptionTests</c>. What xUnit CANNOT
    /// reach is the live half this fix depends on:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <see cref="RewindInvoker.FindTreeForReFlyFork"/> resolving the marker's
    ///     tree out of the REAL <c>RecordingStore</c> under KSP (its third
    ///     fallback reads <c>ParsekFlight.Instance.ActiveTreeForSerialization</c>,
    ///     a Unity singleton).
    ///   </description></item>
    ///   <item><description>
    ///     The single pending-tree slot genuinely holding a FOREIGN tree at the
    ///     same time as a live <see cref="ReFlySessionMarker"/> on the real
    ///     <see cref="ParsekScenario"/> - the exact state R1 flight 3 reached
    ///     (<c>pend.tree=b435c4ad:Limbo</c> while the marker named
    ///     <c>tree-b9-stack-root</c>).
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ParsekFlight.AdoptReFlyMarkerTreeAsActive"/> mutating the
    ///     live store lists so the adopted tree cannot be written twice by OnSave.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// Everything the test installs is synthetic and removed in the finally
    /// block; it never touches the player's own trees, recordings, or marker,
    /// and skips outright if a real Re-Fly session or pending tree is live.
    /// </para>
    /// </summary>
    public class ReFlyMarkerTreeAdoptionTest
    {
        private const string TreeIdPrefix = "ingame-r1-";

        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "R1: a Re-Fly whose tree is not the pending slot's occupant adopts the marker tree")]
        public void ReFlyMarkerTreeIsAdoptedOverForeignPendingTree()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            if (scenario.ActiveReFlySessionMarker != null)
            {
                InGameAssert.Skip("A real Re-Fly session is active — conclude or discard it and rerun. "
                    + "This test installs its own synthetic marker and must not stomp a live one.");
                return;
            }
            if (RecordingStore.HasPendingTree)
            {
                InGameAssert.Skip("A real pending tree occupies the single pending slot — "
                    + "conclude it (scene exit / merge dialog) and rerun. This test needs to "
                    + "install its own foreign pending tree to reproduce the R1 state.");
                return;
            }

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            string markerTreeId = TreeIdPrefix + "marker-" + suffix;
            string foreignTreeId = TreeIdPrefix + "foreign-" + suffix;
            string originId = TreeIdPrefix + "origin-" + suffix;
            string forkId = TreeIdPrefix + "fork-" + suffix;
            const uint originPid = 0x0B9A11C0u;

            var origin = new Recording
            {
                RecordingId = originId,
                TreeId = markerTreeId,
                VesselName = "R1 Origin",
                VesselPersistentId = originPid,
                MergeState = MergeState.Immutable,
            };
            var fork = new Recording
            {
                RecordingId = forkId,
                TreeId = markerTreeId,
                VesselName = "R1 Origin",
                VesselPersistentId = originPid,
                MergeState = MergeState.NotCommitted,
                SupersedeTargetId = originId,
            };
            var markerTree = new RecordingTree
            {
                Id = markerTreeId,
                TreeName = "R1 Marker Tree",
                RootRecordingId = originId,
                // Still the PRE-rewind pointer, exactly as the RP quicksave leaves it.
                ActiveRecordingId = originId,
            };
            markerTree.AddOrReplaceRecording(origin);
            markerTree.AddOrReplaceRecording(fork);

            var foreignRec = new Recording
            {
                RecordingId = TreeIdPrefix + "foreignrec-" + suffix,
                TreeId = foreignTreeId,
                VesselName = "R1 Pre-Rewind Flight",
                VesselPersistentId = 0x0F0E1691u,
            };
            var foreignTree = new RecordingTree
            {
                Id = foreignTreeId,
                TreeName = "R1 Pre-Rewind Flight",
                RootRecordingId = foreignRec.RecordingId,
                ActiveRecordingId = foreignRec.RecordingId,
            };
            foreignTree.AddOrReplaceRecording(foreignRec);

            var marker = new ReFlySessionMarker
            {
                SessionId = "sess_ingame_r1_" + suffix,
                TreeId = markerTreeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                RewindPointId = "rp_ingame_r1_" + suffix,
                InPlaceContinuation = true,
            };

            bool installedMarker = false;
            bool stashedForeign = false;
            try
            {
                // Live store, exactly the R1 shape: the origin tree is COMMITTED
                // (AtomicMarkerWrite's eager attach landed the fork there), and the
                // pending slot holds the unrelated pre-rewind flight in Limbo.
                RecordingStore.AddCommittedTreeForTesting(markerTree);
                RecordingStore.AddCommittedInternal(origin);
                RecordingStore.AddCommittedInternal(fork);
                RecordingStore.StashPendingTree(foreignTree, PendingTreeState.Limbo);
                stashedForeign = true;
                scenario.ActiveReFlySessionMarker = marker;
                installedMarker = true;

                // 1. The live resolver must find the marker's tree.
                RecordingTree resolved = RewindInvoker.FindTreeForReFlyFork(marker.TreeId);
                InGameAssert.IsNotNull(resolved,
                    $"FindTreeForReFlyFork('{marker.TreeId}') returned null in the live store");
                InGameAssert.IsTrue(ReferenceEquals(resolved, markerTree),
                    "FindTreeForReFlyFork must return the canonical committed tree instance "
                    + "(the fork was attached to THAT object; a copy would diverge at merge)");

                // 2. The adoption decision, fed from the LIVE store, must pick the
                //    marker tree over the foreign pending occupant.
                InGameAssert.IsTrue(RecordingStore.HasPendingTree,
                    "pending slot expected to hold the synthetic foreign tree");
                InGameAssert.IsTrue(
                    RecordingStore.PendingTree.Id == foreignTreeId,
                    "pending slot holds an unexpected tree");
                ReFlyRestoreTreeDecision adoption = ReFlyRestoreAdoption.ResolveRestoreTree(
                    marker,
                    RecordingStore.HasPendingTree,
                    RecordingStore.PendingTree?.Id,
                    resolved != null);
                InGameAssert.AreEqual(ReFlyRestoreTreeSource.MarkerTree, adoption.Source,
                    $"expected the restore to adopt the marker tree; reason={adoption.Reason}");
                InGameAssert.AreEqual(markerTreeId, adoption.TreeId, "adopted tree id mismatch");

                // 3. With the marker's tree in hand, the UNCHANGED bug #585 helper
                //    swaps the wait target to the provisional. Pre-fix it received
                //    the foreign pending tree and returned marker-tree-id-mismatch.
                var swap = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                    marker,
                    markerTree.Id,
                    markerTree.ActiveRecordingId,
                    recId =>
                    {
                        if (string.IsNullOrEmpty(recId)) return null;
                        Recording rec;
                        if (!markerTree.Recordings.TryGetValue(recId, out rec) || rec == null)
                            return null;
                        return (rec.VesselName, rec.VesselPersistentId);
                    });
                InGameAssert.IsTrue(swap.ShouldSwap,
                    $"expected the #585 swap to fire on the adopted tree; reason={swap.Reason}");
                InGameAssert.AreEqual(forkId, swap.TargetRecordingId,
                    "the recorder must be pointed at the session's provisional");
                InGameAssert.AreEqual(originPid, swap.TargetVesselPersistentId,
                    "the restore must wait for the fork's vessel pid");

                // 4. The install itself, against the live store lists.
                bool detached = ParsekFlight.AdoptReFlyMarkerTreeAsActive(markerTree, marker);
                InGameAssert.IsTrue(detached,
                    "AdoptReFlyMarkerTreeAsActive must detach the committed copy");
                InGameAssert.IsFalse(CommittedTreesContain(markerTreeId),
                    "the adopted tree must leave committedTrees — a tree that is both "
                    + "activeTree and committed gets written twice by OnSave under one id");
                InGameAssert.IsFalse(CommittedRecordingsContain(origin),
                    "the detach must strip the tree's recordings from the flat committed list");
                InGameAssert.IsTrue(CommittedRecordingsContain(fork),
                    "the session provisional must SURVIVE the detach — the merge tail "
                    + "resolves the fork from the committed list");
                InGameAssert.IsTrue(markerTree.Recordings.ContainsKey(forkId),
                    "tree membership must be untouched by the detach");

                // 5. The pending slot is deliberately left alone: its occupant is the
                //    pre-rewind flight, whose vessel the rewind load replaced, so its
                //    scene-exit disposition is unchanged by this fix.
                InGameAssert.IsTrue(RecordingStore.HasPendingTree
                    && RecordingStore.PendingTree.Id == foreignTreeId,
                    "the foreign pending tree must be left in place by the adoption");

                ParsekLog.Info("RewindTest",
                    $"ReFlyMarkerTreeIsAdoptedOverForeignPendingTree: all assertions passed "
                    + $"(sess={marker.SessionId} markerTree={markerTreeId} "
                    + $"foreignPendingTree={foreignTreeId})");
            }
            finally
            {
                if (installedMarker)
                {
                    scenario.ActiveReFlySessionMarker = null;
                    Parsek.Rendering.RenderSessionState.Clear("ingame-r1-test-teardown");
                }
                if (stashedForeign
                    && RecordingStore.HasPendingTree
                    && RecordingStore.PendingTree.Id == foreignTreeId)
                {
                    RecordingStore.PopPendingTree();
                }
                RecordingStore.RemoveCommittedInternal(fork);
                RecordingStore.RemoveCommittedInternal(origin);
                RecordingStore.RemoveCommittedTreeById(
                    markerTreeId, "ingame-r1-test-teardown");
            }
        }

        // Allowlisted raw reads: object-identity / id membership probes over the
        // store lists, not effective-state queries (see the file-level ERS
        // exemption note for the InGameTests surface in the audit allowlist).
        private static bool CommittedTreesContain(string treeId)
        {
            var trees = RecordingStore.CommittedTrees;
            if (trees == null) return false;
            for (int i = 0; i < trees.Count; i++)
            {
                if (trees[i] != null
                    && string.Equals(trees[i].Id, treeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool CommittedRecordingsContain(Recording rec)
        {
            if (rec == null) return false;
            var committed = RecordingStore.CommittedRecordings;
            if (committed == null) return false;
            for (int i = 0; i < committed.Count; i++)
            {
                if (ReferenceEquals(committed[i], rec))
                    return true;
            }
            return false;
        }
    }
}
