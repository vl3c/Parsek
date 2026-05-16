using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase D coverage for segment-scoped switch/Fly auto-record:
    /// narrowing of the five #866 committed-tree-restore suppression /
    /// save sites so they do not suppress events / save state for new
    /// recording ids owned by an active <see cref="SwitchSegmentSession"/>.
    /// See <c>docs/dev/plans/segment-scoped-switch-fly-autorecord.md</c>
    /// §"Interaction With #866 Event Filtering" for the full contract.
    /// </summary>
    [Collection("Sequential")]
    public class SwitchSegmentSuppressionNarrowingTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public SwitchSegmentSuppressionNarrowingTests()
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

        private static ParsekScenario MakeScenarioWithSession(out SwitchSegmentSession session)
        {
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            session = new SwitchSegmentSession
            {
                SessionId = Guid.NewGuid(),
                IntentId = Guid.NewGuid(),
                EntryReason = SwitchSegmentEntryReason.TrackingStationFly,
                TreeId = "tree_test",
                ActiveSegmentRecordingId = "rec_seg",
                SwitchUT = 250.0,
            };
            scenario.ArmSwitchSegmentSession(session);
            return scenario;
        }

        private static string ToSessionString(Guid sessionId)
            => sessionId.ToString("D", CultureInfo.InvariantCulture);

        private static Recording MakeRecording(
            string recordingId,
            string treeId,
            string switchSegmentSessionId = null)
        {
            return new Recording
            {
                RecordingId = recordingId,
                TreeId = treeId,
                VesselName = "Test Recording",
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 200.0,
                SwitchSegmentSessionId = switchSegmentSessionId,
            };
        }

        private static RecordingTree MakeTreeWithRecordings(
            string treeId,
            params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = treeId,
                RootRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
                ActiveRecordingId = recordings.Length > 0 ? recordings[0].RecordingId : null,
            };
            foreach (var rec in recordings)
                tree.AddOrReplaceRecording(rec);
            return tree;
        }

        private static GameStateEvent MakeEvent(double ut, string recordingId)
        {
            return new GameStateEvent
            {
                ut = ut,
                eventType = GameStateEventType.TechResearched,
                key = "tech-test",
                detail = "",
                recordingId = recordingId ?? "",
            };
        }

        private static void AddCommitted(Recording rec)
        {
            RecordingStore.AddCommittedInternal(rec);
        }

        // -----------------------------------------------------------------
        // IsMarkerOwnedSwitchSegmentRecordingId — ownership predicate
        // -----------------------------------------------------------------

        // Fails if: the ownership predicate returns true when no
        // SwitchSegmentSession is armed. With no session there is no
        // "marker-owned" concept — the predicate must short-circuit false.
        [Fact]
        public void IsMarkerOwnedSwitchSegmentRecordingId_NoActiveSession_ReturnsFalse()
        {
            var scenario = new ParsekScenario();
            ParsekScenario.SetInstanceForTesting(scenario);
            // No ArmSwitchSegmentSession call — session is null.

            var rec = MakeRecording(
                "rec_marker_orphan", "tree_test",
                switchSegmentSessionId: Guid.NewGuid().ToString("D"));
            AddCommitted(rec);

            Assert.False(
                RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId("rec_marker_orphan"));
        }

        // Fails if: the predicate returns true for a recording that has
        // never been stamped with a SwitchSegmentSessionId, even when a
        // session is armed. A null/empty stamp must not be confused with
        // ownership.
        [Fact]
        public void IsMarkerOwnedSwitchSegmentRecordingId_SessionArmed_RecordingNotStamped_ReturnsFalse()
        {
            MakeScenarioWithSession(out _);

            var rec = MakeRecording(
                "rec_plain", "tree_test",
                switchSegmentSessionId: null);
            AddCommitted(rec);

            Assert.False(
                RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId("rec_plain"));
        }

        // Fails if: the predicate matches on any session id instead of the
        // active session's id. Cross-session leakage would mark recordings
        // from a prior session as marker-owned and bypass suppression for
        // ids the active session does not own.
        [Fact]
        public void IsMarkerOwnedSwitchSegmentRecordingId_SessionArmed_RecordingStampedWithOtherSession_ReturnsFalse()
        {
            MakeScenarioWithSession(out _);

            Guid otherSessionId = Guid.NewGuid();
            var rec = MakeRecording(
                "rec_other_session", "tree_test",
                switchSegmentSessionId: otherSessionId.ToString("D"));
            AddCommitted(rec);

            Assert.False(
                RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId("rec_other_session"));
        }

        // Fails if: the predicate fails to recognize a recording whose
        // stamp matches the active session id. This is the load-bearing
        // case — the whole narrowing depends on this true.
        [Fact]
        public void IsMarkerOwnedSwitchSegmentRecordingId_SessionArmed_RecordingStampedWithActiveSession_ReturnsTrue()
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);

            var rec = MakeRecording(
                "rec_segment_owned", "tree_test",
                switchSegmentSessionId: ToSessionString(session.SessionId));
            AddCommitted(rec);

            Assert.True(
                RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId("rec_segment_owned"));
        }

        // Fails if: someone reorders the cross-store lookup in
        // FindRecordingByIdAcrossStores and the pending-only stamp wins,
        // leaking marker-owned behavior onto committed history. LOW 14
        // (PR #876 review): the predicate must consult committed storage
        // first; a pending-only Recording with the same id (but a
        // different SwitchSegmentSessionId stamp) must NOT shadow the
        // committed copy's result.
        [Fact]
        public void IsMarkerOwnedSwitchSegmentRecordingId_CommittedAndPendingHaveSameId_PrefersCommittedCopyResult()
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);
            string sharedId = "rec_shared";

            // Committed copy: NOT stamped with the active session - it is
            // ordinary committed history and must not be marker-owned.
            var committedCopy = MakeRecording(
                sharedId, "tree_test",
                switchSegmentSessionId: null);
            AddCommitted(committedCopy);

            // Pending copy: stamped with the active session id. If the
            // predicate walked pending storage first, it would mis-report
            // committed history as marker-owned.
            var pendingCopy = MakeRecording(
                sharedId, "tree_test",
                switchSegmentSessionId: ToSessionString(session.SessionId));
            var pendingTree = MakeTreeWithRecordings("tree_test", pendingCopy);
            RecordingStore.StashPendingTree(pendingTree);

            // Committed-first ordering preserves committed history's
            // non-owned status.
            Assert.False(
                RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(sharedId),
                "Predicate must consult committed storage first; the pending-only " +
                "stamp must NOT win the lookup");
        }

        // -----------------------------------------------------------------
        // ShouldSuppressCommittedTreeRestoreAttemptEventPersistence
        // -----------------------------------------------------------------

        // Fails if: an event tagged with a marker-owned new recording id
        // is suppressed by the same-id committed-tree restore-attempt path
        // when an attempt is armed. Plan §"Interaction With #866 Event
        // Filtering" item 2: "new recording IDs owned by SwitchSegmentSession
        // are not suppressed merely because a committed-tree restore attempt
        // is armed."
        [Fact]
        public void ShouldSuppressEventPersistence_MarkerOwnedRecordingId_ReturnsFalse_EvenWithRestoreAttemptArmed()
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);

            // Commit a parent recording so the restore-attempt arm
            // captures something concrete, then arm the attempt.
            var parent = MakeRecording("rec_parent", "tree_attempt");
            parent.ExplicitEndUT = 200.0;
            var attemptTree = MakeTreeWithRecordings("tree_attempt", parent);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            AddCommitted(parent);

            RecordingStore.ArmCommittedTreeRestoreAttempt(
                attemptTree, "test-arm");
            Assert.True(RecordingStore.HasCommittedTreeRestoreAttemptForTesting);

            // Stamp the new segment recording with the active session id
            // and add it to committed storage so the lookup succeeds.
            var segment = MakeRecording(
                "rec_segment_owned", "tree_attempt",
                switchSegmentSessionId: ToSessionString(session.SessionId));
            AddCommitted(segment);

            var evt = MakeEvent(ut: 250.0, recordingId: "rec_segment_owned");

            Assert.False(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(evt));
            Assert.Contains(logLines, l =>
                l.Contains("[RecordingStore]")
                && l.Contains("not-suppressed")
                && l.Contains("reason=marker-owned-switch-segment")
                && l.Contains("rec_segment_owned"));
        }

        // Fails if: the post-cutoff same-id committed-tree restore-attempt
        // tail suppression is broken when no marker owns the id. This is
        // the existing #866 contract — narrowing must not relax it.
        [Fact]
        public void ShouldSuppressEventPersistence_CommittedRecordingId_StillSuppressed_DuringRestoreAttempt()
        {
            MakeScenarioWithSession(out _);

            var parent = MakeRecording("rec_tip", "tree_attempt");
            parent.ExplicitEndUT = 200.0;
            var attemptTree = MakeTreeWithRecordings("tree_attempt", parent);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            AddCommitted(parent);

            RecordingStore.ArmCommittedTreeRestoreAttempt(
                attemptTree, "test-arm");

            // Post-cutoff event on the original committed id — no marker
            // ownership, must still be suppressed.
            var afterCutoff = MakeEvent(ut: 250.0, recordingId: "rec_tip");
            Assert.True(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(afterCutoff));

            // Pre-cutoff event on the same id — historical, not suppressed.
            var beforeCutoff = MakeEvent(ut: 150.0, recordingId: "rec_tip");
            Assert.False(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(beforeCutoff));
        }

        // Fails if: a pending-only attempt id that is NOT marker-owned by
        // the active session escapes suppression. The "pending-only"
        // branch is the second arm of #866 and must remain intact for ids
        // the session does not own.
        [Fact]
        public void ShouldSuppressEventPersistence_PendingOnlyNonMarkerOwned_StillSuppressed_DuringRestoreAttempt()
        {
            MakeScenarioWithSession(out _);

            // Committed tree with the attempt parent so the attempt arms
            // with a recorded id set.
            var parent = MakeRecording("rec_parent", "tree_attempt");
            parent.ExplicitEndUT = 200.0;
            var attemptTree = MakeTreeWithRecordings("tree_attempt", parent);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            AddCommitted(parent);

            RecordingStore.ArmCommittedTreeRestoreAttempt(
                attemptTree, "test-arm");

            // Add a new pending-only id directly to the pending tree —
            // not in the attempt's recorded id set, not in committed
            // storage, and NOT stamped with the active session id.
            var pendingOnlyRec = MakeRecording(
                "rec_pending_only", "tree_attempt",
                switchSegmentSessionId: null);
            attemptTree.AddOrReplaceRecording(pendingOnlyRec);
            // Bypass via StashPendingTree so IsPendingOnlyCommittedTreeRestoreAttemptRecordingId
            // can find it in pendingTree.
            RecordingStore.StashPendingTree(attemptTree);

            var evt = MakeEvent(ut: 250.0, recordingId: "rec_pending_only");
            Assert.True(
                RecordingStore.ShouldSuppressCommittedTreeRestoreAttemptEventPersistence(evt));
        }

        // -----------------------------------------------------------------
        // ShouldDeferPendingEventMilestoneFlushForSave — partition by
        // marker ownership across pending events
        // -----------------------------------------------------------------

        // Fails if: deferral persists even when every pending event with
        // a recording id is marker-owned. The narrowing claim: marker-
        // owned events flush normally so they survive F5/save/reload.
        [Fact]
        public void ShouldDeferPendingEventMilestoneFlush_AllMarkerOwned_ReturnsFalse()
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);

            // Arm a committed-restore attempt so the un-narrowed
            // predicate would have returned true on the parent gate.
            var parent = MakeRecording("rec_parent", "tree_attempt");
            parent.ExplicitEndUT = 200.0;
            var attemptTree = MakeTreeWithRecordings("tree_attempt", parent);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            AddCommitted(parent);
            RecordingStore.ArmCommittedTreeRestoreAttempt(attemptTree, "test-arm");

            var segment = MakeRecording(
                "rec_segment_owned", "tree_attempt",
                switchSegmentSessionId: ToSessionString(session.SessionId));
            AddCommitted(segment);

            // Pending events are all on the marker-owned id.
            var evt1 = MakeEvent(ut: 250.0, recordingId: "rec_segment_owned");
            var evt2 = MakeEvent(ut: 260.0, recordingId: "rec_segment_owned");
            GameStateStore.AddEvent(ref evt1);
            GameStateStore.AddEvent(ref evt2);

            Assert.False(ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave());
            Assert.Contains(logLines, l =>
                l.Contains("[Scenario]")
                && l.Contains("not-deferred")
                && l.Contains("reason=marker-owned-switch-segment"));
        }

        // Fails if: a mix of marker-owned + committed-overlap-but-not-
        // marker-owned pending events does not still defer. Even one
        // non-marker-owned overlap event must trigger deferral (the
        // existing #866 contract still binds for it).
        [Fact]
        public void ShouldDeferPendingEventMilestoneFlush_MixedMilestones_ReturnsTrue()
        {
            MakeScenarioWithSession(out SwitchSegmentSession session);

            var parent = MakeRecording("rec_parent", "tree_attempt");
            parent.ExplicitEndUT = 200.0;
            var attemptTree = MakeTreeWithRecordings("tree_attempt", parent);
            RecordingStore.AddCommittedTreeForTesting(attemptTree);
            AddCommitted(parent);
            RecordingStore.ArmCommittedTreeRestoreAttempt(attemptTree, "test-arm");

            var segment = MakeRecording(
                "rec_segment_owned", "tree_attempt",
                switchSegmentSessionId: ToSessionString(session.SessionId));
            AddCommitted(segment);

            // One marker-owned event + one non-marker-owned event on a
            // committed-overlap id. Deferral must still fire for the
            // latter.
            var ownedEvt = MakeEvent(ut: 250.0, recordingId: "rec_segment_owned");
            var overlapEvt = MakeEvent(ut: 260.0, recordingId: "rec_parent");
            GameStateStore.AddEvent(ref ownedEvt);
            GameStateStore.AddEvent(ref overlapEvt);

            Assert.True(ParsekScenario.ShouldDeferPendingEventMilestoneFlushForSave());
        }

        // -----------------------------------------------------------------
        // SaveActiveTreeIfAny dirty sidecar skip — source-text gate.
        // The full Scenario.OnSave path is not drivable from xUnit (see
        // reference_parsek_scenario_xunit.md), so guard the narrowed
        // bypass by reading the source.
        // -----------------------------------------------------------------

        // Fails if: SaveActiveTreeIfAny no longer guards the dirty
        // sidecar skip with a marker-owned bypass. Plan item 7: marker-
        // owned new segment sidecars must be durable enough for
        // F5/save/reload, while original committed sidecars stay
        // protected before Merge.
        [Fact]
        public void SaveActiveTreeIfAny_MarkerOwnedRecording_BypassesDirtySidecarSkip()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string scenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(scenarioPath),
                $"ParsekScenario.cs not found at {scenarioPath}");

            string source = File.ReadAllText(scenarioPath);

            // The dirty sidecar skip must AND on IsMarkerOwnedSwitchSegmentRecordingId
            // so marker-owned ids do NOT skip the sidecar save.
            Assert.Contains(
                "RecordingStore.IsCommittedTreeRestoreAttemptRecordingId(rec.RecordingId)"
                + "\r\n                        && !RecordingStore.IsMarkerOwnedSwitchSegmentRecordingId(rec.RecordingId)",
                source.Replace("\r\n                        &&\r\n",
                               "\r\n                        && "));

            // And the bypass must log Verbose with the marker-owned
            // reason so playtest logs reveal the bypass.
            Assert.Contains(
                "reason=marker-owned-switch-segment",
                source);
        }

        // -----------------------------------------------------------------
        // Clone-parent sidecar audit (Phase D item 8).
        //
        // The audit question: does any save path currently write the
        // clone-parent's mutated `ChildBranchPointId` (set by
        // SwitchSegmentBuilder.CreateSwitchContinuationSegment on the
        // clone copy of the parent) back into the committed parent's
        // sidecar?
        //
        // Answer (verified by Phase D analysis):
        //   - The clone is a DeepClone of the committed tree
        //     (ParsekFlight.TryTakeCommittedTreeForSpawnedVesselRestore
        //     -> RecordingTree.DeepClone). The clone-parent Recording
        //     object and the committed-parent Recording object are
        //     distinct instances.
        //   - SaveActiveTreeIfAny writes the active (clone) tree under
        //     a separate RECORDING_TREE node tagged isActive=True. The
        //     committed tree is written under its own RECORDING_TREE
        //     node by SaveTreeRecordings, loaded from the committed-tree
        //     storage (RecordingStore.CommittedTrees), not from the
        //     clone.
        //   - Sidecars (.prec / *_vessel.craft / *_ghost.craft / .pcrf)
        //     contain trajectory + craft data only. ChildBranchPointId
        //     is per-recording tree metadata serialized by
        //     RecordingTreeRecordCodec.WriteRecord into the RECORDING
        //     ConfigNode — not into the sidecar files. Sidecars cannot
        //     carry the field at all.
        //   - SaveTreeRecordings (lines ~872-907 of ParsekScenario.cs)
        //     loops over committedTrees and writes each one's own
        //     Recording objects. There is no code path that copies
        //     ChildBranchPointId from a clone Recording back onto a
        //     committed Recording.
        //
        // No-op finding. This test pins the audit by checking the two
        // load-bearing anchors stay in place: DeepClone is used for the
        // clone, and SaveTreeRecordings reads from CommittedTrees.
        // -----------------------------------------------------------------

        // Fails if: the clone-parent isolation invariants regress —
        // either ParsekFlight stops DeepClone'ing the committed tree, or
        // SaveTreeRecordings starts reading from anything other than
        // committedTrees. Either regression would risk leaking the
        // clone parent's mutated ChildBranchPointId into committed
        // storage.
        [Fact]
        public void Audit_NoCodepath_WritesCloneParentChildBranchPointIdToCommittedSidecar()
        {
            string projectRoot = Path.GetFullPath(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "..", "..", "..", "..", ".."));
            string parsekFlightPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekFlight.cs");
            string parsekScenarioPath = Path.Combine(projectRoot,
                "Source", "Parsek", "ParsekScenario.cs");
            Assert.True(File.Exists(parsekFlightPath),
                $"ParsekFlight.cs not found at {parsekFlightPath}");
            Assert.True(File.Exists(parsekScenarioPath),
                $"ParsekScenario.cs not found at {parsekScenarioPath}");

            string flightSource = File.ReadAllText(parsekFlightPath);
            string scenarioSource = File.ReadAllText(parsekScenarioPath);

            // 1) The committed-tree restore path must still DeepClone
            //    the committed tree. Without DeepClone the clone parent
            //    IS the committed parent, and any ChildBranchPointId
            //    mutation would directly corrupt committed history.
            Assert.Contains(
                "RecordingTree liveTree = RecordingTree.DeepClone(committedTree);",
                flightSource);

            // 2) SaveTreeRecordings (committed-tree serializer) must
            //    still read from RecordingStore.CommittedTrees. If a
            //    future refactor starts iterating over the active
            //    (clone) tree here, the clone parent's mutated
            //    ChildBranchPointId would be written into the committed
            //    tree's ConfigNode and the audit's no-op finding would
            //    no longer hold.
            Assert.Contains(
                "internal static void SaveTreeRecordings(ConfigNode node)",
                scenarioSource);
            Assert.Contains(
                "var committedTrees = RecordingStore.CommittedTrees;",
                scenarioSource);

            // 3) SaveActiveTreeIfAny writes the clone tree under
            //    isActive=True (a distinct RECORDING_TREE node) — NOT
            //    via the committed-tree loop above. The two writers
            //    target different ConfigNode subtrees in the .sfs.
            Assert.Contains(
                "treeNode.AddValue(\"isActive\", \"True\");",
                scenarioSource);
        }
    }
}
