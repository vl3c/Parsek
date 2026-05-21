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
            // Both slots are OPEN crashed Unfinished Flights, so their effective
            // tips are CommittedProvisional (post-promotion). Open/closed is
            // read from the tip MergeState after collapse-seal-into-mergestate.
            var parent = Rec(
                "rec_parent",
                MergeState.CommittedProvisional,
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
                out string overrideReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: probeSlot));
            Assert.Equal("stableTerminalFocusSlot", overrideReason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_probe")
                && l.Contains("reason=stableTerminalFocusSlot")
                && l.Contains("focusSlotOverride=1"));
        }

        [Fact]
        public void OrbitingFocusSlot_StaticFocusPathUnchangedByOverride()
        {
            // When the merge-time slot equals rp.FocusSlotIndex (the player
            // is Re-Flying the static focus slot), the override and the
            // static-focus check both produce stableTerminalFocusSlot. The
            // override branch fires first under the v0.9.1 ordering so the
            // verdict comes out of the override path; the static-focus
            // check remains in the code as the natural-merge / non-override
            // path. End-result reason and qualifies=false are identical
            // either way, which is what this test pins.
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
                out string staticReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: focusSlot));
            Assert.Equal("stableTerminalFocusSlot", staticReason);
        }

        [Fact]
        public void SubOrbitalNonFocusSlot_WithFocusOverride_FallsThroughToStableLeafUnconcluded()
        {
            // A suborbital arc is "still in flight" under the new seal
            // contract (the vessel will crash, land, splash, or with a burn
            // reach orbit). The Re-Fly merge focus override therefore must
            // NOT seal on SubOrbital - it falls through to the
            // stableLeafUnconcluded branch and keeps the slot open. Contrast
            // with OrbitingNonFocusSlot_WithFocusOverride_ReturnsStableTerminalFocusSlot
            // above, where Orbiting on the same shape DOES seal.
            const string treeId = "tree_suborbital_probe";
            const string bpId = "bp_suborbital_split";
            var probe = Rec(
                "rec_suborbital_probe",
                MergeState.CommittedProvisional,
                TerminalState.SubOrbital,
                parentBranchPointId: bpId);
            InstallTree(treeId, probe);
            var rp = RpWithFocus("rp_suborbital_split", bpId, 0, "rec_parent", "rec_suborbital_probe");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                probe, out RewindPoint probeRp, out int probeSlot));
            Assert.Same(rp, probeRp);
            Assert.Equal(1, probeSlot);

            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                probe,
                rp.ChildSlots[probeSlot],
                rp,
                out string overrideReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: probeSlot));
            Assert.Equal("stableLeafUnconcluded", overrideReason);
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_suborbital_probe")
                && l.Contains("reason=stableLeafUnconcluded")
                && l.Contains("terminal=SubOrbital"));
        }

        [Fact]
        public void SubOrbitalFocusSlot_StaticFocusPath_FallsThroughToStableLeafUnconcluded()
        {
            // Mirror of OrbitingFocusSlot_StaticFocusPathUnchangedByOverride
            // but flipped: SubOrbital + slot == rp.FocusSlotIndex no longer
            // returns stableTerminalFocusSlot (slot stays open). The
            // Re-Fly override also does not fire for SubOrbital, so neither
            // path can seal the slot on the focus index. The natural-merge
            // (non-override) call also lands here.
            const string treeId = "tree_focus_suborbital";
            const string bpId = "bp_focus_suborbital_split";
            var focus = Rec(
                "rec_focus_suborbital",
                MergeState.CommittedProvisional,
                TerminalState.SubOrbital,
                parentBranchPointId: bpId);
            InstallTree(treeId, focus);
            var rp = RpWithFocus("rp_focus_suborbital_split", bpId, 0, "rec_focus_suborbital", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(UnfinishedFlightClassifier.TryResolveRewindPointForRecording(
                focus, out RewindPoint focusRp, out int focusSlot));
            Assert.Same(rp, focusRp);
            Assert.Equal(0, focusSlot);

            // Without the override (natural-merge / non-Re-Fly call).
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                focus,
                rp.ChildSlots[focusSlot],
                rp,
                out string naturalReason));
            Assert.Equal("stableLeafUnconcluded", naturalReason);

            // With the override (Re-Fly call site).
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                focus,
                rp.ChildSlots[focusSlot],
                rp,
                out string overrideReason,
                treeContext: null,
                allowNotCommitted: false,
                focusSlotOverride: focusSlot));
            Assert.Equal("stableLeafUnconcluded", overrideReason);
        }

        [Fact]
        public void OptimizerSplitChainContinuation_OnlyChainHeadQualifiesAsUnfinishedFlight()
        {
            // Reproduces the bug from logs/2026-05-18_1853_stash-4-recordings:
            // RecordingOptimizer phase-change split creates a chain HEAD
            // (with BPs matching the rewind point) and a chain TIP (no BPs)
            // sharing the same ChainId. The low-level
            // UnfinishedFlightClassifier predicate stays permissive — both
            // chain members pass TryQualify so RewindPointReaper can route
            // the chain TIP through Qualifies and decide to keep the rewind
            // point alive (covered by RewindPointReaperTests). But the
            // consumer-facing EffectiveState.IsUnfinishedFlight predicate
            // (STASH membership, regular-tree filter, timeline marker,
            // group-picker drop-target, legacy R-button suppression) must
            // emit one row per logical flight. With both halves admitted,
            // the stash showed 4 entries for a 2-slot RP; post-fix only
            // the chain head admits and the stash shows 2.
            const string treeId = "tree_phase_split";
            const string bpId = "bp_slot_anchor";
            const string chainId = "chain_kerbal_x";
            var head = Rec(
                "rec_head",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                childBranchPointId: bpId);
            head.ChainId = chainId;
            head.ChainIndex = 0;
            var tip = Rec(
                "rec_tip",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed);
            tip.ChainId = chainId;
            tip.ChainIndex = 1;
            InstallTree(treeId, head, tip);
            var rp = RpWithFocus("rp_slot_anchor", bpId, 0, "rec_head", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            // Low-level classifier stays permissive: both chain members
            // pass TryQualify when the reaper passes them directly with a
            // chosen slot. Pinned so a regression here would be caught
            // before RewindPointReaperTests' more elaborate fixture trips.
            Assert.True(UnfinishedFlightClassifier.Qualifies(
                head, rp.ChildSlots[0], rp));
            Assert.True(UnfinishedFlightClassifier.Qualifies(
                tip, rp.ChildSlots[0], rp));

            // Consumer-facing predicate suppresses the chain continuation:
            // both members resolve to slot 0, slot.Origin is "rec_head" so
            // HEAD is the anchor and TIP suppresses with
            // reason=slotPeerAnchored.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(EffectiveState.IsUnfinishedFlight(head));
            Assert.False(EffectiveState.IsUnfinishedFlight(tip));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_tip")
                && l.Contains("reason=slotPeerAnchored")
                && l.Contains("slotOrigin=rec_head")
                && l.Contains("anchorRec=rec_head"));

            // The STASH UI group falls out of the predicate, so it lists
            // one row per logical flight without doing its own dedupe.
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_head", members[0].RecordingId);
        }

        [Fact]
        public void OptimizerSplitChainContinuation_MultiSegmentChainCollapsesToHead()
        {
            // Three-member chain (Atmospheric -> ExoBallistic -> ...) —
            // confirms only the slot-anchor (HEAD) surfaces, not a
            // mid-chain segment that happens to be enumerated first.
            const string treeId = "tree_multi";
            const string bpId = "bp_multi_anchor";
            const string chainId = "chain_multi";
            var head = Rec("rec_head", MergeState.CommittedProvisional,
                TerminalState.Destroyed, childBranchPointId: bpId);
            head.ChainId = chainId;
            head.ChainIndex = 0;
            var mid = Rec("rec_mid", MergeState.CommittedProvisional,
                TerminalState.Destroyed);
            mid.ChainId = chainId;
            mid.ChainIndex = 1;
            var tail = Rec("rec_tail", MergeState.CommittedProvisional,
                TerminalState.Destroyed);
            tail.ChainId = chainId;
            tail.ChainIndex = 2;
            InstallTree(treeId, head, mid, tail);
            var rp = RpWithFocus("rp_multi", bpId, 0, "rec_head", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(head));
            Assert.False(EffectiveState.IsUnfinishedFlight(mid));
            Assert.False(EffectiveState.IsUnfinishedFlight(tail));

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_head", members[0].RecordingId);
        }

        [Fact]
        public void ChainContinuationWhoseHeadFailsRaw_StillSurfacesAsUnfinishedFlight()
        {
            // Asymmetric chain shape: the chain HEAD is the launch-row
            // (no parent/child BP linkage, no BP match), and only the
            // chain TIP carries the BP-linked qualifier. slot.Origin is
            // the TIP recording, so TIP is the anchor and surfaces. The
            // HEAD doesn't pass Raw at all (no slot match), so the
            // dedupe never runs for it. Pinned alongside the optimizer-
            // split tests so the asymmetry is documented in one place.
            const string treeId = "tree_launch_chain";
            const string bpId = "bp_launch_split";
            const string chainId = "chain_launch";
            var head = Rec(
                "rec_launch_head",
                MergeState.CommittedProvisional,
                TerminalState.Landed);
            head.ChainId = chainId;
            head.ChainIndex = 0;
            var tip = Rec(
                "rec_launch_tip",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                parentBranchPointId: bpId);
            tip.ChainId = chainId;
            tip.ChainIndex = 1;
            InstallTree(treeId, head, tip);
            var rp = RpWithFocus("rp_launch", bpId, 0, "rec_launch_tip", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            // HEAD has no BP linkage and is not the slot origin -> Raw
            // rejects it -> consumer-facing predicate rejects it.
            Assert.False(EffectiveState.IsUnfinishedFlight(head));

            // TIP carries the BP-linked qualifier. The dedupe's lower-
            // index-peer check looks at HEAD, finds Raw(HEAD)=false, and
            // does NOT suppress TIP. TIP surfaces.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(EffectiveState.IsUnfinishedFlight(tip));
            Assert.DoesNotContain(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_launch_tip")
                && l.Contains("reason=slotPeerAnchored"));

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_launch_tip", members[0].RecordingId);
        }

        [Fact]
        public void SupersedeTargetWithoutChainId_SuppressedByVisibleLaunchOriginAnchor()
        {
            // Reproduces the bug from logs/2026-05-19_2055_pr901-stash-organization:
            // a re-fly merge supersedes a chain continuation member (a mid-
            // chain TIP) and writes a new recording with empty ChainId.
            // The slot resolution walker (ResolveRewindPointSlotIndexForRecording)
            // hops chain-then-supersede, so the new recording resolves to the
            // same slot as the visible chain HEAD. Without slot-anchor
            // dedupe, both pass IsUnfinishedFlight and STASH renders two
            // "Kerbal X" rows. The slot's OriginChildRecordingId is the
            // launch row (HEAD), which is itself ERS-visible, so HEAD is
            // the anchor and the empty-ChainId supersede target suppresses
            // with reason=slotPeerAnchored.
            const string treeId = "tree_refly_chain_tip";
            const string bpId = "bp_refly";
            const string chainId = "chain_kerbal_x";

            // Launch row: chainIndex 0, visible, slot.Origin.
            var launchHead = Rec(
                "rec_launch_head",
                MergeState.CommittedProvisional,
                TerminalState.SubOrbital);
            launchHead.ChainId = chainId;
            launchHead.ChainIndex = 0;

            // Chain continuation tip: chainIndex 1. Will be superseded.
            var chainTip = Rec(
                "rec_chain_tip",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed);
            chainTip.ChainId = chainId;
            chainTip.ChainIndex = 1;

            // Re-fly supersede target: no ChainId, BP-linked to the rewind
            // point. ResolveRewindPointSlotIndexForRecording hops
            // chain → tip → supersede to map this to slot 0.
            var supersedeTarget = Rec(
                "rec_refly_tip",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                parentBranchPointId: bpId);
            // ChainId left null — re-fly target writes without inheriting.

            InstallTree(treeId, launchHead, chainTip, supersedeTarget);
            var rp = RpWithFocus("rp_refly", bpId, 0, "rec_launch_head", "rec_other");
            var scenario = InstallScenario(new List<RewindPoint> { rp });
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_test",
                OldRecordingId = "rec_chain_tip",
                NewRecordingId = "rec_refly_tip",
                UT = 100.0
            });
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            // Launch HEAD admits as the slot anchor.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.True(EffectiveState.IsUnfinishedFlight(launchHead));

            // Supersede target maps to the same slot but is not the anchor
            // — it suppresses with reason=slotPeerAnchored.
            ParsekLog.ResetRateLimitsForTesting();
            logLines.Clear();
            Assert.False(EffectiveState.IsUnfinishedFlight(supersedeTarget));
            Assert.Contains(logLines, l =>
                l.Contains("[UnfinishedFlights]")
                && l.Contains("rec=rec_refly_tip")
                && l.Contains("reason=slotPeerAnchored")
                && l.Contains("slotOrigin=rec_launch_head")
                && l.Contains("anchorRec=rec_launch_head"));

            // STASH lists exactly one row for the slot.
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_launch_head", members[0].RecordingId);
        }

        [Fact]
        public void ChainContinuationStillReportsChainMembershipOfUnfinishedFlight()
        {
            // After the slot-anchor dedupe, only the chain HEAD admits as
            // IsUnfinishedFlight=true. The chain TIP itself returns false.
            // But IsChainMemberOfUnfinishedFlight must still return true
            // for the TIP — it scans same-ChainId peers and asks each
            // one. The HEAD admits, so the TIP correctly reports it
            // belongs to an unfinished flight chain. This is the contract
            // the R-button suppression and chain-aware UI sites rely on.
            const string treeId = "tree_chain_membership";
            const string bpId = "bp_anchor";
            const string chainId = "chain_x";

            var head = Rec(
                "rec_head",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                childBranchPointId: bpId);
            head.ChainId = chainId;
            head.ChainIndex = 0;

            var tip = Rec(
                "rec_tip",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed);
            tip.ChainId = chainId;
            tip.ChainIndex = 1;

            InstallTree(treeId, head, tip);
            var rp = RpWithFocus("rp_chain", bpId, 0, "rec_head", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(head));
            Assert.False(EffectiveState.IsUnfinishedFlight(tip));

            // Membership query: both peers report true because the chain
            // contains an Unfinished Flight anchor (HEAD).
            Assert.True(EffectiveState.IsChainMemberOfUnfinishedFlight(head));
            Assert.True(EffectiveState.IsChainMemberOfUnfinishedFlight(tip));
        }

        [Fact]
        public void SupersedeTargetWithHiddenOrigin_BecomesAnchor()
        {
            // Companion to SupersedeTargetWithoutChainId_SuppressedByVisibleLaunchOriginAnchor:
            // when the slot's OriginChildRecordingId is itself superseded
            // (and therefore hidden in ERS), the anchor walks supersedes
            // forward from origin and the visible supersede target wins.
            const string treeId = "tree_refly_origin";
            const string bpId = "bp_origin";

            var origin = Rec(
                "rec_origin",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed);

            var supersedeTarget = Rec(
                "rec_refly_target",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                parentBranchPointId: bpId);

            InstallTree(treeId, origin, supersedeTarget);
            var rp = RpWithFocus("rp_refly_origin", bpId, 0, "rec_origin", "rec_other");
            var scenario = InstallScenario(new List<RewindPoint> { rp });
            scenario.RecordingSupersedes.Add(new RecordingSupersedeRelation
            {
                RelationId = "rsr_origin",
                OldRecordingId = "rec_origin",
                NewRecordingId = "rec_refly_target",
                UT = 100.0
            });
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            // Origin is hidden (superseded), supersede target is visible
            // and becomes the slot anchor.
            Assert.False(EffectiveState.IsUnfinishedFlight(origin));
            Assert.True(EffectiveState.IsUnfinishedFlight(supersedeTarget));

            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_refly_target", members[0].RecordingId);
        }

        [Fact]
        public void NonChainRecording_AdmitsWithoutChainDedupe()
        {
            // The slot-anchor dedupe must not interfere with the legacy
            // single-recording flow: a recording with null ChainId that
            // is itself the slot origin admits directly.
            const string treeId = "tree_solo";
            const string bpId = "bp_solo_anchor";
            var solo = Rec(
                "rec_solo",
                MergeState.CommittedProvisional,
                TerminalState.Destroyed,
                childBranchPointId: bpId);
            // ChainId left null — non-chain recording.
            InstallTree(treeId, solo);
            var rp = RpWithFocus("rp_solo", bpId, 0, "rec_solo", "rec_other");
            InstallScenario(new List<RewindPoint> { rp });

            Assert.True(EffectiveState.IsUnfinishedFlight(solo));
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_solo", members[0].RecordingId);
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

        [Fact]
        public void OpenClosedFilter_ImmutableTip_HidesShapeQualifyingSlotFromUf()
        {
            // The terminal shape qualifies (crashed), but the slot's effective
            // tip is Immutable (sealed / concluded), so it is NOT an open
            // Unfinished Flight. Open/closed is read solely from the tip
            // MergeState (collapse-seal-into-mergestate plan §7.7).
            const string treeId = "tree_open_closed";
            const string bpId = "bp_open_closed";
            var crashed = Rec(
                "rec_crashed",
                MergeState.Immutable,
                TerminalState.Destroyed,
                parentBranchPointId: bpId);
            InstallTree(treeId, crashed);
            var rp = RpWithFocus("rp_open_closed", bpId, 0, "rec_other", "rec_crashed");
            InstallScenario(new List<RewindPoint> { rp });

            // Shape predicate still qualifies the crashed slot.
            Assert.True(UnfinishedFlightClassifier.TryQualify(
                crashed, rp.ChildSlots[1], rp, out string shapeReason));
            Assert.Equal("crashed", shapeReason);

            // Slot tip is Immutable -> closed -> not open.
            Assert.False(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));

            // Consumer-facing open read returns false; the row drops from UF.
            Assert.False(EffectiveState.IsUnfinishedFlight(crashed));
            Assert.Empty(UnfinishedFlightsGroup.ComputeMembers());

            // Flip the tip to CommittedProvisional -> open -> surfaces.
            crashed.MergeState = MergeState.CommittedProvisional;
            EffectiveState.ResetCachesForTesting();
            Assert.True(UnfinishedFlightClassifier.IsSlotEffectiveTipOpen(rp.ChildSlots[1]));
            Assert.True(EffectiveState.IsUnfinishedFlight(crashed));
            var members = UnfinishedFlightsGroup.ComputeMembers();
            Assert.Single(members);
            Assert.Equal("rec_crashed", members[0].RecordingId);
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
