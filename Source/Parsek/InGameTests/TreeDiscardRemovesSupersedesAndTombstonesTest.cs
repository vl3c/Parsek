using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Phase 11 of Rewind-to-Staging (design §6.10 / §3.5 invariant 7):
    /// live-scene verification that <see cref="TreeDiscardPurge.PurgeTree"/>
    /// drops every piece of cross-recording state tied to a discarded tree
    /// (RPs, supersede relations, ledger tombstones, marker + journal when
    /// scoped).
    ///
    /// <para>
    /// Runs as a synthetic in-memory fixture: the test installs a fake
    /// tree + supersede + tombstone on the live scenario, calls
    /// <see cref="TreeDiscardPurge.PurgeTree"/>, asserts the purge was
    /// total, then restores the original scenario state so the
    /// player's save is untouched. Does NOT mutate any on-disk files.
    /// </para>
    /// </summary>
    public class TreeDiscardRemovesSupersedesAndTombstonesTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.FLIGHT,
            Description = "Tree discard purges RPs + supersedes + tombstones tied to the tree")]
        public void TreeDiscardRemovesSupersedesAndTombstones()
        {
            var scenario = ParsekScenario.Instance;
            InGameAssert.IsNotNull(scenario, "ParsekScenario.Instance is null");

            // Snapshot the live scenario state so we can restore it after
            // the synthetic fixture runs. We never touch the live trees /
            // committed recordings; the synthetic tree is injected on top.
            var savedRps = scenario.RewindPoints;
            var savedSupersedes = scenario.RecordingSupersedes;
            var savedTombstones = scenario.LedgerTombstones;

            string fakeTreeId = "phase11_igt_tree_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRecId = "phase11_igt_rec_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeBpId = "phase11_igt_bp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRpId = "rp_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeRelId = "phase11_igt_rel_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string fakeTombId = "phase11_igt_tomb_" + Guid.NewGuid().ToString("N").Substring(0, 8);

            // Synthetic tree with 1 branch point + 1 recording. Inserted
            // directly into CommittedTrees so TreeDiscardPurge.FindTree
            // finds it.
            var tree = new RecordingTree
            {
                Id = fakeTreeId,
                TreeName = "Phase11Igt_" + fakeTreeId,
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
                    [fakeRecId] = new Recording
                    {
                        RecordingId = fakeRecId,
                        VesselName = "Phase11_IgtTest",
                        TreeId = fakeTreeId,
                        MergeState = MergeState.Immutable,
                    },
                },
            };
            RecordingStore.AddCommittedTreeForTesting(tree);

            // Swap the scenario's in-memory lists for synthetic ones so
            // the purge has isolated targets to touch.
            scenario.RewindPoints = new List<RewindPoint>
            {
                new RewindPoint
                {
                    RewindPointId = fakeRpId,
                    BranchPointId = fakeBpId,
                    UT = 0.0,
                    SessionProvisional = false,
                    ChildSlots = new List<ChildSlot>(),
                },
            };
            scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>
            {
                new RecordingSupersedeRelation
                {
                    RelationId = fakeRelId,
                    OldRecordingId = fakeRecId,
                    NewRecordingId = "phase11_igt_other",
                    UT = 0.0,
                },
            };
            scenario.LedgerTombstones = new List<LedgerTombstone>
            {
                new LedgerTombstone
                {
                    TombstoneId = fakeTombId,
                    ActionId = "phase11_igt_noaction",
                    RetiringRecordingId = fakeRecId,
                    UT = 0.0,
                },
            };

            // File-delete hook avoids touching the real save folder.
            bool deleteHookCalled = false;
            TreeDiscardPurge.DeleteQuicksaveForTesting = id =>
            {
                deleteHookCalled = true;
                return true;
            };

            try
            {
                ParsekLog.Info("RewindTest",
                    $"TreeDiscardRemovesSupersedesAndTombstones: " +
                    $"synthetic tree={fakeTreeId} rp={fakeRpId} rel={fakeRelId} tomb={fakeTombId}");

                TreeDiscardPurge.PurgeTree(fakeTreeId);

                InGameAssert.AreEqual(0, scenario.RewindPoints.Count,
                    "RewindPoints list should be empty after PurgeTree");
                InGameAssert.AreEqual(0, scenario.RecordingSupersedes.Count,
                    "RecordingSupersedes list should be empty after PurgeTree");
                InGameAssert.AreEqual(0, scenario.LedgerTombstones.Count,
                    "LedgerTombstones list should be empty after PurgeTree " +
                    "(fallback via RetiringRecordingId when action absent from ledger)");
                InGameAssert.IsTrue(deleteHookCalled,
                    "Quicksave delete hook should have been invoked for the synthetic RP");

                // BranchPoint back-ref on the synthetic tree cleared.
                InGameAssert.IsNull(tree.BranchPoints[0].RewindPointId,
                    "BranchPoint back-ref should be cleared after PurgeTree");
            }
            finally
            {
                // Remove synthetic tree from the live committed list so we
                // leave the player's scenario in its original shape.
                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                {
                    if (trees[i] == tree) trees.RemoveAt(i);
                }

                // Restore the original scenario lists verbatim.
                scenario.RewindPoints = savedRps;
                scenario.RecordingSupersedes = savedSupersedes;
                scenario.LedgerTombstones = savedTombstones;

                TreeDiscardPurge.ResetTestOverrides();
            }
        }
    }
}
