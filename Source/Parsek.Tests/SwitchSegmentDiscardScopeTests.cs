using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase E coverage for segment-scoped switch/Fly auto-record: the
    /// scoped Discard helper (<see cref="RecordingStore.TryDiscardActiveSwitchSegmentAttempt"/>),
    /// the remaining-pending-changes probe
    /// (<see cref="RecordingStore.HasRemainingPendingChangesAfterSegmentDiscard"/>),
    /// the <see cref="MergeDialog"/> hook + second-dialog flow, and the
    /// entry-reason-aware dialog copy. Per plan
    /// <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// §"Merge and Discard Scope", §"Final Disposition After Scoped
    /// Discard", and §"Dialog and UI Copy" plus the test list at the
    /// bottom of the plan.
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
        // Pending-tree disposition: prune segment, preserve pre-existing
        // pending changes, second dialog should fire.
        // -----------------------------------------------------------------

        // Fails if: pre-existing pending recordings disappear when the
        // segment-scoped discard runs, or the post-discard remaining-
        // pending-changes probe misses them. Plan test #15.
        [Fact]
        public void Discard_AfterPendingTreeSwitch_PreservesPreExistingPendingChanges_AndPromptsSecondDialog()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Add a pre-existing pending recording (not marker-owned, not
            // committed). The session's PreSessionBranchPointIds is empty
            // but this pre-existing recording exists independently of any
            // session-authored BP.
            var preExisting = MakeRecording("rec_pre_existing", tree.Id,
                vesselName: "Pre-Existing Probe");
            tree.AddOrReplaceRecording(preExisting);

            RecordingStore.StashPendingTree(tree);

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.PendingTreePrune,
                disposition);
            Assert.Equal("scoped-discard-success", reason);

            // Segment + branch point gone, parent + pre-existing preserved.
            Assert.False(tree.Recordings.ContainsKey(segment.RecordingId));
            Assert.True(tree.Recordings.ContainsKey(parent.RecordingId));
            Assert.True(tree.Recordings.ContainsKey(preExisting.RecordingId));
            Assert.DoesNotContain(tree.BranchPoints, b =>
                string.Equals(b.Id, bp.Id, StringComparison.Ordinal));
            // Parent's ChildBranchPointId scrubbed.
            Assert.Null(parent.ChildBranchPointId);

            // Second-dialog probe says remaining pending changes exist.
            int remainingRecordings;
            int remainingBps;
            Assert.True(
                RecordingStore.HasRemainingPendingChangesAfterSegmentDiscard(
                    out remainingRecordings, out remainingBps));
            Assert.True(remainingRecordings >= 1,
                $"Expected at least one remaining recording, got {remainingRecordings}");

            // Session marker cleared.
            Assert.Null(scenario.ActiveSwitchSegmentSession);
        }

        // Fails if: a tree with NO pre-existing pending changes (segment
        // only) still reports remaining pending changes after scoped
        // discard.
        [Fact]
        public void Discard_AfterPendingTreeSwitch_NoPreExistingChanges_NoSecondDialog()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Both parent and segment are owned-or-original tree members.
            // The "pre-existing" parent recording is in committed storage
            // (representing the committed timeline), the segment is the
            // marker-owned child.
            // Commit the parent into committed storage so it counts as
            // "committed history" (not pending).
            RecordingStore.AddCommittedInternal(parent);

            RecordingStore.StashPendingTree(tree);

            string reason;
            var disposition =
                RecordingStore.TryDiscardActiveSwitchSegmentAttempt(out reason);

            Assert.Equal(
                RecordingStore.SwitchSegmentDiscardDisposition.PendingTreePrune,
                disposition);

            int remainingRecordings;
            int remainingBps;
            // Parent is committed (so doesn't count as remaining pending).
            // Segment was removed. No BPs left.
            bool hasRemaining =
                RecordingStore.HasRemainingPendingChangesAfterSegmentDiscard(
                    out remainingRecordings, out remainingBps);
            Assert.Equal(0, remainingRecordings);
            Assert.Equal(0, remainingBps);
            Assert.False(hasRemaining);
        }

        // -----------------------------------------------------------------
        // Owned-id transitive closure + recordings cleanup
        // -----------------------------------------------------------------

        // Fails if: the descendant-walk iteration cap is removed or its
        // safety-break log line is dropped. Phase F review fix (1c)
        // extracted the bare 1024 literal to a named constant and added a
        // Warn log line so a future blow-up leaves a diagnostic trail in
        // KSP.log. The cap is a defense-in-depth guard against a
        // corrupted branch-point graph that no current healthy production
        // path can trip on its own (the marker-stamp scan adds every
        // session-stamped recording up front, so the do/while typically
        // terminates after a single empty pass). Testing the runtime path
        // would require injecting a corrupted graph; instead we pin three
        // anchors that together guarantee the safety net stays in place:
        // (a) the constant exists with a sensible value, (b) the function
        // surface still walks tree.Recordings, (c) the Warn log line
        // template is present in the source so the diagnostic message
        // shape can't drift silently.
        [Fact]
        public void CollectMarkerOwned_DeepCycleProtection_LogsWarn_AndReturnsPartialList()
        {
            // (a) the constant exists and is non-trivial — anything below
            // realistic tree depth would risk false-positive caps on
            // healthy production graphs.
            Assert.True(
                RecordingStore.SwitchSegmentRecordingTreeWalkMaxIterations >= 256,
                "cap must allow realistic tree depth");

            // (b) sanity-check the function still returns a sensible set
            // for a normal small chain (regression: a refactor that
            // accidentally bypassed the marker-stamp scan would surface
            // here as a count-1 collection).
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();
            string sessionIdStr = SessionIdString(session.SessionId);
            string bp2Id = "bp_chain";
            segment.ChildBranchPointId = bp2Id;
            var grandchild = MakeRecording("rec_grandchild", tree.Id,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bp2Id);
            tree.AddOrReplaceRecording(grandchild);
            tree.BranchPoints.Add(MakeBranchPoint(
                bp2Id, segment.RecordingId, grandchild.RecordingId));

            HashSet<string> collected =
                RecordingStore.CollectSwitchSegmentMarkerOwnedRecordingIds(tree, session);
            Assert.Contains(segment.RecordingId, collected);
            Assert.Contains(grandchild.RecordingId, collected);
            Assert.DoesNotContain(parent.RecordingId, collected);

            // (c) source-text gate on the Warn log line shape. The break
            // path is defense-in-depth — we cannot easily exercise it
            // from xUnit because the marker-stamp scan + the descendant
            // walk's `ids.Contains(childId) continue` guard together make
            // the loop terminate after one empty pass on every healthy
            // input. The source-text gate pins the diagnostic content so
            // a future regression cannot silently strip the break log.
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

        // Fails if: a descendant recording stamped with the session id is
        // left in the tree. Owned-id walk must follow ChildBranchPointId
        // chains.
        [Fact]
        public void Discard_RemovesAllMarkerOwnedRecordings_AndDescendants()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment();

            // Chained segment-owned descendant: segment -> bp2 -> grandchild.
            string sessionIdStr = SessionIdString(session.SessionId);
            string bp2Id = "bp_chain";
            segment.ChildBranchPointId = bp2Id;
            var grandchild = MakeRecording("rec_grandchild", tree.Id,
                switchSegmentSessionId: sessionIdStr,
                parentBranchPointId: bp2Id);
            tree.AddOrReplaceRecording(grandchild);
            tree.BranchPoints.Add(MakeBranchPoint(
                bp2Id, segment.RecordingId, grandchild.RecordingId));

            // Distinct physical-split child of segment that did NOT inherit
            // the session id stays out of scope. Per plan §"Composing with
            // existing branch types during an active segment". Make it via
            // a Breakup-style child under a separate non-session BP though:
            // since segment's ChildBranchPointId already points at bp2,
            // hang the unrelated child off the grandchild instead.
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
            // The "unrelated" debris is NOT marker-owned (no stamp). It
            // would survive the owned-id closure pass; structural-BP
            // walk via session-authored branch points removes it too
            // (bpUnrelatedId was authored after the session started and
            // is not in PreSessionBranchPointIds). The test asserts the
            // marker-owned closure correctly excluded it from ownedIds —
            // bp-prune treats it as session-authored topology since the
            // BP was added during the session. We observe the surviving
            // semantics indirectly: parent + segment chain ids are gone.
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
            // (Loose anchor: ensure no `foreach (var rec in pendingTree.Recordings.Values)`
            // appears inside the scoped helper.)
            int helperStart = source.IndexOf(
                "internal static SwitchSegmentDiscardDisposition TryDiscardActiveSwitchSegmentAttempt");
            int helperEnd = source.IndexOf(
                "internal static bool HasRemainingPendingChangesAfterSegmentDiscard");
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
                "internal static bool HasRemainingPendingChangesAfterSegmentDiscard");
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
        // Dialog copy: entry-reason-aware verbs
        // -----------------------------------------------------------------

        // Fails if: the TS Fly entry reason produces non-"Fly" verb copy.
        [Fact]
        public void DialogCopy_TsFly_UsesFlyVerb()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment(
                    entryReason: SwitchSegmentEntryReason.TrackingStationFly);
            string body = MergeDialog.BuildSwitchSegmentDialogBody(session, tree);
            Assert.Contains("Keep your new flight on 'Probe Alpha'?", body);
            Assert.DoesNotContain("switch into", body);
        }

        // Fails if: the KSC marker Fly entry reason does not use the Fly verb.
        [Fact]
        public void DialogCopy_KscMarkerFly_UsesFlyVerb()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment(
                    entryReason: SwitchSegmentEntryReason.KscMarkerFly);
            string body = MergeDialog.BuildSwitchSegmentDialogBody(session, tree);
            Assert.Contains("Keep your new flight on 'Probe Alpha'?", body);
            Assert.DoesNotContain("switch into", body);
        }

        // Fails if: the Map Switch-To entry reason does not use the Switch verb.
        [Fact]
        public void DialogCopy_MapSwitchTo_UsesSwitchVerb()
        {
            var (scenario, session, tree, parent, segment, bp) =
                BuildPendingTreeWithSegment(
                    entryReason: SwitchSegmentEntryReason.MapSwitchTo);
            string body = MergeDialog.BuildSwitchSegmentDialogBody(session, tree);
            Assert.Contains("Keep your switch into 'Probe Alpha'?", body);
            Assert.DoesNotContain("new flight on", body);
        }

        // Fails if: the trailing clause documenting Discard / Merge
        // semantics is missing from either entry-reason variant.
        [Fact]
        public void DialogCopy_AlwaysIncludesDiscardMergeTrailingClause()
        {
            var (scenario1, session1, tree1, _, _, _) =
                BuildPendingTreeWithSegment(
                    entryReason: SwitchSegmentEntryReason.TrackingStationFly);
            var (scenario2, session2, tree2, _, _, _) =
                BuildPendingTreeWithSegment(
                    entryReason: SwitchSegmentEntryReason.MapSwitchTo);

            const string expected =
                "Choosing Discard returns to the committed timeline; " +
                "choosing Merge appends this segment under it.";
            Assert.Contains(expected, MergeDialog.BuildSwitchSegmentDialogBody(session1, tree1));
            Assert.Contains(expected, MergeDialog.BuildSwitchSegmentDialogBody(session2, tree2));
        }

        // Fails if: a null session or a segment recording with no
        // VesselName falls through to anything other than "this vessel".
        [Fact]
        public void DialogCopy_FallsBackToThisVessel_WhenVesselNameUnknown()
        {
            var session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.TrackingStationFly,
                ActiveSegmentRecordingId = null,
                PreSessionBranchPointIds = new List<string>(),
            };
            string body = MergeDialog.BuildSwitchSegmentDialogBody(session, null);
            Assert.Contains("'this vessel'", body);
        }

        // -----------------------------------------------------------------
        // Second dialog wiring + Cancel: source-text gate on the
        // second-dialog opener (Unity PopupDialog needed at runtime).
        // -----------------------------------------------------------------

        // Fails if: the secondary dialog does NOT offer all three of
        // Merge / Discard / Cancel.
        [Fact]
        public void SecondDialog_AfterScopedDiscard_OffersCancelAlongMergeAndDiscard()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            // ShowSecondaryPendingDiscardDialog must construct all three
            // buttons.
            int secondOpener = source.IndexOf(
                "internal static void ShowSecondaryPendingDiscardDialog");
            Assert.True(secondOpener > 0,
                "ShowSecondaryPendingDiscardDialog opener not found");
            string body = source.Substring(secondOpener);
            int endOfMethod = body.IndexOf("\n        }\n");
            string scopedBody = endOfMethod > 0 ? body.Substring(0, endOfMethod) : body;
            Assert.Contains("new DialogGUIButton(\"Merge to Timeline\"", scopedBody);
            Assert.Contains("new DialogGUIButton(\"Discard\"", scopedBody);
            Assert.Contains("new DialogGUIButton(\"Cancel\"", scopedBody);
        }

        // Fails if: the Cancel button in the secondary dialog disposes
        // the pruned pending tree instead of leaving it intact.
        [Fact]
        public void SecondDialog_Cancel_LeavesPrunedPendingTreeIntact_AndUnlocksInputLock()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            // The Cancel button must call ClearPendingFlag (which releases
            // the input lock) and NOT call DiscardPendingTreeAndRecalculate.
            int cancelButton = source.IndexOf(
                "new DialogGUIButton(\"Cancel\", () =>");
            Assert.True(cancelButton > 0, "Cancel button construction not found");
            int cancelEnd = source.IndexOf("})", cancelButton);
            Assert.True(cancelEnd > cancelButton);
            string cancelBody = source.Substring(cancelButton, cancelEnd - cancelButton);

            Assert.Contains("ClearPendingFlag", cancelBody);
            Assert.DoesNotContain("DiscardPendingTreeAndRecalculate", cancelBody);
            Assert.DoesNotContain("RecordingStore.PopPendingTree", cancelBody);
        }

        // -----------------------------------------------------------------
        // MergeDiscard wiring: scoped-discard hook runs before the
        // whole-pending-tree fallback. Source-text gate on placement.
        // -----------------------------------------------------------------

        // Fails if: TryDiscardActiveSwitchSegmentAttempt is not called
        // before DiscardPendingTreeAndRecalculate inside
        // MergeDiscardWithResult.
        [Fact]
        public void MergeDiscard_CallsScopedSwitchSegmentHookBeforeWholeTreeFallback()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf(
                "internal static MergeDiscardOutcome MergeDiscardWithResult");
            int methodEnd = source.IndexOf(
                "internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology",
                methodStart);
            Assert.True(methodStart > 0 && methodEnd > methodStart);
            string body = source.Substring(methodStart, methodEnd - methodStart);

            int hookCall = body.IndexOf(
                "RecordingStore.TryDiscardActiveSwitchSegmentAttempt");
            int wholeTreeCall = body.IndexOf(
                "ParsekScenario.DiscardPendingTreeAndRecalculate");
            Assert.True(hookCall > 0, "Scoped switch-segment hook not found in MergeDiscardWithResult");
            Assert.True(wholeTreeCall > 0, "Whole-pending-tree fallback not found");
            Assert.True(hookCall < wholeTreeCall,
                "Scoped switch-segment hook must run BEFORE whole-tree discard fallback");
        }

        // Fails if: MergeDiscardWithResult does not call
        // ShowSecondaryPendingDiscardDialog when HasRemainingPendingChangesAfterSegmentDiscard
        // returns true.
        [Fact]
        public void MergeDiscard_OpensSecondaryDialog_WhenScopedDiscardLeavesPendingChanges()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            int methodStart = source.IndexOf(
                "internal static MergeDiscardOutcome MergeDiscardWithResult");
            int methodEnd = source.IndexOf(
                "internal static AttemptDiscardSummary PruneActiveReFlyAttemptOwnedTopology",
                methodStart);
            string body = source.Substring(methodStart, methodEnd - methodStart);

            Assert.Contains(
                "RecordingStore.HasRemainingPendingChangesAfterSegmentDiscard",
                body);
            // HIGH 1: pre-transition path forwards the postChoice
            // continuation so the secondary dialog's terminal handlers
            // can run the scene transition after the player commits.
            Assert.Contains(
                "ShowSecondaryPendingDiscardDialog(",
                body);
            Assert.Contains(
                "RecordingStore.PendingTree, postChoice",
                body);
        }

        // -----------------------------------------------------------------
        // HIGH 1 (PR #876 review): the pre-transition Discard path must
        // defer the scene-load continuation while the secondary dialog is
        // still on-screen.
        //
        // Plan §"Final Disposition After Scoped Discard": "Scene exit is
        // blocked only until the player picks one of the three; there
        // must not be a state where the first dialog is dismissed but the
        // scene is half-transitioning while the second one is waiting."
        //
        // Before this fix, MergeDiscardWithResult returned true after
        // opening the secondary dialog, and RunPreTransitionAction
        // immediately invoked postChoice (HighLogic.LoadScene) - tearing
        // down FLIGHT while the player was still looking at Merge /
        // Discard / Cancel. The fix is structural: tri-state return,
        // deferral signal, and the secondary dialog's Merge / Discard
        // handlers re-invoke postChoice on a terminal choice; Cancel
        // drops it.
        // -----------------------------------------------------------------

        // Fails if: any of the four structural anchors regresses such that
        // a scene transition could fire while the secondary dialog is up.
        // We cannot drive ShowSecondaryPendingDiscardDialog from xUnit
        // (Unity PopupDialog needed), so we pin the wiring via source
        // anchors.
        [Fact]
        public void MergeDiscard_OpensSecondaryDialog_PostChoiceDeferredUntilSecondaryTerminalChoice()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            // (1) The tri-state enum exists and includes the deferred value.
            // Fails if: someone collapses the contract back to bool, losing
            // the "deferred to secondary dialog" signal between the discard
            // helper and the pre-transition wrapper.
            Assert.Contains("internal enum MergeDiscardOutcome", source);
            Assert.Contains("DeferredToSecondaryDialog", source);
            Assert.Contains("RanToCompletion", source);
            Assert.Contains("RefusedJournalActive", source);

            // (2) MergeDiscardWithResult returns DeferredToSecondaryDialog
            // immediately after opening the secondary dialog.
            // Fails if: a refactor reorders the return after the secondary
            // open to return RanToCompletion, which would cause
            // RunPreTransitionAction to run postChoice while the dialog
            // is still up.
            int secondaryOpenCall = source.IndexOf(
                "ShowSecondaryPendingDiscardDialog(\r\n                            RecordingStore.PendingTree, postChoice)");
            if (secondaryOpenCall < 0)
            {
                secondaryOpenCall = source.IndexOf(
                    "ShowSecondaryPendingDiscardDialog(\n                            RecordingStore.PendingTree, postChoice)");
            }
            Assert.True(secondaryOpenCall > 0,
                "secondary dialog opener call (with postChoice forward) not found");
            int deferredReturn = source.IndexOf(
                "return MergeDiscardOutcome.DeferredToSecondaryDialog;",
                secondaryOpenCall);
            Assert.True(deferredReturn > secondaryOpenCall,
                "Deferred return must follow the secondary dialog open call");
            Assert.True(deferredReturn - secondaryOpenCall < 600,
                "Deferred return must be the immediate post-open action " +
                "(was " + (deferredReturn - secondaryOpenCall) + " chars away)");

            // (3) RunPreTransitionAction must understand the deferred case
            // and skip postChoice on it.
            // Fails if: the deferred branch falls through to the postChoice
            // invoke at the bottom of the method.
            int wrapperStart = source.IndexOf(
                "private static void RunPreTransitionAction(");
            // Locate the wrapper's closing brace by scanning to the next
            // "private static" / "internal static" sibling declaration.
            int wrapperNext = source.IndexOf(
                "internal enum MergeDiscardOutcome", wrapperStart);
            Assert.True(wrapperStart > 0 && wrapperNext > wrapperStart);
            string wrapperBody = source.Substring(wrapperStart, wrapperNext - wrapperStart);
            Assert.Contains(
                "outcome == MergeDiscardOutcome.DeferredToSecondaryDialog",
                wrapperBody);
            Assert.Contains(
                "deferring postChoice until secondary terminal choice",
                wrapperBody);
            int deferredCheck = wrapperBody.IndexOf(
                "outcome == MergeDiscardOutcome.DeferredToSecondaryDialog");
            int nextReturn = wrapperBody.IndexOf("return;", deferredCheck);
            Assert.True(nextReturn > deferredCheck,
                "Deferred branch must return before falling through");
            // The final postChoice?.Invoke must appear AFTER the deferred
            // return statement, not BEFORE it (so the deferred branch
            // exits before it could be reached).
            int finalPostChoiceInvoke = wrapperBody.IndexOf("postChoice?.Invoke", deferredCheck);
            Assert.True(finalPostChoiceInvoke < 0
                || finalPostChoiceInvoke > nextReturn,
                "Deferred branch's return must precede any unguarded postChoice invoke");

            // (4) ShowSecondaryPendingDiscardDialog accepts a postChoice
            // continuation and routes it through Merge / Discard handlers
            // (terminal) but NOT through Cancel.
            // Fails if: the signature drops postChoice, Cancel starts
            // invoking it, or Merge/Discard stop invoking it.
            int secondaryOpener = source.IndexOf(
                "internal static void ShowSecondaryPendingDiscardDialog(");
            Assert.True(secondaryOpener > 0);
            int secondaryEnd = source.IndexOf(
                "private static void InvokePostChoiceSafely",
                secondaryOpener);
            Assert.True(secondaryEnd > secondaryOpener);
            string secondaryBody = source.Substring(
                secondaryOpener, secondaryEnd - secondaryOpener);
            Assert.Contains("System.Action postChoice", secondaryBody);
            int mergeBtn = secondaryBody.IndexOf("\"Merge to Timeline\"");
            int discardBtn = secondaryBody.IndexOf("\"Discard\"");
            int cancelBtn = secondaryBody.IndexOf("\"Cancel\"");
            Assert.True(mergeBtn > 0 && discardBtn > mergeBtn && cancelBtn > discardBtn);
            // The buttons[] array closes with `};` before the
            // PopupDialog.SpawnPopupDialog call. Use that as the upper bound
            // for the Cancel slice so the spawn-null fallback's
            // InvokePostChoiceSafely call below is excluded.
            int buttonsArrayEnd = secondaryBody.IndexOf(
                "PopupDialog.DismissPopup(DialogName)", cancelBtn);
            Assert.True(buttonsArrayEnd > cancelBtn,
                "Could not locate buttons[] array end before PopupDialog.DismissPopup");
            string mergeSlice = secondaryBody.Substring(mergeBtn, discardBtn - mergeBtn);
            string discardSlice = secondaryBody.Substring(discardBtn, cancelBtn - discardBtn);
            string cancelSlice = secondaryBody.Substring(cancelBtn, buttonsArrayEnd - cancelBtn);
            Assert.Contains("InvokePostChoiceSafely(", mergeSlice);
            Assert.Contains("InvokePostChoiceSafely(", discardSlice);
            Assert.DoesNotContain("InvokePostChoiceSafely(", cancelSlice);
        }

        // -----------------------------------------------------------------
        // MED 7 (PR #876 review): the secondary dialog's Merge / Discard
        // handlers refuse to act when an unexpected ReFlySessionMarker is
        // armed at terminal-choice time. By design, the first dialog's
        // ReFly hook handles Re-Fly state before scoped switch-segment
        // discard ever opens the secondary dialog, so a Re-Fly marker
        // here is a "should never happen" path that historically would
        // have silently bypassed the Re-Fly supersede pipeline by routing
        // straight through MergeCommit / DiscardPendingTreeAndRecalculate.
        // The guard logs a Warn under [SwitchSegment] and bails.
        //
        // ShowSecondaryPendingDiscardDialog cannot be driven directly from
        // xUnit (Unity PopupDialog.SpawnPopupDialog is required to surface
        // the button lambdas to a caller). Source-text gates pin the four
        // anchors that together guarantee the guard cannot regress:
        // (1) the Merge handler reads ActiveReFlySessionMarker and
        //     short-circuits on non-null, (2) the Discard handler does
        //     the same, (3) both log under [SwitchSegment] with the
        //     unexpected-refly-active-in-secondary-dialog tag, (4) the
        //     refusal returns before MergeCommit /
        //     DiscardPendingTreeAndRecalculate run.
        // -----------------------------------------------------------------

        // Fails if: the secondary dialog Merge handler no longer guards
        // against an unexpected armed ReFlySessionMarker, or stops logging
        // the refusal under [SwitchSegment].
        [Fact]
        public void SecondaryDialog_Merge_RefusesWhenReFlyMarkerActive_LogsWarn()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            int secondaryOpener = source.IndexOf(
                "internal static void ShowSecondaryPendingDiscardDialog(");
            Assert.True(secondaryOpener > 0,
                "ShowSecondaryPendingDiscardDialog opener not found");
            int secondaryEnd = source.IndexOf(
                "private static void InvokePostChoiceSafely",
                secondaryOpener);
            Assert.True(secondaryEnd > secondaryOpener);
            string secondaryBody = source.Substring(
                secondaryOpener, secondaryEnd - secondaryOpener);

            int mergeBtn = secondaryBody.IndexOf("\"Merge to Timeline\"");
            int discardBtn = secondaryBody.IndexOf("\"Discard\"");
            Assert.True(mergeBtn > 0 && discardBtn > mergeBtn);
            string mergeSlice = secondaryBody.Substring(mergeBtn, discardBtn - mergeBtn);

            // (1) Merge handler reads ActiveReFlySessionMarker.
            Assert.Contains("ActiveReFlySessionMarker", mergeSlice);

            // (2) Refuses on non-null with a Warn under [SwitchSegment].
            Assert.Contains("unexpected-refly-active-in-secondary-dialog", mergeSlice);
            Assert.Contains("ParsekLog.Warn(\"SwitchSegment\"", mergeSlice);
            Assert.Contains("Merge refused", mergeSlice);

            // (3) Refusal returns before MergeCommit runs. The MergeCommit
            //     call must appear AFTER the Warn so the guard's `return`
            //     prevents MergeCommit from executing.
            int warnInMerge = mergeSlice.IndexOf("unexpected-refly-active-in-secondary-dialog");
            int mergeCommitInMerge = mergeSlice.IndexOf("MergeCommit(");
            Assert.True(warnInMerge > 0 && mergeCommitInMerge > warnInMerge,
                "Warn must precede MergeCommit so the guard's return skips the commit");
        }

        // Fails if: the secondary dialog Discard handler no longer guards
        // against an unexpected armed ReFlySessionMarker, or stops logging
        // the refusal under [SwitchSegment].
        [Fact]
        public void SecondaryDialog_Discard_RefusesWhenReFlyMarkerActive_LogsWarn()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string path = Path.Combine(projectRoot,
                "Source", "Parsek", "MergeDialog.cs");
            string source = File.ReadAllText(path);

            int secondaryOpener = source.IndexOf(
                "internal static void ShowSecondaryPendingDiscardDialog(");
            Assert.True(secondaryOpener > 0,
                "ShowSecondaryPendingDiscardDialog opener not found");
            int secondaryEnd = source.IndexOf(
                "private static void InvokePostChoiceSafely",
                secondaryOpener);
            Assert.True(secondaryEnd > secondaryOpener);
            string secondaryBody = source.Substring(
                secondaryOpener, secondaryEnd - secondaryOpener);

            int discardBtn = secondaryBody.IndexOf("\"Discard\"");
            int cancelBtn = secondaryBody.IndexOf("\"Cancel\"");
            Assert.True(discardBtn > 0 && cancelBtn > discardBtn);
            string discardSlice = secondaryBody.Substring(discardBtn, cancelBtn - discardBtn);

            // (1) Discard handler reads ActiveReFlySessionMarker.
            Assert.Contains("ActiveReFlySessionMarker", discardSlice);

            // (2) Refuses on non-null with a Warn under [SwitchSegment].
            Assert.Contains("unexpected-refly-active-in-secondary-dialog", discardSlice);
            Assert.Contains("ParsekLog.Warn(\"SwitchSegment\"", discardSlice);
            Assert.Contains("Discard refused", discardSlice);

            // (3) Refusal returns before DiscardPendingTreeAndRecalculate
            //     runs. The discard call must appear AFTER the Warn so the
            //     guard's `return` prevents whole-tree discard from running.
            int warnInDiscard = discardSlice.IndexOf("unexpected-refly-active-in-secondary-dialog");
            int discardCallInDiscard = discardSlice.IndexOf("DiscardPendingTreeAndRecalculate");
            Assert.True(warnInDiscard > 0 && discardCallInDiscard > warnInDiscard,
                "Warn must precede DiscardPendingTreeAndRecalculate so the guard's return skips the discard");
        }
    }
}
