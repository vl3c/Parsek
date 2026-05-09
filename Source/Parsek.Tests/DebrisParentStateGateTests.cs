using Parsek;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Truth-table tests for <see cref="DebrisParentStateGate.IsParentRecordingClosedOrSuperseded"/>
    /// — PR 3b review follow-up. The predicate replaced the original v8-of-the-plan
    /// `parentRec.ExplicitEndUT &lt; currentUT` check, which incorrectly treated
    /// the natural per-sample lag of an active background recording's ExplicitEndUT
    /// as "parent finalized" and would have ended every v12 debris on the next TTL
    /// tick. Each test exercises one truth-table case so a future regression to the
    /// lagging-UT signal would fail loudly.
    /// </summary>
    public class DebrisParentStateGateTests
    {
        [Fact]
        public void Returns_True_When_ParentRecording_Is_Null()
        {
            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(
                null,
                new RecordingTree { Id = "tree" }));
        }

        [Fact]
        public void Returns_True_When_ClosedBackgroundParent_Not_In_ActivePool()
        {
            // After a background split, CloseParentRecording sets
            // ChildBranchPointId on the closed parent AND the surrounding
            // flow swaps BackgroundMap[pid] to the continuation's id.
            // The closed parent's RecordingId no longer matches the
            // BackgroundMap entry (continuation owns it), and the closed
            // parent isn't tree.ActiveRecordingId either — both
            // active-pool checks miss, so the predicate returns true.
            //
            // The model "ChildBranchPointId set means closed" was fixed
            // in the fourth review pass: that signal is also set on the
            // ACTIVE focused recording during a focused breakup
            // (ProcessBreakupEvent at ParsekFlight.cs:5427), where the
            // recording keeps growing. So ChildBranchPointId-set is
            // NOT sufficient evidence of closure — what closes a
            // background parent is the BackgroundMap swap to the
            // continuation, not the ChildBranchPointId stamp.
            var tree = new RecordingTree { Id = "tree" };
            var closedParent = new Recording
            {
                RecordingId = "closed-parent",
                VesselPersistentId = 100u,
                ChildBranchPointId = "bp-1",
            };
            tree.AddOrReplaceRecording(closedParent);
            // Continuation owns the BackgroundMap entry now.
            tree.BackgroundMap[100u] = "continuation-rec";

            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(closedParent, tree));
        }

        [Fact]
        public void Returns_True_When_BackgroundMap_Has_Different_RecordingId_For_Vessel()
        {
            // Parent was superseded — BackgroundMap now points at a successor
            // (e.g. a continuation recording, a Re-Fly fork). The parentRec
            // itself is no longer accepting samples.
            var tree = new RecordingTree { Id = "tree" };
            var parentRec = new Recording
            {
                RecordingId = "old-parent",
                VesselPersistentId = 100u,
            };
            tree.AddOrReplaceRecording(parentRec);
            tree.BackgroundMap[100u] = "successor-recording";

            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree));
        }

        [Fact]
        public void Returns_True_When_BackgroundMap_Has_No_Entry_For_Vessel()
        {
            // Parent vessel was destroyed (OnVesselRemovedFromBackground
            // removed the entry).
            var tree = new RecordingTree { Id = "tree" };
            var parentRec = new Recording
            {
                RecordingId = "vanished-parent",
                VesselPersistentId = 100u,
            };
            tree.AddOrReplaceRecording(parentRec);
            // BackgroundMap intentionally has no entry for vesselPid=100u.

            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree));
        }

        [Fact]
        public void Returns_True_When_VesselPersistentId_Is_Zero_AND_Not_TreeActiveRecording()
        {
            // Defensive: a Recording with VesselPersistentId == 0 that is
            // NOT the tree's active recording cannot be matched in
            // BackgroundMap (the key would be ambiguous) and has no
            // active-focused fallback, so treat it as closed. This is
            // distinct from the focused-corner-case test below
            // (`Returns_False_When_Parent_Is_TreeActiveRecording_With_VesselPersistentId_Zero`)
            // where ActiveRecordingId DOES match — in that case the
            // active-focused branch fires and the predicate returns false
            // even with vessel id 0.
            var tree = new RecordingTree { Id = "tree" };
            var parentRec = new Recording
            {
                RecordingId = "no-vessel-id",
                VesselPersistentId = 0u,
            };
            tree.AddOrReplaceRecording(parentRec);
            // ActiveRecordingId intentionally NOT set to parentRec.RecordingId.

            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree));
        }

        [Fact]
        public void Returns_True_When_Tree_Is_Null()
        {
            var parentRec = new Recording
            {
                RecordingId = "orphan-parent",
                VesselPersistentId = 100u,
            };
            Assert.True(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, null));
        }

        [Fact]
        public void Returns_False_When_Parent_Is_Active_Recording_With_Lagging_ExplicitEndUT()
        {
            // The bug scenario the original v8 check would have triggered on:
            // an active background recording whose ExplicitEndUT lags the
            // current frame by the sample interval. The new predicate must
            // NOT report this as "finalized" — the recorder is still writing
            // samples to it.
            var tree = new RecordingTree { Id = "tree" };
            var parentRec = new Recording
            {
                RecordingId = "active-parent",
                VesselPersistentId = 100u,
                ExplicitEndUT = 99.5,           // last authored sample (lagging)
                ChildBranchPointId = null,      // not closed at a split
            };
            tree.AddOrReplaceRecording(parentRec);
            tree.BackgroundMap[100u] = "active-parent";

            // Whatever the current UT is (even when much greater than
            // ExplicitEndUT), the predicate must report "not closed."
            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(parentRec, tree));
        }

        [Fact]
        public void Returns_False_When_Parent_Is_Active_Continuation_After_Split()
        {
            // After a split, the continuation recording is the new active
            // entry in BackgroundMap. Its ChildBranchPointId is null (it
            // wasn't closed; it was just opened). It IS, however,
            // `ParentBranchPointId`-tagged. Make sure we don't conflate the
            // two and accidentally fire the gate on a continuation.
            var tree = new RecordingTree { Id = "tree" };
            var continuationRec = new Recording
            {
                RecordingId = "continuation",
                VesselPersistentId = 100u,
                ParentBranchPointId = "bp-1",   // it was BORN at a split…
                ChildBranchPointId = null,      // …but hasn't been closed yet
                ExplicitStartUT = 100.0,
            };
            tree.AddOrReplaceRecording(continuationRec);
            tree.BackgroundMap[100u] = "continuation";

            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(continuationRec, tree));
        }

        // ===== PR 3b review follow-up #2: focused-parent regression =====
        // The active focused-vessel recording is intentionally NOT in
        // BackgroundMap (which only tracks background-recorded vessels).
        // Focused-vessel debris (created via
        // ParsekFlight.CreateBreakupChildRecording) anchors to the active
        // recording, so a predicate that only consults BackgroundMap would
        // expire every focused-vessel debris on the first TTL tick. The
        // tree.ActiveRecordingId match must be honored as "active and
        // accepting samples."

        [Fact]
        public void Returns_False_When_Parent_Is_TreeActiveRecording_Even_If_Not_In_BackgroundMap()
        {
            // The reviewer's bug: an active focused recording absent from
            // BackgroundMap (which is the design — BackgroundMap is
            // background-recorded vessels only) must NOT be treated as
            // closed. Focused-vessel debris's parent is by construction
            // the tree's ActiveRecordingId.
            var tree = new RecordingTree { Id = "tree" };
            var activeRec = new Recording
            {
                RecordingId = "active-focused",
                VesselPersistentId = 4242u,
                ChildBranchPointId = null,
            };
            tree.AddOrReplaceRecording(activeRec);
            tree.ActiveRecordingId = activeRec.RecordingId;
            // BackgroundMap intentionally has NO entry for this vessel pid —
            // the focused vessel is not background-recorded.

            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(activeRec, tree));
        }

        [Fact]
        public void Returns_False_When_Parent_Is_TreeActiveRecording_With_VesselPersistentId_Zero()
        {
            // Defensive corner: an active recording may have
            // VesselPersistentId == 0 transiently (before the recorder
            // assigns one). The active-recording-match branch fires before
            // the vessel-id check, so this still resolves as "active."
            var tree = new RecordingTree { Id = "tree" };
            var activeRec = new Recording
            {
                RecordingId = "active-no-pid",
                VesselPersistentId = 0u,
                ChildBranchPointId = null,
            };
            tree.AddOrReplaceRecording(activeRec);
            tree.ActiveRecordingId = activeRec.RecordingId;

            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(activeRec, tree));
        }

        [Fact]
        public void Returns_False_When_FocusedParent_Has_ChildBranchPointId_From_Breakup()
        {
            // Fourth review pass — the load-bearing focused-breakup-continuous
            // regression. ParsekFlight.ProcessBreakupEvent (line 5427) sets
            //   activeRec.ChildBranchPointId = breakupBp.Id
            // on the active focused recording at every focused-vessel breakup,
            // BUT the comment at line 5430 explicitly says
            //   "The recording continues past breakup (breakup-continuous design)".
            // So the active focused recording can have ChildBranchPointId set
            // mid-flight while still receiving samples. A predicate that
            // short-circuits on ChildBranchPointId-set would have ended every
            // focused-vessel debris on the first TTL tick after the very
            // breakup that spawned it.
            //
            // The active-recording match must win over any
            // ChildBranchPointId signal.
            var tree = new RecordingTree { Id = "tree" };
            var focusedActive = new Recording
            {
                RecordingId = "focused-active",
                VesselPersistentId = 4242u,
                ChildBranchPointId = "breakup-bp-1",  // set by ProcessBreakupEvent
            };
            tree.AddOrReplaceRecording(focusedActive);
            tree.ActiveRecordingId = focusedActive.RecordingId;
            // BackgroundMap intentionally has NO entry for this vessel pid —
            // the focused vessel is not background-recorded.

            Assert.False(DebrisParentStateGate.IsParentRecordingClosedOrSuperseded(
                focusedActive, tree));
        }
    }
}
