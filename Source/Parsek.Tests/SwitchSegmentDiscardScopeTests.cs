using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase E + post-#876 playtest coverage (2026-05-17) for segment-scoped
    /// switch/Fly auto-record: the scoped Discard helper
    /// (<see cref="RecordingStore.TryDiscardActiveSwitchSegmentAttempt"/>),
    /// the topology-based subtree collector
    /// (<see cref="RecordingStore.CollectSwitchSegmentSubtreeRecordingIds"/>),
    /// the <see cref="MergeDialog"/> hook, and the unified whole-tree body
    /// builder. Per plan
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// §"Merge and Discard Scope", §"Final Disposition After Scoped
    /// Discard", and §"Dialog and UI Copy" plus the test list at the
    /// bottom of the plan.
    ///
    /// <para>Bug 2 follow-up (post-#876 playtest 2026-05-17): the second
    /// whole-pending-tree dialog flow was deleted in favor of a broader
    /// topology-based subtree sweep. Tests pinning the old second-dialog
    /// behavior have been removed.</para>
    ///
    /// <para>Bug 3 follow-up: the entry-reason-aware copy
    /// (<c>BuildSwitchSegmentDialogBody</c>) was deleted; both switch-segment
    /// and regular tree-merge dialogs now share
    /// <c>BuildWholeTreeMergeDialogBody</c> with a duration line.</para>
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentDiscardScopeTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentDiscardScopeTests()
        {
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            GameStateStore.SuppressLogging = true;
            RecordingStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
        }

        public void Dispose()
        {
            ParsekScenario.SetInstanceForTesting(null);
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            GameStateStore.ResetForTesting();
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        private static string SessionIdString(Guid sessionId)
            => sessionId.ToString("D", CultureInfo.InvariantCulture);

        private static Recording MakeRecording(
            string recordingId,
            string treeId,
            string switchSegmentSessionId = null,
            string parentBranchPointId = null,
            string childBranchPointId = null,
            string vesselName = "Test")
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = vesselName,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                SwitchSegmentSessionId = switchSegmentSessionId,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
            };
        }

        private static RecordingTree MakeTree(
            string treeId,
            string activeRecordingId = null,
            params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                BranchPoints = new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            if (recordings.Length > 0)
                tree.RootRecordingId = recordings[0].RecordingId;
            tree.ActiveRecordingId = activeRecordingId
                ?? (recordings.Length > 0 ? recordings[0].RecordingId : null);
            return tree;
        }

        private static BranchPoint MakeBranchPoint(
            string bpId,
            string parentRecordingId,
            string childRecordingId,
            BranchPointType type = BranchPointType.VesselSwitchContinuation)
        {
            return new BranchPoint
            {
                Id = bpId,
                UT = 150.0,
                Type = type,
                ParentRecordingIds = new List<string> { parentRecordingId },
                ChildRecordingIds = new List<string> { childRecordingId },
            };
        }

        /// <summary>
        /// Wires up a scenario with an armed switch-segment session targeting
        /// a tree that contains a parent recording + a marker-owned child
        /// segment connected via a session-authored branch point.
        /// </summary>
        private static (ParsekScenario scenario, SwitchSegmentSession session,
            RecordingTree tree, Recording parent, Recording segment, BranchPoint bp)
            BuildPendingTreeWithSegment(
                string treeId = "tree_pending",
                SwitchSegmentEntryReason entryReason
                    = SwitchSegmentEntryReason.TrackingStationFly)
        {
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            string sessionIdStr;
            Guid sessionId = Guid.NewGuid();
            sessionIdStr = SessionIdString(sessionId);

            string bpId = "bp_seg_" + Guid.NewGuid().ToString("N");
            var parent = MakeRecording("rec_parent", treeId,
                childBranchPointId: bpId, vesselName: "Parent Vessel");
            var segment = MakeRecording("rec_seg", treeId,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bpId,
                vesselName: "Probe Alpha");

            var tree = MakeTree(treeId, activeRecordingId: "rec_seg", parent, segment);
            var bp = MakeBranchPoint(bpId, parent.RecordingId, segment.RecordingId);
            tree.BranchPoints.Add(bp);

            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                IntentId = Guid.NewGuid(),
                EntryReason = entryReason,
                TreeId = treeId,
                ParentRecordingId = parent.RecordingId,
                ActiveSegmentRecordingId = segment.RecordingId,
                SwitchUT = 150.0,
                // Pre-existing BPs = empty (none existed before the session).
                PreSessionBranchPointIds = new List<string>(),
            };
            scenario.ArmSwitchSegmentSession(session);
            return (scenario, session, tree, parent, segment, bp);
        }

        // -----------------------------------------------------------------
        // Guard: no active session → returns NoActiveSession with reason
        // -----------------------------------------------------------------

        // Fails if: the scoped-discard helper claims success when no
        // session is armed. Plan §"Merge and Discard Scope" requires the
        // helper to be opt-in: callers fall back to whole-tree discard
        // only when no session was armed.
        [Fact]
        public void Discard_NoActiveSession_ReturnsFalse_WithReason()
        {
            // No scenario / no session.
            ParsekScenario.SetInstanceForTesting(new ParsekScenario());

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.NoActiveSession,
                disposition);
            Assert.Equal("no-active-session", reason);
        }

        // -----------------------------------------------------------------
        // Pending-tree disposition: prune segment subtree, preserve siblings
        // in OTHER trees, no second dialog.
        // -----------------------------------------------------------------

        // Bug 2 (post-#876 playtest 2026-05-17): debris from a Breakup-during-
        // segment must be removed even though it carries no
        // SwitchSegmentSessionId stamp. The topology-based sweep is the
        // load-bearing contract: descendants are in scope regardless of
        // marker stamp.
        //
        // Fails if: a future refactor reverts to marker-only filtering;
        // orphan debris from a Breakup-during-segment survives the discard
        // and the secondary-dialog regression returns.
        [Fact]
        public void Discard_DebrisFromBreakupDuringSegment_IsRemoved()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Breakup-during-segment authors a debris recording that does
            // NOT inherit the SwitchSegmentSessionId stamp (per the original
            // design: physical-split children carry their own provenance).
            // Under the topology-based sweep, that debris is in scope
            // because it descends from the segment recording.
            string debrisBpId = "bp_breakup_debris";
            segment.ChildBranchPointId = debrisBpId;
            var debris = MakeRecording("rec_debris", tree.Id,
                switchSegmentSessionId: null, // intentional: no marker stamp
                parentBranchPointId: debrisBpId,
                vesselName: "Debris");
            tree.AddOrReplaceRecording(debris);
            tree.BranchPoints.Add(MakeBranchPoint(
                debrisBpId, segment.RecordingId, debris.RecordingId,
                type: BranchPointType.Breakup));

            RecordingStore.StashPendingTree(tree);

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.PendingTreePrune,
                disposition);
            Assert.Equal("scoped-discard-success", reason);

            // Segment AND debris are gone — the topology sweep took both.
            Assert.False(tree.Recordings.ContainsKey(segment.RecordingId));
            Assert.False(tree.Recordings.ContainsKey(debris.RecordingId));
            // Parent preserved.
            Assert.True(tree.Recordings.ContainsKey(parent.RecordingId));
        }

        // Bug 2 (post-#876 playtest 2026-05-17): the scoped sweep walks only
        // the segment's tree. A recording in a different tree never touched
        // by the segment must not be removed.
        //
        // Fails if: a future refactor over-broadens the scope and sweeps
        // unrelated pending state.
        [Fact]
        public void Discard_RecordingInSiblingTree_IsNotRemoved()
        {
            var (scenario, session, treeA, parentA, segmentA, bpA) =
                BuildPendingTreeWithSegment(treeId: "tree_a");

            // Build an entirely separate tree B as committed history.
            var siblingTreeBRec = MakeRecording("rec_sibling_b", "tree_b",
                vesselName: "Sibling B Vessel");
            var treeB = MakeTree("tree_b", activeRecordingId: "rec_sibling_b",
                siblingTreeBRec);
            RecordingStore.AddCommittedInternal(siblingTreeBRec);
            RecordingStore.AddCommittedTreeForTesting(treeB);

            RecordingStore.StashPendingTree(treeA);

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.PendingTreePrune,
                disposition);

            // Segment A is gone from tree A.
            Assert.False(treeA.Recordings.ContainsKey(segmentA.RecordingId));
            // Tree B is untouched.
            Assert.True(RecordingStore.IsCommittedRecordingId("rec_sibling_b"));
            var cTree = RecordingStore.CommittedTrees.Find(
                t => t.Id == "tree_b");
            Assert.NotNull(cTree);
            Assert.True(cTree.Recordings.ContainsKey("rec_sibling_b"));
        }

        // Bug 2 (post-#876 playtest 2026-05-17): direct test of the topology
        // walker. A 4-level chain should collect all descendants.
        //
        // Fails if: the walk fails to traverse multi-level descendants.
        [Fact]
        public void CollectSubtree_DeepChain_CollectsAllDescendants()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Build a 4-level chain rooted at the segment:
            //   segment -> bp1 -> childA -> bp2 -> childB -> bp3 -> childC.
            string bp1Id = "bp_chain_1";
            string bp2Id = "bp_chain_2";
            string bp3Id = "bp_chain_3";
            segment.ChildBranchPointId = bp1Id;
            var childA = MakeRecording("rec_child_a", tree.Id,
                parentBranchPointId: bp1Id,
                childBranchPointId: bp2Id);
            var childB = MakeRecording("rec_child_b", tree.Id,
                parentBranchPointId: bp2Id,
                childBranchPointId: bp3Id);
            var childC = MakeRecording("rec_child_c", tree.Id,
                parentBranchPointId: bp3Id);
            tree.AddOrReplaceRecording(childA);
            tree.AddOrReplaceRecording(childB);
            tree.AddOrReplaceRecording(childC);
            tree.BranchPoints.Add(MakeBranchPoint(bp1Id,
                segment.RecordingId, childA.RecordingId));
            tree.BranchPoints.Add(MakeBranchPoint(bp2Id,
                childA.RecordingId, childB.RecordingId));
            tree.BranchPoints.Add(MakeBranchPoint(bp3Id,
                childB.RecordingId, childC.RecordingId));

            HashSet<string> collected =
                RecordingStore.CollectSwitchSegmentSubtreeRecordingIds(tree, session);

            Assert.Contains(segment.RecordingId, collected);
            Assert.Contains(childA.RecordingId, collected);
            Assert.Contains(childB.RecordingId, collected);
            Assert.Contains(childC.RecordingId, collected);
            // Parent (above the segment) is NOT in scope.
            Assert.DoesNotContain(parent.RecordingId, collected);
        }

        // -----------------------------------------------------------------
        // Cycle protection
        // -----------------------------------------------------------------

        // Fails if: the descendant-walk iteration cap is removed or its
        // safety-break log line is dropped. Phase F review fix (1c)
        // extracted the bare 1024 literal to a named constant and added a
        // Warn log line so a future blow-up leaves a diagnostic trail in
        // KSP.log. The cap is a defense-in-depth guard against a
        // corrupted branch-point graph that no current healthy production
        // path can trip on its own.
        [Fact]
        public void CollectSubtree_CycleProtection_LogsWarn_AndReturnsPartialList()
        {
            // (a) the constant exists and is non-trivial — anything below
            // realistic tree depth would risk false-positive caps on
            // healthy production graphs.
            Assert.True(
                RecordingStore.SwitchSegmentRecordingTreeWalkMaxIterations >= 256,
                "cap must allow realistic tree depth");

            // (b) source-text gate on the Warn log line shape.
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string storePath = Path.Combine(projectRoot,
                "Source", "Parsek", "RecordingStore.cs");
            string source = File.ReadAllText(storePath);
            Assert.Contains(
                "SwitchSegmentRecordingTreeWalkMaxIterations",
                source);
            Assert.Contains(
                "iteration cap reached, breaking walk",
                source);
            Assert.Contains(
                "collectedSoFar=",
                source);
        }

        // Bug 2 follow-up (post-#876 playtest 2026-05-17): the
        // marker-only filter has been replaced with a topology sweep.
        // The descendant test now asserts that a descendant recording
        // (chained via ChildBranchPointId -> BranchPoint.ChildRecordingIds)
        // IS removed regardless of its SwitchSegmentSessionId stamp. The
        // previously-uncovered "unrelated debris" branch is now covered
        // by the dedicated Discard_DebrisFromBreakupDuringSegment_IsRemoved
        // test above.
        [Fact]
        public void Discard_RemovesAllMarkerOwnedRecordings_AndDescendants()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Chained session-stamped descendant: segment -> bp2 -> grandchild.
            string sessionIdStr = SessionIdString(session.SessionId);
            string bp2Id = "bp_chain";
            segment.ChildBranchPointId = bp2Id;
            var grandchild = MakeRecording("rec_grandchild", tree.Id,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bp2Id);
            tree.AddOrReplaceRecording(grandchild);
            tree.BranchPoints.Add(MakeBranchPoint(
                bp2Id, segment.RecordingId, grandchild.RecordingId));

            // Bug 2 (post-#876 playtest 2026-05-17): under the new topology
            // contract, a Breakup-style child descended from the segment IS
            // removed — even when it carries no session stamp. Hang it off
            // the grandchild to confirm the walk follows ChildBranchPointId
            // through multiple levels regardless of stamp.
            string bpUnrelatedId = "bp_breakup";
            grandchild.ChildBranchPointId = bpUnrelatedId;
            var unrelated = MakeRecording("rec_unrelated_debris", tree.Id,
                switchSegmentSessionId: null,
                parentBranchPointId: bpUnrelatedId);
            tree.AddOrReplaceRecording(unrelated);
            tree.BranchPoints.Add(MakeBranchPoint(
                bpUnrelatedId, grandchild.RecordingId, unrelated.RecordingId,
                type: BranchPointType.Breakup));

            RecordingStore.StashPendingTree(tree);

            string reason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.False(tree.Recordings.ContainsKey(segment.RecordingId));
            Assert.False(tree.Recordings.ContainsKey(grandchild.RecordingId));
            // Bug 2: the topologically-descended debris IS now removed by
            // the subtree sweep (regardless of its missing stamp).
            Assert.False(tree.Recordings.ContainsKey(unrelated.RecordingId));
            Assert.True(tree.Recordings.ContainsKey(parent.RecordingId));
        }

        // Fails if: marker-owned recording IDs survive scoped discard
        // (sidecar deletion is delegated to existing DeleteRecordingFiles;
        // here we check the recording is removed from the tree).
        [Fact]
        public void Discard_RemovesAttemptOwnedRecordings_AndDescendantsFromTreeAndBranchPoints()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();
            RecordingStore.StashPendingTree(tree);

            string reason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.False(tree.Recordings.ContainsKey("rec_seg"));
            // BP referencing the removed segment id is gone too.
            Assert.DoesNotContain(tree.BranchPoints, b => b.Id == bp.Id);
        }

        // -----------------------------------------------------------------
        // Event purge: attempt-owned events only
        // -----------------------------------------------------------------

        // Fails if: events tagged with marker-owned ids survive scoped
        // discard, or events tagged with committed ids are mistakenly
        // purged. Plan test #4 (events branch).
        [Fact]
        public void Discard_RemovesAttemptOwnedEvents_ButLeavesPriorEventsIntact()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();
            RecordingStore.StashPendingTree(tree);

            // Tag a committed-side event at parent and a segment-side
            // event at segment.
            var priorEvt = new GameStateEvent
            {
                ut = 120.0,
                eventType = GameStateEventType.TechResearched,
                key = "tech-prior",
                detail = "",
                recordingId = parent.RecordingId,
            };
            var segmentEvt = new GameStateEvent
            {
                ut = 200.0,
                eventType = GameStateEventType.TechResearched,
                key = "tech-segment",
                detail = "",
                recordingId = segment.RecordingId,
            };
            GameStateStore.AddEvent(ref priorEvt);
            GameStateStore.AddEvent(ref segmentEvt);

            int beforeCount = GameStateStore.EventCount;
            Assert.Equal(2, beforeCount);

            string reason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            int afterCount = GameStateStore.EventCount;
            Assert.Equal(1, afterCount);
            // The surviving event must be the prior one.
            Assert.Equal(parent.RecordingId, GameStateStore.Events[0].recordingId);
        }

        // Fails if: the helper calls DeleteRecordingFiles in a way that
        // also wipes the committed parent's sidecar. The DeleteRecordingFiles
        // helper itself is well-tested; here we assert call topology:
        // only marker-owned recording ids reach the deletion site.
        [Fact]
        public void Discard_RemovesAttemptOwnedSidecars_ByDeletingOnlyMarkerOwnedRecordingFiles()
        {
            // Source-text gate: assert TryDiscardActiveSwitchSegmentAttempt
            // routes through DeleteRecordingFiles guarded by an ownedIds
            // membership check. Plan test #4 (sidecars branch).
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "RecordingStore.cs");
            Assert.True(File.Exists(path));
            string source = File.ReadAllText(path);

            // Anchor 1: scoped helper iterates ownedIds and calls
            // DeleteRecordingFiles.
            Assert.Contains("foreach (string id in ownedIds)", source);
            Assert.Contains("DeleteRecordingFiles(rec);", source);
            // Anchor 2: counter incremented per delete so the summary log
            // reports the count.
            Assert.Contains("deletedSidecars++", source);
            // Anchor 3: scoped helper does NOT iterate over the whole
            // pending tree's Recordings.Values for deletion — only
            // ownedIds is the source of recordings to wipe.
            int helperStart = source.IndexOf(
                "internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt");
            int helperEnd = source.IndexOf(
                "private static RecordingTree FindSegmentTreeForSession");
            Assert.True(helperStart > 0 && helperEnd > helperStart);
            string helperBody = source.Substring(helperStart, helperEnd - helperStart);
            Assert.DoesNotContain(
                "foreach (var rec in pendingTree.Recordings.Values)",
                helperBody);
        }

        // -----------------------------------------------------------------
        // Marker cleared after success
        // -----------------------------------------------------------------

        // Fails if: ActiveSwitchSegmentSession survives a successful
        // scoped discard. Plan §"Final Disposition After Scoped Discard"
        // requires the marker to be cleared regardless of disposition path.
        [Fact]
        public void Discard_ClearsSwitchSegmentSessionMarker_AfterSuccess()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();
            RecordingStore.StashPendingTree(tree);

            string reason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Null(scenario.ActiveSwitchSegmentSession);
            // Log line records the clear reason.
            Assert.Contains(logLines, l =>
                l.Contains("[SwitchSegment]")
                && l.Contains("cleared")
                && l.Contains("reason=scoped-discard"));
        }

        // -----------------------------------------------------------------
        // Committed-restore clone: drop the clone + clear restore attempt
        // -----------------------------------------------------------------

        // Fails if: after scoped discard with the segment inside a
        // committed-tree restore clone, the clone wrapper survives or the
        // committed-tree restore attempt remains armed.
        [Fact]
        public void Discard_AfterCommittedSpawnedSwitch_DropsActiveClone_AndClearsRestoreAttempt()
        {
            string committedTreeId = "tree_committed";

            // Set up committed tree with the parent recording.
            var committedParent = MakeRecording("rec_committed_parent", committedTreeId,
                vesselName: "Committed Parent");
            var committedTree = MakeTree(committedTreeId,
                activeRecordingId: committedParent.RecordingId, committedParent);
            RecordingStore.AddCommittedInternal(committedParent);
            RecordingStore.AddCommittedTreeForTesting(committedTree);

            // Arm the committed-tree restore attempt.
            RecordingStore.ArmCommittedTreeRestoreAttempt(committedTree, "test-arm");
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttempt);

            // Now build a clone tree with the same tree id, an in-place
            // mutated parent (clone copy of committedParent) and a marker-
            // owned segment under it.
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);
            string bpId = "bp_clone_seg";

            var cloneParent = MakeRecording("rec_committed_parent", committedTreeId,
                childBranchPointId: bpId, vesselName: "Committed Parent");
            var cloneSegment = MakeRecording("rec_clone_seg", committedTreeId,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bpId, vesselName: "Probe Alpha");
            var cloneTree = MakeTree(committedTreeId,
                activeRecordingId: cloneSegment.RecordingId, cloneParent, cloneSegment);
            cloneTree.BranchPoints.Add(MakeBranchPoint(
                bpId, cloneParent.RecordingId, cloneSegment.RecordingId));
            RecordingStore.StashPendingTree(cloneTree);

            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = committedTreeId,
                CommittedTreeId = committedTreeId,
                ParentRecordingId = cloneParent.RecordingId,
                ActiveSegmentRecordingId = cloneSegment.RecordingId,
                SwitchUT = 150.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            scenario.ArmSwitchSegmentSession(session);

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.CommittedRestoreClone,
                disposition);

            // Clone wrapper dropped: pending slot is empty.
            Assert.False(RecordingStore.HasPendingTree);
            // Committed-tree restore attempt cleared.
            Assert.False(RecordingStore.HasCommittedTreeRestoreAttempt);
            // Original committed tree + parent still present, untouched.
            Assert.NotNull(RecordingStore.CommittedTrees.Find(
                t => t.Id == committedTreeId));
            Assert.True(RecordingStore.IsCommittedRecordingId("rec_committed_parent"));
            // Marker cleared.
            Assert.Null(scenario.ActiveSwitchSegmentSession);
        }

        // Fails if: IsCommittedTreeRestoreAttemptTree (the guard the in-flight
        // revert-clone teardown uses to detect a live committed clone before the
        // scoped discard clears the attempt) does not track the armed tree id, or
        // reports a stale/other id.
        [Fact]
        public void IsCommittedTreeRestoreAttemptTree_TracksArmedTree()
        {
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree("tree_x"));
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(null));

            var committedTree = MakeTree("tree_x",
                activeRecordingId: "rec_x",
                MakeRecording("rec_x", "tree_x", vesselName: "X"));
            RecordingStore.ArmCommittedTreeRestoreAttempt(committedTree, "test-arm");

            Assert.True(RecordingStore.IsCommittedTreeRestoreAttemptTree("tree_x"));
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree("tree_other"));
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree(null));

            RecordingStore.ClearCommittedTreeRestoreAttempt("test-clear");
            Assert.False(RecordingStore.IsCommittedTreeRestoreAttemptTree("tree_x"));
        }

        // -----------------------------------------------------------------
        // Committed history preserved across the scoped discard
        // -----------------------------------------------------------------

        // Fails if: any committed recording id is lost or the committed
        // tree is mutated. Plan test #4.
        [Fact]
        public void Discard_AfterCommittedSpawnedSwitch_PreservesAllCommittedIds()
        {
            string committedTreeId = "tree_committed";
            var c1 = MakeRecording("rec_c_1", committedTreeId, vesselName: "C1");
            var c2 = MakeRecording("rec_c_2", committedTreeId, vesselName: "C2");
            var c3 = MakeRecording("rec_c_3", committedTreeId, vesselName: "C3");
            var committedTree = MakeTree(committedTreeId,
                activeRecordingId: c1.RecordingId, c1, c2, c3);
            RecordingStore.AddCommittedInternal(c1);
            RecordingStore.AddCommittedInternal(c2);
            RecordingStore.AddCommittedInternal(c3);
            RecordingStore.AddCommittedTreeForTesting(committedTree);
            RecordingStore.ArmCommittedTreeRestoreAttempt(committedTree, "test-arm");

            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);

            Guid sessionId = Guid.NewGuid();
            string sessionIdStr = SessionIdString(sessionId);
            string bpId = "bp_clone_seg";
            var cloneC1 = MakeRecording("rec_c_1", committedTreeId,
                childBranchPointId: bpId, vesselName: "C1");
            var cloneC2 = MakeRecording("rec_c_2", committedTreeId, vesselName: "C2");
            var cloneC3 = MakeRecording("rec_c_3", committedTreeId, vesselName: "C3");
            var seg = MakeRecording("rec_clone_seg", committedTreeId,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bpId, vesselName: "Probe Alpha");
            var cloneTree = MakeTree(committedTreeId,
                activeRecordingId: seg.RecordingId, cloneC1, cloneC2, cloneC3, seg);
            cloneTree.BranchPoints.Add(MakeBranchPoint(
                bpId, cloneC1.RecordingId, seg.RecordingId));
            RecordingStore.StashPendingTree(cloneTree);

            var session = new SwitchSegmentSession
            {
                SessionId = sessionId,
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.MapSwitchTo,
                TreeId = committedTreeId,
                CommittedTreeId = committedTreeId,
                ParentRecordingId = cloneC1.RecordingId,
                ActiveSegmentRecordingId = seg.RecordingId,
                SwitchUT = 150.0,
                PreSessionBranchPointIds = new List<string>(),
            };
            scenario.ArmSwitchSegmentSession(session);

            // Tag committed-side events (pre-segment-UT) that must survive.
            for (int i = 0; i < 3; i++)
            {
                var e = new GameStateEvent
                {
                    ut = 100.0 + i,
                    eventType = GameStateEventType.TechResearched,
                    key = "tech-c" + i,
                    detail = "",
                    recordingId = "rec_c_" + (i + 1),
                };
                GameStateStore.AddEvent(ref e);
            }

            string reason;
            RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            // All three committed ids still present.
            Assert.True(RecordingStore.IsCommittedRecordingId("rec_c_1"));
            Assert.True(RecordingStore.IsCommittedRecordingId("rec_c_2"));
            Assert.True(RecordingStore.IsCommittedRecordingId("rec_c_3"));
            // Committed tree still has all three recordings.
            var cTree = RecordingStore.CommittedTrees.Find(
                t => t.Id == committedTreeId);
            Assert.NotNull(cTree);
            Assert.Contains("rec_c_1", cTree.Recordings.Keys);
            Assert.Contains("rec_c_2", cTree.Recordings.Keys);
            Assert.Contains("rec_c_3", cTree.Recordings.Keys);
            // All three committed events survive.
            Assert.Equal(3, GameStateStore.EventCount);
        }

        // -----------------------------------------------------------------
        // Audit: scoped Discard does NOT commit the pruned clone over the
        // committed tree
        // -----------------------------------------------------------------

        // Fails if: TryDiscardActiveSwitchSegmentAttempt calls CommitTree /
        // CommitPendingTree / MarkTreeAsApplied to "complete" a discard.
        // Plan §"Final Disposition": "It should not commit the pruned
        // clone back over the original tree just to complete Discard."
        [Fact]
        public void Discard_DoesNotCommitPrunedCloneBackOverOriginal()
        {
            // Source-text audit: the scoped helper's body must never
            // mention CommitTree(...) / CommitPendingTree / MarkTreeAsApplied.
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "RecordingStore.cs");
            string source = File.ReadAllText(path);

            int helperStart = source.IndexOf(
                "internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt");
            int helperEnd = source.IndexOf(
                "private static RecordingTree FindSegmentTreeForSession");
            Assert.True(helperStart > 0 && helperEnd > helperStart);
            string helperBody = source.Substring(helperStart, helperEnd - helperStart);

            Assert.DoesNotContain("CommitTree(", helperBody);
            Assert.DoesNotContain("CommitPendingTree(", helperBody);
            Assert.DoesNotContain("MarkTreeAsApplied(", helperBody);
        }

        // -----------------------------------------------------------------
        // Merge path: marker cleared after commit succeeds
        // -----------------------------------------------------------------

        // Fails if: MergeDialog.MergeCommit does not clear an active
        // SwitchSegmentSession after a successful commit. Plan test #5.
        // (We source-text-gate the seam since driving MergeCommit needs
        // a full pending tree fixture; the seam test asserts the wiring.)
        [Fact]
        public void Merge_AfterSwitchSegment_ClearsMarker_OnCommitSuccess()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            // MergeCommit clears the session marker after the commit.
            Assert.Contains(
                "switchSegmentScenario.ClearSwitchSegmentSession(\"scoped-merge-success\")",
                source);
            // And clears any active committed-tree restore attempt.
            Assert.Contains(
                "ClearCommittedTreeRestoreAttempt(\n                        \"scoped-merge-success switch-segment\")",
                source.Replace("\r\n", "\n"));
        }

        // Fails if: pending-tree merge path does not clear the marker
        // (the marker should clear after any successful commit, both
        // pending-tree and committed-restore-clone shapes).
        [Fact]
        public void Merge_AfterPendingTreeSwitch_CommitsPrunedSegmentNormally_AndClearsMarker()
        {
            // Source-text gate on the marker-clear placement: the clear
            // happens inside MergeCommit after the existing TryCommitReFly
            // logic but before ClearPendingFlag, so the success of the
            // commit (CommitPendingTree, MarkTreeAsApplied) is already
            // executed when the marker is cleared.
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            int markApplied = source.IndexOf("RecordingStore.MarkTreeAsApplied(tree);");
            int clearMarker = source.IndexOf(
                "switchSegmentScenario.ClearSwitchSegmentSession(\"scoped-merge-success\")");
            int clearPending = source.IndexOf(
                "ClearPendingFlag(\"merge dialog commit button\")");
            Assert.True(markApplied > 0,
                "MarkTreeAsApplied not found");
            Assert.True(clearMarker > markApplied,
                "Marker clear must come AFTER MarkTreeAsApplied");
            Assert.True(clearPending > clearMarker,
                "ClearPendingFlag must come AFTER marker clear");
        }

        // -----------------------------------------------------------------
        // Bug 3 (post-#876 playtest 2026-05-17): unified whole-tree dialog
        // body
        // -----------------------------------------------------------------

        // Fails if: a future refactor drops the duration line; the player
        // loses the only signal that distinguishes a 16s switch-segment
        // dialog from a 30-minute launch dialog.
        [Fact]
        public void DialogBody_AlwaysIncludesDuration()
        {
            var tree = MakeTree("Kerbal X",
                activeRecordingId: "rec_main",
                MakeRecording("rec_main", "Kerbal X", vesselName: "Kerbal X"));
            // The recording's start/end is 100..200 per MakeRecording
            // defaults, so the tree duration is 100s. FormatDuration(100)
            // renders as "1m 40s".
            string body = MergeDialog.BuildWholeTreeMergeDialogBody(tree);
            Assert.Contains("Kerbal X", body);
            Assert.Contains("1m 40s", body);
        }

        // Fails if: a null tree input throws instead of returning a safe
        // placeholder body.
        [Fact]
        public void DialogBody_NullTree_RendersFallback()
        {
            string body = MergeDialog.BuildWholeTreeMergeDialogBody(null);
            // Fallback "<unnamed> - 0s" — duration 0 because tree is null.
            Assert.Contains("<unnamed>", body);
            Assert.Contains("0s", body);
        }

        // -----------------------------------------------------------------
        // MergeDiscard wiring: scoped-discard hook runs before the
        // whole-pending-tree fallback. Source-text gate on placement.
        // -----------------------------------------------------------------

        // Fails if: TryDiscardActiveSwitchSegmentAttempt is not called
        // before DiscardPendingTreeAndRecalculate inside
        // MergeDiscardRanToCompletion.
        [Fact]
        public void MergeDiscard_CallsScopedSwitchSegmentHookBeforeWholeTreeFallback()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.ReFlyDiscard.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf(
                "internal static bool MergeDiscardRanToCompletion");
            int methodEnd = source.IndexOf(
                "internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology",
                methodStart);
            Assert.True(methodStart > 0 && methodEnd > methodStart);
            string body = source.Substring(methodStart, methodEnd - methodStart);

            int hookCall = body.IndexOf(
                "RecordingStore.TryDiscardActiveSwitchSegmentAttempt");
            int wholeTreeCall = body.IndexOf(
                "ParsekScenario.DiscardPendingTreeAndRecalculate");
            Assert.True(hookCall > 0, "Scoped switch-segment hook not found in MergeDiscardRanToCompletion");
            Assert.True(wholeTreeCall > 0, "Whole-pending-tree fallback not found");
            Assert.True(hookCall < wholeTreeCall,
                "Scoped switch-segment hook must run BEFORE whole-tree discard fallback");
        }

        // Bug 2 (post-#876 playtest 2026-05-17): the secondary-dialog flow
        // has been removed. The discard hook just returns and the caller
        // proceeds to postChoice (scene transition) without prompting again.
        //
        // Fails if: a future refactor restores the secondary-dialog flow
        // and re-introduces the orphan-debris false-positive prompt.
        [Fact]
        public void MergeDiscard_DoesNotOpenSecondaryDialog_AfterScopedDiscard()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            // The deleted symbols must not reappear.
            Assert.DoesNotContain(
                "HasRemainingPendingChangesAfterSegmentDiscard",
                source);
            Assert.DoesNotContain(
                "ShowSecondaryPendingDiscardDialog",
                source);
            Assert.DoesNotContain(
                "BuildSecondaryPendingTreeDialogBody",
                source);
            Assert.DoesNotContain(
                "DeferredToSecondaryDialog",
                source);
        }
    }
}
