using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    [Collection("Sequential")]
    public class UnfinishedFlightClassifierTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public UnfinishedFlightClassifierTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;
            priorVerbose = ParsekLog.IsVerboseEnabled;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        [Fact]
        public void DestroyedTreeBranchingParent_WithNoBpFields_ResolvesByOriginSlot()
        {
            const string treeId = "tree_kerbal_x";
            const string bpId = "bp_probe_split";
            var parent = Rec(
                "rec_parent",
                MergeState.Immutable,
                TerminalState.Destroyed);
            var child = Rec(
                "rec_probe",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                parentBranchPointId: bpId);
            InstallTree(treeId, parent, child);
            var rp = RpWithFocus("rp_probe_split", bpId, 0, "rec_parent", "rec_probe");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(parent));
            Assert.True(UnfinishedFlightClassifier.IsUnfinishedFlightCandidateShape(child));

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                parent, out RewindPoint parentRp, out int parentSlot));
            Assert.Same(rp, parentRp);
            Assert.Equal(0, parentSlot);

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                child, out RewindPoint childRp, out int childSlot));
            Assert.Same(rp, childRp);
            Assert.Equal(1, childSlot);

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                parent,
                rp.ChildSlots[parentSlot],
                rp,
                considerSealed: true,
                out string parentReason));
            Assert.Equal("crashed", parentReason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_parent")
                && l.Contains("reason=crashed")
                && l.Contains("side=origin-only"));

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            var members = UnfinishedFlightsGroup.ComputeMembers();

            Assert.Equal(2, members.Count);
            Assert.Contains(members, r => r.RecordingId == "rec_parent");
            Assert.Contains(members, r => r.RecordingId == "rec_probe");
        }

        [Fact]
        public void ActiveParentChildBranchPoint_TakesPrecedenceOverOriginSlotFallback()
        {
            const string treeId = "tree_chain";
            const string bpId = "bp_live_split";
            var parent = Rec(
                "rec_parent",
                MergeState.Immutable,
                TerminalState.Destroyed,
                childBranchPointId: bpId);
            InstallTree(treeId, parent);
            var earlierOriginRp = Rp("rp_origin_first", "bp_other", "rec_parent");
            var branchRp = Rp("rp_branch", bpId, "rec_parent");
            InstallScenario(new List<RewindPoint> { earlierOriginRp, branchRp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                parent, out RewindPoint resolvedRp, out int slotListIndex));
            Assert.Same(branchRp, resolvedRp);
            Assert.Equal(0, slotListIndex);

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                parent,
                branchRp.ChildSlots[slotListIndex],
                branchRp,
                considerSealed: true,
                out string reason));
            Assert.Equal("crashed", reason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_parent")
                && l.Contains("reason=crashed")
                && l.Contains("side=active-parent-child"));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_parent")
                && l.Contains("side=origin-only"));
        }

        private static Recording Rec(
            string id,
            MergeState state,
            TerminalState terminal,
            string parentBranchPointId = null,
            string childBranchPointId = null)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId
            };
        }

        private static void InstallTree(string treeId, params Recording[] recordings)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test tree",
                RootRecordingId = recordings.FirstOrDefault()?.RecordingId
            };

            foreach (var rec in recordings)
            {
                rec.TreeId = treeId;
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec);
            }

            RecordingStore.AddCommittedTreeForTesting(tree);
        }

        private static ParsekScenario InstallScenario(List<RewindPoint> rps)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = rps ?? new List<RewindPoint>()
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            scenario.BumpTombstoneStateVersion();
            EffectiveState.ResetCachesForTesting();
            return scenario;
        }

        private static RewindPoint Rp(string rpId, string bpId, params string[] slotRecordingIds)
            => RpWithFocus(rpId, bpId, -1, slotRecordingIds);

        private static RewindPoint RpWithFocus(
            string rpId,
            string bpId,
            int focusSlotIndex,
            params string[] slotRecordingIds)
        {
            var slots = new List<ChildSlot>();
            if (slotRecordingIds != null)
            {
                for (int i = 0; i < slotRecordingIds.Length; i++)
                {
                    slots.Add(new ChildSlot
                    {
                        SlotIndex = i,
                        OriginChildRecordingId = slotRecordingIds[i],
                        Controllable = true
                    });
                }
            }

            return new RewindPoint
            {
                RewindPointId = rpId,
                BranchPointId = bpId,
                UT = 0.0,
                FocusSlotIndex = focusSlotIndex,
                ChildSlots = slots
            };
        }
    }
}
