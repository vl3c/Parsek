using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// R1-EMPTY-PROVISIONAL: a Rewind-to-Separation followed by a re-fly did not
    /// supersede the branch it was invoked to replace. The session's provisional
    /// never had a recorder bound to it, the re-flight landed in a brand-new
    /// unrelated tree, and the merge wrote zero supersede rows.
    ///
    /// <para>
    /// Root cause: the bug #585 in-place-continuation swap in
    /// <c>ParsekFlight.RestoreActiveTreeFromPending</c> is gated on the restored
    /// tree matching <see cref="ReFlySessionMarker.TreeId"/>, and the restore
    /// sourced its tree from the single pending slot. The slot carries whatever
    /// tree was live when Re-Fly was clicked, which is the marker's tree only by
    /// coincidence.
    /// </para>
    ///
    /// <para>
    /// These cells pin the new adoption decision, the store-level install, the
    /// early-detection predicate, and — load-bearing for the #585 constraint —
    /// that the historical path still resolves to the pending slot and the
    /// unchanged #585 helper still swaps on it.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class R1EmptyProvisionalAdoptionTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public R1EmptyProvisionalAdoptionTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        private static ReFlySessionMarker InPlaceMarker(
            string treeId = "tree-origin",
            string forkId = "rec-fork",
            string originId = "rec-origin")
        {
            return new ReFlySessionMarker
            {
                SessionId = "sess_r1",
                TreeId = treeId,
                ActiveReFlyRecordingId = forkId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = originId,
                InPlaceContinuation = true,
            };
        }

        // ================================================================
        // ReFlyRestoreAdoption.ResolveRestoreTree
        // ================================================================

        [Fact]
        public void ResolveRestoreTree_NoMarker_UsesPendingSlot()
        {
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                marker: null,
                hasPendingTree: true,
                pendingTreeId: "tree-live",
                markerTreeResolvable: false);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("no-inplace-refly-session", d.Reason);
        }

        [Fact]
        public void ResolveRestoreTree_PlaceholderModeMarker_UsesPendingSlot()
        {
            // Placeholder mode cannot satisfy the #585 swap
            // (ResolveInPlaceContinuationTarget returns "placeholder-pattern"),
            // so the documented merge-dialog fallback must stay the recovery.
            var marker = InPlaceMarker();
            marker.InPlaceContinuation = false;

            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                marker, hasPendingTree: true,
                pendingTreeId: "tree-live", markerTreeResolvable: true);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("no-inplace-refly-session", d.Reason);
        }

        /// <summary>
        /// BUG #585 PRESERVATION CELL. The historical shape — the pending slot
        /// already holds the marker's tree — must keep sourcing from the pending
        /// slot, byte-identically to pre-fix behaviour.
        /// </summary>
        [Fact]
        public void ResolveRestoreTree_PendingSlotIsMarkerTree_UsesPendingSlot_Bug585Path()
        {
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                InPlaceMarker(treeId: "tree-origin"),
                hasPendingTree: true,
                pendingTreeId: "tree-origin",
                markerTreeResolvable: true);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("pending-slot-is-marker-tree", d.Reason);
            Assert.Null(d.TreeId);
        }

        /// <summary>
        /// THE R1 REPRO CELL. Flight 3's exact shape: the pending slot holds the
        /// pre-rewind flight's Limbo tree (b435c4ad "Kerbal X") while the marker
        /// names tree-b9-stack-root, where the provisional was attached.
        /// </summary>
        [Fact]
        public void ResolveRestoreTree_PendingSlotHoldsForeignTree_AdoptsMarkerTree()
        {
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                InPlaceMarker(treeId: "tree-b9-stack-root"),
                hasPendingTree: true,
                pendingTreeId: "b435c4ad",
                markerTreeResolvable: true);

            Assert.Equal(ReFlyRestoreTreeSource.MarkerTree, d.Source);
            Assert.Equal("tree-b9-stack-root", d.TreeId);
            Assert.Equal("refly-pending-slot-holds-foreign-tree", d.Reason);
        }

        /// <summary>
        /// Re-Fly invoked with no live tree (SPACECENTER / TRACKSTATION, or FLIGHT
        /// with recording off): the pending slot is empty, so OnLoad's Limbo
        /// dispatch scheduled nothing at all. Same root cause, different shape.
        /// </summary>
        [Fact]
        public void ResolveRestoreTree_NoPendingTree_AdoptsMarkerTree()
        {
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                InPlaceMarker(treeId: "tree-origin"),
                hasPendingTree: false,
                pendingTreeId: null,
                markerTreeResolvable: true);

            Assert.Equal(ReFlyRestoreTreeSource.MarkerTree, d.Source);
            Assert.Equal("tree-origin", d.TreeId);
            Assert.Equal("refly-no-pending-tree", d.Reason);
        }

        [Fact]
        public void ResolveRestoreTree_MarkerTreeUnresolvable_FallsBackToPendingSlot()
        {
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                InPlaceMarker(treeId: "tree-gone"),
                hasPendingTree: true,
                pendingTreeId: "tree-live",
                markerTreeResolvable: false);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("refly-marker-tree-unresolvable", d.Reason);
        }

        [Fact]
        public void ResolveRestoreTree_LegacyMarkerWithoutTreeId_UsesPendingSlot()
        {
            // ResolveInPlaceContinuationTarget deliberately allows the swap for a
            // marker with no TreeId (EmptyMarkerTreeId_AllowsSwap), so the pending
            // slot stays the only sensible source.
            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                InPlaceMarker(treeId: null),
                hasPendingTree: true,
                pendingTreeId: "tree-live",
                markerTreeResolvable: false);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("refly-marker-has-no-treeid", d.Reason);
        }

        [Fact]
        public void ResolveRestoreTree_MarkerFieldsEmpty_UsesPendingSlot()
        {
            var marker = InPlaceMarker();
            marker.ActiveReFlyRecordingId = null;

            var d = ReFlyRestoreAdoption.ResolveRestoreTree(
                marker, hasPendingTree: true,
                pendingTreeId: "tree-live", markerTreeResolvable: true);

            Assert.Equal(ReFlyRestoreTreeSource.PendingTree, d.Source);
            Assert.Equal("no-inplace-refly-session", d.Reason);
        }

        // ================================================================
        // Composition: adoption -> the UNCHANGED #585 swap helper
        // ================================================================

        /// <summary>
        /// The load-bearing chain. The fix works because the tree handed to the
        /// (unmodified) #585 helper is now the marker's tree, so the helper's
        /// tree-id gate passes and the swap redirects the wait target to the
        /// provisional. Pre-fix, the helper received the foreign pending tree and
        /// returned <c>marker-tree-id-mismatch</c>.
        /// </summary>
        [Fact]
        public void Adoption_ThenBug585Swap_RedirectsWaitTargetToProvisional()
        {
            const string markerTreeId = "tree-b9-stack-root";
            var marker = InPlaceMarker(
                treeId: markerTreeId, forkId: "rec_5b0697a6", originId: "b9-booster-a");

            // Pre-fix: the coroutine handed the foreign pending tree to the helper.
            var preFix = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, "b435c4ad", "9a98f7c3",
                id => id == "9a98f7c3" ? ("Kerbal X", 2708531065u) : ((string, uint)?)null);
            Assert.False(preFix.ShouldSwap);
            Assert.Equal("marker-tree-id-mismatch", preFix.Reason);

            // Post-fix: adoption picks the marker's tree, and the same helper swaps.
            var adoption = ReFlyRestoreAdoption.ResolveRestoreTree(
                marker, hasPendingTree: true,
                pendingTreeId: "b435c4ad", markerTreeResolvable: true);
            Assert.Equal(ReFlyRestoreTreeSource.MarkerTree, adoption.Source);

            var postFix = ReFlySessionMarker.ResolveInPlaceContinuationTarget(
                marker, adoption.TreeId, "b9-booster-a",
                id => id == "rec_5b0697a6"
                    ? ("B9 Booster A", 948397159u)
                    : ((string, uint)?)null);

            Assert.True(postFix.ShouldSwap);
            Assert.Equal("rec_5b0697a6", postFix.TargetRecordingId);
            Assert.Equal(948397159u, postFix.TargetVesselPersistentId);
            Assert.Equal("in-place-continuation", postFix.Reason);
        }

        // ================================================================
        // ReFlyRestoreAdoption.IsOrphanedFreshTreeStart (layer 2 detection)
        // ================================================================

        [Fact]
        public void IsOrphanedFreshTreeStart_FreshTreeDuringInPlaceReFly_ReturnsTrue()
        {
            Assert.True(ReFlyRestoreAdoption.IsOrphanedFreshTreeStart(
                InPlaceMarker(treeId: "tree-b9-stack-root"), "820de77e"));
        }

        [Fact]
        public void IsOrphanedFreshTreeStart_NewTreeIsMarkerTree_ReturnsFalse()
        {
            Assert.False(ReFlyRestoreAdoption.IsOrphanedFreshTreeStart(
                InPlaceMarker(treeId: "tree-b9-stack-root"), "tree-b9-stack-root"));
        }

        [Fact]
        public void IsOrphanedFreshTreeStart_NoMarker_ReturnsFalse()
        {
            Assert.False(ReFlyRestoreAdoption.IsOrphanedFreshTreeStart(null, "820de77e"));
        }

        [Fact]
        public void IsOrphanedFreshTreeStart_PlaceholderMarker_ReturnsFalse()
        {
            var marker = InPlaceMarker();
            marker.InPlaceContinuation = false;
            Assert.False(ReFlyRestoreAdoption.IsOrphanedFreshTreeStart(marker, "820de77e"));
        }

        [Fact]
        public void IsOrphanedFreshTreeStart_EmptyNewTreeId_ReturnsFalse()
        {
            Assert.False(ReFlyRestoreAdoption.IsOrphanedFreshTreeStart(
                InPlaceMarker(), ""));
        }

        // ================================================================
        // ParsekFlight.ShouldUpgradeRestoreModeForReFlyMarkerTree
        // ================================================================

        [Fact]
        public void ShouldUpgradeRestoreModeForReFlyMarkerTree_NoneAndMarkerTree_True()
        {
            Assert.True(ParsekFlight.ShouldUpgradeRestoreModeForReFlyMarkerTree(
                ParsekScenario.ActiveTreeRestoreMode.None,
                ReFlyRestoreTreeSource.MarkerTree));
        }

        [Fact]
        public void ShouldUpgradeRestoreModeForReFlyMarkerTree_NoneAndPendingTree_False()
        {
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyMarkerTree(
                ParsekScenario.ActiveTreeRestoreMode.None,
                ReFlyRestoreTreeSource.PendingTree));
        }

        [Fact]
        public void ShouldUpgradeRestoreModeForReFlyMarkerTree_AlreadyScheduled_False()
        {
            // The Limbo dispatch already scheduled a restore; the coroutine's own
            // adoption decision handles the tree choice from there.
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyMarkerTree(
                ParsekScenario.ActiveTreeRestoreMode.Quickload,
                ReFlyRestoreTreeSource.MarkerTree));
            Assert.False(ParsekFlight.ShouldUpgradeRestoreModeForReFlyMarkerTree(
                ParsekScenario.ActiveTreeRestoreMode.VesselSwitch,
                ReFlyRestoreTreeSource.MarkerTree));
        }

        // ================================================================
        // ParsekFlight.AdoptReFlyMarkerTreeAsActive (store-level install)
        // ================================================================

        private static RecordingTree BuildMarkerTree(
            string treeId, string originId, string forkId,
            out Recording origin, out Recording fork)
        {
            origin = new Recording
            {
                RecordingId = originId,
                TreeId = treeId,
                VesselName = "B9 Booster A",
                VesselPersistentId = 948397159u,
                MergeState = MergeState.Immutable,
            };
            fork = new Recording
            {
                RecordingId = forkId,
                TreeId = treeId,
                VesselName = "B9 Booster A",
                VesselPersistentId = 948397159u,
                MergeState = MergeState.NotCommitted,
                SupersedeTargetId = originId,
            };
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "B9 Stack",
                RootRecordingId = originId,
                ActiveRecordingId = originId,
            };
            tree.AddOrReplaceRecording(origin);
            tree.AddOrReplaceRecording(fork);
            return tree;
        }

        [Fact]
        public void AdoptReFlyMarkerTreeAsActive_DetachesCommittedCopy_AndKeepsProvisionalCommitted()
        {
            try
            {
                RecordingStore.SuppressLogging = true;
                RecordingStore.ResetForTesting();

                const string treeId = "tree-b9-stack-root";
                const string originId = "b9-booster-a";
                const string forkId = "rec_5b0697a6";

                var tree = BuildMarkerTree(treeId, originId, forkId,
                    out Recording origin, out Recording fork);

                // The R1 shape on disk: the origin tree is a COMMITTED tree and
                // AtomicMarkerWrite eagerly attached the fork to it + AddProvisional
                // put the fork in the flat committed list.
                RecordingStore.AddCommittedTreeForTesting(tree);
                RecordingStore.AddCommittedInternal(origin);
                RecordingStore.AddCommittedInternal(fork);

                var marker = InPlaceMarker(treeId, forkId, originId);

                bool detached = ParsekFlight.AdoptReFlyMarkerTreeAsActive(tree, marker);

                Assert.True(detached);
                // A live activeTree must NOT also sit in committedTrees, or OnSave
                // writes the same tree id twice (PARSEK_ACTIVE_TREE + RECORDING_TREE).
                Assert.DoesNotContain(RecordingStore.CommittedTrees,
                    t => string.Equals(t.Id, treeId, StringComparison.Ordinal));
                // The detach strips the tree's recordings from the flat list...
                Assert.DoesNotContain(RecordingStore.CommittedRecordings,
                    r => ReferenceEquals(r, origin));
                // ...but the session provisional must survive it: the merge tail
                // resolves the fork from the committed list.
                Assert.Contains(RecordingStore.CommittedRecordings,
                    r => ReferenceEquals(r, fork));
                // Tree membership is untouched by the detach.
                Assert.True(tree.Recordings.ContainsKey(forkId));
                Assert.True(tree.Recordings.ContainsKey(originId));

                Assert.Contains(logLines, l =>
                    l.Contains("[Flight]")
                    && l.Contains("AdoptReFlyMarkerTreeAsActive")
                    && l.Contains("detachedCommittedCopy=True")
                    && l.Contains("provisionalReAddedToCommittedList=True"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void AdoptReFlyMarkerTreeAsActive_NoCommittedCopy_LeavesProvisionalAlone()
        {
            try
            {
                RecordingStore.SuppressLogging = true;
                RecordingStore.ResetForTesting();

                const string treeId = "tree-live-only";
                var tree = BuildMarkerTree(treeId, "rec-origin", "rec-fork",
                    out Recording origin, out Recording fork);
                // Fork committed, tree NOT in committedTrees (the async-load shape
                // where the tree only lives on the live side).
                RecordingStore.AddCommittedInternal(fork);

                bool detached = ParsekFlight.AdoptReFlyMarkerTreeAsActive(
                    tree, InPlaceMarker(treeId, "rec-fork", "rec-origin"));

                Assert.False(detached);
                Assert.Contains(RecordingStore.CommittedRecordings,
                    r => ReferenceEquals(r, fork));
                Assert.Single(RecordingStore.CommittedRecordings);
                Assert.Contains(logLines, l =>
                    l.Contains("AdoptReFlyMarkerTreeAsActive")
                    && l.Contains("detachedCommittedCopy=False")
                    && l.Contains("provisionalReAddedToCommittedList=False"));
            }
            finally
            {
                RecordingStore.ResetForTesting();
                RecordingStore.SuppressLogging = false;
            }
        }

        [Fact]
        public void AdoptReFlyMarkerTreeAsActive_NullTree_WarnsAndReturnsFalse()
        {
            Assert.False(ParsekFlight.AdoptReFlyMarkerTreeAsActive(null, InPlaceMarker()));
            Assert.Contains(logLines, l =>
                l.Contains("[WARN]")
                && l.Contains("AdoptReFlyMarkerTreeAsActive")
                && l.Contains("nothing to adopt"));
        }
    }
}
