using System;
using System.Collections.Generic;
using UnityEngine;

namespace Parsek.InGameTests
{
    /// <summary>
    /// Synthetic live-runtime coverage for stable-leaf Unfinished Flights. This
    /// installs an in-memory RP fixture, exercises the production group/route
    /// and Seal/reaper paths, then restores the live scenario state.
    /// </summary>
    public class StableLeafUnfinishedFlightsRuntimeTest
    {
        [InGameTest(Category = "Rewind", Scene = GameScenes.SPACECENTER,
            Description = "Stable-leaf Unfinished Flights group, Seal, and last-slot reap work on a synthetic RP fixture")]
        public void StableLeafGroupSealAndLastSlotReap()
        {
            var scenario = ParsekScenario.Instance;
            if (scenario == null)
            {
                InGameAssert.Skip("No ParsekScenario instance");
                return;
            }

            if (scenario.ActiveReFlySessionMarker != null)
            {
                InGameAssert.Skip("Active re-fly session is present");
                return;
            }

            if (scenario.ActiveMergeJournal != null)
            {
                InGameAssert.Skip("Active merge journal is present");
                return;
            }

            var savedRps = scenario.RewindPoints;
            var savedSupersedes = scenario.RecordingSupersedes;
            var savedTombstones = scenario.LedgerTombstones;

            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            string treeId = "stableleaf_igt_tree_" + suffix;
            string bpId = "stableleaf_igt_bp_" + suffix;
            string rpId = "stableleaf_igt_rp_" + suffix;

            var focus = NewRecording(treeId, bpId, "focus_" + suffix,
                "StableLeaf IGT Focus", MergeState.Immutable, TerminalState.Orbiting);
            var orbiting = NewRecording(treeId, bpId, "orbit_" + suffix,
                "StableLeaf IGT Orbiting Probe", MergeState.CommittedProvisional, TerminalState.Orbiting);
            var subOrbital = NewRecording(treeId, bpId, "suborb_" + suffix,
                "StableLeaf IGT SubOrbital Probe", MergeState.CommittedProvisional, TerminalState.SubOrbital);
            var eva = NewRecording(treeId, bpId, "eva_" + suffix,
                "StableLeaf IGT EVA Kerbal", MergeState.CommittedProvisional, TerminalState.Landed);
            eva.EvaCrewName = "Valentina Kerman";
            var debris = NewRecording(treeId, bpId, "debris_" + suffix,
                "StableLeaf IGT Debris", MergeState.Immutable, TerminalState.Orbiting);
            debris.IsDebris = true;
            var landed = NewRecording(treeId, bpId, "landed_" + suffix,
                "StableLeaf IGT Landed Probe", MergeState.Immutable, TerminalState.Landed);

            var branchPoint = new BranchPoint
            {
                Id = bpId,
                Type = BranchPointType.Undock,
                UT = 100.0,
                RewindPointId = rpId,
                ParentRecordingIds = new List<string> { "stableleaf_igt_parent_" + suffix },
                ChildRecordingIds = new List<string>
                {
                    focus.RecordingId,
                    orbiting.RecordingId,
                    subOrbital.RecordingId,
                    eva.RecordingId,
                    debris.RecordingId,
                    landed.RecordingId,
                },
            };

            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "StableLeafIGT_" + suffix,
                BranchPoints = new List<BranchPoint> { branchPoint },
                Recordings = new Dictionary<string, Recording>
                {
                    [focus.RecordingId] = focus,
                    [orbiting.RecordingId] = orbiting,
                    [subOrbital.RecordingId] = subOrbital,
                    [eva.RecordingId] = eva,
                    [debris.RecordingId] = debris,
                    [landed.RecordingId] = landed,
                },
            };

            var rp = new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 100.0,
                SessionProvisional = false,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    NewSlot(0, focus.RecordingId, controllable: true),
                    NewSlot(1, orbiting.RecordingId, controllable: true),
                    NewSlot(2, subOrbital.RecordingId, controllable: true),
                    NewSlot(3, eva.RecordingId, controllable: true),
                    NewSlot(4, debris.RecordingId, controllable: false),
                    NewSlot(5, landed.RecordingId, controllable: true),
                },
            };

            Recording[] recordings = { focus, orbiting, subOrbital, eva, debris, landed };
            bool deleteHookCalled = false;

            try
            {
                RecordingStore.AddCommittedTreeForTesting(tree);
                for (int i = 0; i < recordings.Length; i++)
                    RecordingStore.AddCommittedInternal(recordings[i]);

                scenario.RewindPoints = new List<RewindPoint> { rp };
                scenario.RecordingSupersedes = new List<RecordingSupersedeRelation>();
                scenario.LedgerTombstones = new List<LedgerTombstone>();
                scenario.BumpSupersedeStateVersion();
                scenario.BumpTombstoneStateVersion();
                EffectiveState.ResetCachesForTesting();

                var members = UnfinishedFlightsGroup.ComputeMembers();
                AssertContainsMember(members, orbiting, "Orbiting non-focus probe should be unfinished");
                AssertContainsMember(members, subOrbital, "SubOrbital non-focus probe should be unfinished");
                AssertContainsMember(members, eva, "Stranded EVA should be unfinished");
                AssertDoesNotContainMember(members, focus, "Focused Orbiting slot should stay forward-only");
                AssertDoesNotContainMember(members, debris, "Debris/non-controllable slot should stay forward-only");
                AssertDoesNotContainMember(members, landed, "Stable Landed vessel should stay forward-only");
                InGameAssert.AreEqual(3, CountSyntheticMembers(members, suffix),
                    "Synthetic fixture should contribute exactly three Unfinished Flight rows");

                RewindPoint resolvedRp;
                int resolvedSlot;
                InGameAssert.IsTrue(
                    RecordingsTableUI.TryResolveUnfinishedFlightRewindPoint(orbiting, out resolvedRp, out resolvedSlot),
                    "Orbiting member should resolve to a Rewind Point route");
                InGameAssert.AreEqual(rp, resolvedRp,
                    "Route should resolve the synthetic Rewind Point");
                InGameAssert.AreEqual(1, resolvedSlot,
                    "Orbiting member should resolve slot 1");

                InGameAssert.IsFalse(RewindPointReaper.IsReapEligible(rp, scenario.RecordingSupersedes),
                    "RP should not reap while stable-leaf CP slots remain unsealed");

                UnfinishedFlightSealHandler.UtcNowForTesting =
                    () => new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                string reason;
                InGameAssert.IsTrue(UnfinishedFlightSealHandler.TrySeal(orbiting, out reason),
                    "Sealing the orbiting member should succeed");
                InGameAssert.AreEqual(MergeState.CommittedProvisional, orbiting.MergeState,
                    "Seal must not mutate MergeState");
                InGameAssert.IsTrue(rp.ChildSlots[1].Sealed,
                    "Orbiting slot should be marked sealed");
                InGameAssert.AreEqual("2000-01-01T00:00:00.0000000Z", rp.ChildSlots[1].SealedRealTime,
                    "Seal timestamp should come from the test clock");
                AssertDoesNotContainMember(UnfinishedFlightsGroup.ComputeMembers(), orbiting,
                    "Sealed orbiting slot should drop from Unfinished Flights");
                InGameAssert.AreEqual(1, scenario.RewindPoints.Count,
                    "RP should remain while other CP slots are still open");

                InGameAssert.IsTrue(UnfinishedFlightSealHandler.TrySeal(subOrbital, out reason),
                    "Sealing the suborbital member should succeed");
                InGameAssert.AreEqual(1, scenario.RewindPoints.Count,
                    "RP should remain until the last open CP slot is sealed");

                RewindPointReaper.DeleteQuicksaveForTesting = id =>
                {
                    deleteHookCalled = string.Equals(id, rpId, StringComparison.Ordinal);
                    return true;
                };

                InGameAssert.IsTrue(UnfinishedFlightSealHandler.TrySeal(eva, out reason),
                    "Sealing the final open member should succeed");
                InGameAssert.AreEqual(MergeState.CommittedProvisional, eva.MergeState,
                    "Last Seal must not mutate MergeState");
                InGameAssert.IsTrue(deleteHookCalled,
                    "Last Seal should invoke the RP quicksave delete hook");
                InGameAssert.AreEqual(0, scenario.RewindPoints.Count,
                    "Last sealed CP slot should auto-reap the synthetic RP");
                InGameAssert.IsNull(branchPoint.RewindPointId,
                    "Auto-reap should clear the BranchPoint back-reference");
            }
            finally
            {
                var trees = RecordingStore.CommittedTrees;
                for (int i = trees.Count - 1; i >= 0; i--)
                    if (object.ReferenceEquals(trees[i], tree))
                        trees.RemoveAt(i);

                for (int i = 0; i < recordings.Length; i++)
                    RecordingStore.RemoveCommittedInternal(recordings[i]);

                scenario.RewindPoints = savedRps;
                scenario.RecordingSupersedes = savedSupersedes;
                scenario.LedgerTombstones = savedTombstones;
                scenario.BumpSupersedeStateVersion();
                scenario.BumpTombstoneStateVersion();

                RewindPointReaper.ResetTestOverrides();
                UnfinishedFlightSealHandler.ResetForTesting();
                EffectiveState.ResetCachesForTesting();
            }
        }

        private static Recording NewRecording(
            string treeId,
            string branchPointId,
            string idStem,
            string vesselName,
            MergeState mergeState,
            TerminalState terminal)
        {
            return new Recording
            {
                RecordingId = "stableleaf_igt_" + idStem,
                VesselName = vesselName,
                TreeId = treeId,
                MergeState = mergeState,
                ParentBranchPointId = branchPointId,
                TerminalStateValue = terminal,
                ExplicitStartUT = 100.0,
                ExplicitEndUT = 160.0,
            };
        }

        private static ChildSlot NewSlot(int slotIndex, string originChildRecordingId, bool controllable)
        {
            return new ChildSlot
            {
                SlotIndex = slotIndex,
                OriginChildRecordingId = originChildRecordingId,
                Controllable = controllable,
            };
        }

        private static void AssertContainsMember(
            IReadOnlyList<Recording> members,
            Recording expected,
            string message)
        {
            InGameAssert.IsTrue(ContainsMember(members, expected), message);
        }

        private static void AssertDoesNotContainMember(
            IReadOnlyList<Recording> members,
            Recording unexpected,
            string message)
        {
            InGameAssert.IsFalse(ContainsMember(members, unexpected), message);
        }

        private static bool ContainsMember(IReadOnlyList<Recording> members, Recording rec)
        {
            if (members == null || rec == null)
                return false;

            for (int i = 0; i < members.Count; i++)
                if (object.ReferenceEquals(members[i], rec))
                    return true;

            return false;
        }

        private static int CountSyntheticMembers(IReadOnlyList<Recording> members, string suffix)
        {
            if (members == null)
                return 0;

            int count = 0;
            string needle = "_" + suffix;
            for (int i = 0; i < members.Count; i++)
            {
                var rec = members[i];
                if (rec != null
                    && !string.IsNullOrEmpty(rec.RecordingId)
                    && rec.RecordingId.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    count++;
            }

            return count;
        }
    }
}
