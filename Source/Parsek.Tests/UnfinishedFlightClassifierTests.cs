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

        [Fact]
        public void OrbitingNonFocusSlot_WithFocusOverride_ReturnsStableTerminalFocusSlot()
        {
            // Re-Fly merge call site passes the merge-time slot index here so
            // a player who Re-Flew this slot themselves is treated as the
            // de-facto focus. Without the override the slot-aware path would
            // return stableLeafUnconcluded and keep the slot re-flyable.
            const string treeId = "tree_probe";
            const string bpId = "bp_probe_split";
            var probe = Rec(
                "rec_probe",
                MergeState.CommittedProvisional,
                TerminalState.Orbiting,
                parentBranchPointId: bpId);
            InstallTree(treeId, probe);
            var rp = RpWithFocus("rp_probe_split", bpId, 0, "rec_parent", "rec_probe");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                probe, out RewindPoint probeRp, out int probeSlot));
            Assert.Same(rp, probeRp);
            Assert.Equal(1, probeSlot);

            // Sanity: without the override, the slot stays unconcluded.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                probe,
                rp.ChildSlots[probeSlot],
                rp,
                considerSealed: false,
                out string baselineReason));
            Assert.Equal("stableLeafUnconcluded", baselineReason);

            // With the override pointing at this slot, the classifier
            // returns stableTerminalFocusSlot (qualifies=false) and the
            // log line tags the override value.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                probe,
                rp.ChildSlots[probeSlot],
                rp,
                considerSealed: false,
                out string overrideReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: probeSlot));
            Assert.Equal("stableTerminalFocusSlot", overrideReason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_probe")
                && l.Contains("reason=stableTerminalFocusSlot")
                && l.Contains("override=1"));
        }

        [Fact]
        public void OrbitingFocusSlot_StaticFocusPathUnchangedByOverride()
        {
            // When the slot already matches rp.FocusSlotIndex, the static
            // focus check fires before the override branch; behavior is
            // unchanged regardless of whether the override is supplied.
            const string treeId = "tree_focus";
            const string bpId = "bp_focus_split";
            var focus = Rec(
                "rec_focus",
                MergeState.CommittedProvisional,
                TerminalState.Orbiting,
                parentBranchPointId: bpId);
            InstallTree(treeId, focus);
            var rp = RpWithFocus("rp_focus_split", bpId, 0, "rec_focus", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                focus, out RewindPoint focusRp, out int focusSlot));
            Assert.Same(rp, focusRp);
            Assert.Equal(0, focusSlot);

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.False(UnfinishedFlightClassifier.TryQualify(
                focus,
                rp.ChildSlots[focusSlot],
                rp,
                considerSealed: false,
                out string staticReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: focusSlot));
            Assert.Equal("stableTerminalFocusSlot", staticReason);
        }

        [Fact]
        public void OriginOnlyFallback_WithMultipleMatchingRps_ResolvesLatest()
        {
            const string treeId = "tree_multi_rp";
            var parent = Rec(
                "rec_parent",
                MergeState.Immutable,
                TerminalState.Destroyed);
            InstallTree(treeId, parent);
            var olderRp = Rp("rp_older", "bp_older", "rec_parent");
            olderRp.UT = 10.0;
            var latestRp = RpWithFocus("rp_latest", "bp_latest", 0, "rec_other", "rec_parent");
            latestRp.UT = 46.0;
            InstallScenario(new List<RewindPoint> { olderRp, latestRp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                parent, out RewindPoint resolvedRp, out int slotListIndex));

            Assert.Same(latestRp, resolvedRp);
            Assert.Equal(1, slotListIndex);
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
