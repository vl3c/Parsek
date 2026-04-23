using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §6.8 / §11.2): live-scene
    /// verification that <see cref="RewindPointReaper.ReapOrphanedRPs"/>
    /// retains an RP while one of its slots is still CommittedProvisional,
    /// then reaps it once the last slot flips to Immutable.
    ///
    /// <para>
    /// Runs as a synthetic in-memory fixture: the test installs a fake
    /// 2-slot RP referencing two synthetic recordings (one Immutable, one
    /// CommittedProvisional), calls the reaper (0 reaped), flips the
    /// second slot's recording to Immutable, calls the reaper again
    /// (1 reaped), then restores the live scenario's lists so the
    /// player's state is untouched.
    /// </para>
    /// </summary>
    public class RPReapOnLastSlotImmutableTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "RP stays while any slot is CommittedProvisional; reaps when last slot goes Immutable")]
        public void RPReapOnLastSlotImmutable()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            var savedRps = scenario.RewindPoints;
            var savedSupersedes = scenario.RecordingSupersedes;

            string fakeTreeId = "phase11_igt_rpreap_tree_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string recImmutableId = "phase11_igt_rpreap_A_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string recProvisionalId = "phase11_igt_rpreap_B_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeBpId = "phase11_igt_rpreap_bp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRpId = "rp_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            var recImmutable = new Recording
            {
                RecordingId = recImmutableId,
                VesselName = "Phase11_IgtReapA",
                TreeId = fakeTreeId,
                MergeState = MergeState.Immutable,
            };
            var recProvisional = new Recording
            {
                RecordingId = recProvisionalId,
                VesselName = "Phase11_IgtReapB",
                TreeId = fakeTreeId,
                MergeState = MergeState.CommittedProvisional,
            };

            var tree = new RecordingTree
            {
                Id = fakeTreeId,
                TreeName = "Phase11IgtRPReap_" + fakeTreeId,
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = fakeBpId,
                        Type = BranchPointType.Undock,
                        UT = 0.0,
                        RewindPointId = fakeRpId,
                    },
                },
                Recordings = new Dictionary<string, Recording>
                {
                    [recImmutableId] = recImmutable,
                    [recProvisionalId] = recProvisional,
                },
            };

            RecordingStore.AddCommittedTreeForTesting(tree);
            RecordingStore.AddCommittedInternal(recImmutable);
            RecordingStore.AddCommittedInternal(recProvisional);

            // Swap the scenario's RP list for a synthetic one holding the
            // test's fake RP. Supersedes list is untouched but we snapshot
            // it to restore after.
            var fakeRp = new RewindPoint
            {
                RewindPointId = fakeRpId,
                BranchPointId = fakeBpId,
                UT = 0.0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = recImmutableId,
                        Controllable = true,
                    },
                    new ChildSlot
                    {
                        SlotIndex = 1,
                        OriginChildRecordingId = recProvisionalId,
                        Controllable = true,
                    },
                },
            };
            scenario.RewindPoints = new List<RewindPoint> { fakeRp };

            bool deleteHookCalled = false;
            RewindPointReaper.DeleteQuicksaveForTesting = id =>
            {
                deleteHookCalled = true;
                return true;
            };

            try
            {
                ParsekLog.Info("RewindTest",
                    $"RPReapOnLastSlotImmutable: rp={fakeRpId} " +
                    $"slotA={recImmutableId} (Immutable) " +
                    $"slotB={recProvisionalId} (CommittedProvisional)");

                // First pass: slot B is CommittedProvisional -> RP retained.
                int reaped1 = RewindPointReaper.ReapOrphanedRPs();
                InGameAssert.AreEqual(0, reaped1,
                    "Reap should retain the RP while slot B is CommittedProvisional");
                InGameAssert.AreEqual(1, scenario.RewindPoints.Count,
                    "RewindPoints list should still contain the synthetic RP");

                // Flip slot B to Immutable (simulates the re-fly session
                // completing for that slot).
                recProvisional.MergeState = MergeState.Immutable;

                // Second pass: both slots Immutable -> RP reaped.
                int reaped2 = RewindPointReaper.ReapOrphanedRPs();
                InGameAssert.AreEqual(1, reaped2,
                    "Reap should drop the RP once slot B goes Immutable");
                InGameAssert.AreEqual(0, scenario.RewindPoints.Count,
                    "RewindPoints list should be empty after second reap");
                InGameAssert.IsTrue(deleteHookCalled,
                    "Quicksave delete hook should have fired on the second reap");
                InGameAssert.IsNull(tree.BranchPoints[0].RewindPointId,
                    "BranchPoint back-ref should be cleared after reap");
            }
            finally
            {
                // Remove synthetic tree + recordings from the live
                // committed lists.
                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                    if (trees[i] == tree) trees.RemoveAt(i);
                RecordingStore.RemoveCommittedInternal(recImmutable);
                RecordingStore.RemoveCommittedInternal(recProvisional);

                scenario.RewindPoints = savedRps;
                scenario.RecordingSupersedes = savedSupersedes;

                RewindPointReaper.ResetTestOverrides();
            }
        }
    }
}
