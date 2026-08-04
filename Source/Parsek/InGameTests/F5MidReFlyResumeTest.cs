using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 13 of Rewind-to-Staging (design §6.9 / §7.12):
    /// live-scene verification that <see cref="LoadTimeSweep.Run"/>
    /// preserves a valid re-fly session across an F5 quicksave +
    /// quickload cycle.
    ///
    /// <para>
    /// Running an actual F5+F9 in-game is destructive (it rebuilds
    /// every Parsek subsystem and resets the scene), so the test
    /// simulates the load-path invariant directly: given a live
    /// marker + its NotCommitted provisional + its session-provisional
    /// RP, invoking <see cref="LoadTimeSweep.Run"/> must leave all
    /// three intact, emit a <c>[LoadSweep]</c> summary line with
    /// <c>Marker valid=True</c>, and emit the
    /// <c>[ReFlySession] Marker valid</c> log line.
    /// </para>
    ///
    /// <para>
    /// The test synthesizes its own marker + provisional + RP so it
    /// works whether the player has a live session or not; any real
    /// session state is preserved and restored in the finally block.
    /// </para>
    /// </summary>
    public class F5MidReFlyResumeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "F5 mid-re-fly + simulated quickload -> LoadTimeSweep preserves marker + provisional + session-prov RP")]
        public void F5MidReFlyResume()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Preserve live state so the synthetic fixture doesn't
            // clobber an in-flight session.
            var priorMarker = scenario.ActiveReFlySessionMarker;
            var priorJournal = scenario.ActiveMergeJournal;
            var priorRps = scenario.RewindPoints;

            // CANNOT SELF-SET-UP BESIDE A REAL SESSION, so skip rather than assert
            // against an assumed context (the house rule in .claude/CLAUDE.md).
            //
            // This test must install its OWN marker to drive LoadTimeSweep, and the
            // sweep is a GLOBAL, DESTRUCTIVE operation: with a foreign marker
            // installed, the real session's NotCommitted provisional is no longer
            // owned by the active session, so the sweep correctly discards it as a
            // zombie. Restoring the marker afterwards cannot un-discard the
            // recording, and every later marker-dependent test in the same batch
            // then finds a marker whose ActiveReFlyRecordingId resolves to nothing -
            // an InGameAssert.IsNotNull FAIL, not a skip.
            //
            // FOUND 2026-08-04 by the R7b spec (a real InvokeRewind, then this
            // category run inside the resulting session): the sweep logged
            // `Zombie discarded rec=... supersedeTarget=cl-pod-a`, which is product
            // behaving exactly as designed on state this test had manufactured.
            // Skipping keeps the live session intact for the nine members that need
            // it; the test still executes on any save with no session in progress,
            // which is where its invariant is actually meaningful.
            if (priorMarker != null)
            {
                InGameAssert.Skip(
                    "A live Re-Fly session is already active (sess=" +
                    (priorMarker.SessionId ?? "<no-id>") +
                    "); this test installs its own marker and LoadTimeSweep would " +
                    "destructively discard the real session's provisional. Run it " +
                    "on a save with no re-fly in progress.");
                return;
            }

            string sessId = "phase13_igt_f5mid_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeId = "phase13_igt_tree_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string activeId = "phase13_igt_active_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string originId = "phase13_igt_origin_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string bpId = "phase13_igt_bp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string rpId = "rp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var activeRec = new Recording
            {
                RecordingId = activeId,
                VesselName = "Phase13_IgtF5MidActive",
                TreeId = treeId,
                MergeState = MergeState.NotCommitted,
                CreatingSessionId = sessId,
                SupersedeTargetId = originId,
            };
            var originRec = new Recording
            {
                RecordingId = originId,
                VesselName = "Phase13_IgtF5MidOrigin",
                TreeId = treeId,
                MergeState = MergeState.CommittedProvisional,
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Phase13IgtF5Mid_" + treeId,
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = bpId,
                        Type = BranchPointType.Undock,
                        UT = 0.0,
                        RewindPointId = rpId,
                    },
                },
                Recordings = new Dictionary<string, Recording>
                {
                    [activeId] = activeRec,
                    [originId] = originRec,
                },
            };

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(activeRec);
            RecordingStore.AddCommittedInternal(originRec);

            var fakeRp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 0.0,
                SessionProvisional = true,
                CreatingSessionId = sessId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = originId,
                        Controllable = true,
                    },
                },
            };

            var fakeMarker = new ReFlySessionMarker
            {
                SessionId = sessId,
                TreeId = treeId,
                ActiveReFlyRecordingId = activeId,
                OriginChildRecordingId = originId,
                RewindPointId = rpId,
                // InvokedUT: capture the current UT so the validator's
                // future-UT check passes.
                InvokedUT = Planetarium.GetUniversalTime(),
                InvokedRealTime = DateTime.UtcNow.ToString("o"),
            };

            scenario.RewindPoints = new List<RewindPoint> { fakeRp };
            scenario.ActiveReFlySessionMarker = fakeMarker;
            scenario.ActiveMergeJournal = null; // no interrupted merge

            try
            {
                ParsekLog.Info("RewindTest",
                    $"F5MidReFlyResume: synthesized sess={sessId} tree={treeId} " +
                    $"active={activeId} origin={originId} rp={rpId} — invoking LoadTimeSweep");

                // Capture pre-sweep counts for invariants.
                //
                // SETTLED entries only (Immutable / CommittedProvisional), NOT the
                // whole list, and that is the assertion below actually saying what
                // its comment always claimed. The total count is a PROXY that only
                // works while this test's synthetic provisional is the sole
                // NotCommitted entry in the store, which is true when the test runs
                // on a clean save and false the moment anything else has a live
                // Re-Fly session. In that case the sweep correctly discards the
                // OTHER session's now-orphaned provisional as a zombie - product
                // behaving exactly as designed - and a total-count comparison reads
                // that correct discard as a failure.
                //
                // FOUND 2026-08-04 by the R7b spec, which drives a real InvokeRewind
                // and then runs this category inside the resulting live session:
                // `Zombie discarded rec=... supersedeTarget=cl-pod-a` followed by
                // this assertion failing, while every other invariant in this test
                // passed. Counting only settled entries is immune to that and is
                // strictly more precise - a dropped Immutable entry still reds, and
                // the survival of THIS test's own NotCommitted provisional is
                // asserted separately below.
                int preRecCount = CountSettledRecordings();
                int preRpCount = scenario.RewindPoints.Count;

                // Simulated F5+F9: the save+reload would run Phase 10
                // finisher (no-op without a journal), then LoadTimeSweep,
                // then the Phase 11 reaper. We invoke the sweep directly.
                LoadTimeSweep.Run();

                // Marker survived.
                InGameAssert.IsNotNull(scenario.ActiveReFlySessionMarker,
                    "Marker should survive LoadTimeSweep (all 6 fields are valid)");
                InGameAssert.AreEqual(sessId, scenario.ActiveReFlySessionMarker.SessionId,
                    "Marker's SessionId should be unchanged after sweep");

                // Provisional recording survived.
                Recording postActive = null;
                var committed = RecordingStore.CommittedRecordings;
                for (int i = 0; i < committed.Count; i++)
                {
                    var r = committed[i];
                    if (r == null) continue;
                    if (r.RecordingId == activeId) { postActive = r; break; }
                }
                InGameAssert.IsNotNull(postActive,
                    "NotCommitted provisional must survive the sweep when the marker is valid");
                InGameAssert.AreEqual(MergeState.NotCommitted, postActive.MergeState,
                    "Provisional MergeState must still be NotCommitted post-sweep");

                // Session-provisional RP survived.
                bool rpFound = false;
                for (int i = 0; i < scenario.RewindPoints.Count; i++)
                {
                    if (scenario.RewindPoints[i]?.RewindPointId == rpId) { rpFound = true; break; }
                }
                InGameAssert.IsTrue(rpFound,
                    "Session-provisional RP must survive the sweep when the marker references it");

                // Committed-recording count: the sweep must not have
                // dropped any Immutable / CommittedProvisional entries.
                InGameAssert.AreEqual(preRecCount, CountSettledRecordings(),
                    "Sweep must not drop any settled (Immutable / CommittedProvisional) recording");
                InGameAssert.AreEqual(preRpCount, scenario.RewindPoints.Count,
                    "Sweep must not change the RP count when every session-prov RP is spared");

                ParsekLog.Info("RewindTest",
                    $"F5MidReFlyResume: sweep preserved marker + provisional + rp as expected");
            }
            finally
            {
                // Remove synthetic tree + recordings from the live committed lists.
                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                    if (trees[i] == tree) trees.RemoveAt(i);
                RecordingStore.RemoveCommittedInternal(activeRec);
                RecordingStore.RemoveCommittedInternal(originRec);

                // Restore the pre-test scenario fields.
                scenario.ActiveReFlySessionMarker = priorMarker;
                scenario.ActiveMergeJournal = priorJournal;
                scenario.RewindPoints = priorRps;
            }
        }

        /// <summary>
        /// Committed recordings in a SETTLED merge state (anything other than
        /// <see cref="MergeState.NotCommitted"/>).
        ///
        /// <para>
        /// The sweep's whole job is to discard NotCommitted provisionals whose
        /// session is gone while sparing the active session's, so the count of
        /// NotCommitted entries is EXPECTED to move and is not an invariant. What
        /// must never move is the settled population - that is what this counts.
        /// </para>
        /// </summary>
        private static int CountSettledRecordings()
        {
            int settled = 0;
            var committed = RecordingStore.CommittedRecordings;
            for (int i = 0; i < committed.Count; i++)
            {
                var r = committed[i];
                if (r == null) continue;
                if (r.MergeState != MergeState.NotCommitted) settled++;
            }
            return settled;
        }
    }
}
