using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pins the parent-anchor cascade in
    /// <see cref="EffectiveState.ComputeRewindRetiredRecordingIds(IReadOnlyList{Recording}, IReadOnlyList{RecordingRewindRetirement})"/>.
    ///
    /// <para>
    /// Bug evidence: playtest save
    /// <c>logs/2026-05-19_2329_pr909-narrowed-gate-playtest/saves/x4/persistent.sfs</c>.
    /// Retirement <c>rrt_33919eadcd674138baef970cb3e7b5b7</c> retires
    /// <c>rec_2c68978d</c>. Recording <c>3d4713df</c> has
    /// <c>parentAnchorRecordingId = rec_2c68978d</c> but no retirement of its
    /// own; pre-fix it rendered as an orphan debris ghost alongside the
    /// restored recording's own debris children.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class OrphanDebrisOnRetirementTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;
        private readonly bool priorVerbose;

        public OrphanDebrisOnRetirementTests()
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
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = priorParsekLogSuppress;
            RecordingStore.SuppressLogging = priorStoreSuppress;
            RecordingStore.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        // --- Helpers ----------------------------------------------------------

        private static Recording Rec(
            string id,
            string parentAnchorRecordingId = null,
            MergeState state = MergeState.CommittedProvisional)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                ParentAnchorRecordingId = parentAnchorRecordingId,
            };
        }

        private static RecordingRewindRetirement Retire(string recordingId, string restoredId = null)
        {
            return new RecordingRewindRetirement
            {
                RetirementId = "rrt_" + recordingId,
                RecordingId = recordingId,
                RestoredRecordingId = restoredId,
                Reason = RecordingRewindRetirement.DefaultReason,
            };
        }

        private static Recording RecChain(
            string id,
            string chainId,
            int chainIndex,
            string provisionalForRpId = null,
            string parentAnchorRecordingId = null,
            MergeState state = MergeState.CommittedProvisional)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                MergeState = state,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ProvisionalForRpId = provisionalForRpId,
                ParentAnchorRecordingId = parentAnchorRecordingId,
            };
        }

        // =====================================================================
        // ComputeRewindRetiredRecordingIds cascade overload
        // =====================================================================

        [Fact]
        public void Cascade_RetiredParent_HidesParentAnchoredChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent", "rec_restored") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.Contains("rec_child", retired);
        }

        [Fact]
        public void Cascade_MultipleChildren_HidesAllParentAnchoredChildren()
        {
            var parent = Rec("rec_parent");
            var c1 = Rec("rec_c1", parentAnchorRecordingId: "rec_parent");
            var c2 = Rec("rec_c2", parentAnchorRecordingId: "rec_parent");
            var c3 = Rec("rec_c3", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, c1, c2, c3 };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Equal(4, retired.Count);
            Assert.Contains("rec_c1", retired);
            Assert.Contains("rec_c2", retired);
            Assert.Contains("rec_c3", retired);
        }

        [Fact]
        public void Cascade_TransitiveChain_HidesGrandchildren()
        {
            // rec_parent -> rec_child -> rec_grandchild.
            // grandchild's ParentAnchorRecordingId points at child, not parent;
            // fixed-point closure adds child first, then grandchild.
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var grandchild = Rec("rec_grandchild", parentAnchorRecordingId: "rec_child");
            var recordings = new List<Recording> { parent, child, grandchild };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.Contains("rec_child", retired);
            Assert.Contains("rec_grandchild", retired);
        }

        [Fact]
        public void Cascade_DepthFourChain_HidesAllDescendants()
        {
            // P -> c1 -> c2 -> c3 (depth 4). Pins that the fixed-point
            // closure reaches arbitrary depth, not just two levels.
            var parent = Rec("rec_p");
            var c1 = Rec("rec_c1", parentAnchorRecordingId: "rec_p");
            var c2 = Rec("rec_c2", parentAnchorRecordingId: "rec_c1");
            var c3 = Rec("rec_c3", parentAnchorRecordingId: "rec_c2");
            var recordings = new List<Recording> { parent, c1, c2, c3 };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_p") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Equal(4, retired.Count);
            Assert.Contains("rec_c1", retired);
            Assert.Contains("rec_c2", retired);
            Assert.Contains("rec_c3", retired);
        }

        [Fact]
        public void Cascade_ReverseListOrder_StillReachesAllDescendants()
        {
            // Recordings ordered descendant-first (c3, c2, c1, parent). A
            // single-pass scan would add only c1 (whose parent is in the
            // seed); c2 and c3 require the fixed-point loop's extra passes.
            // Pins that the do/while closure (not a single pass) is what
            // makes the cascade complete.
            var c3 = Rec("rec_c3", parentAnchorRecordingId: "rec_c2");
            var c2 = Rec("rec_c2", parentAnchorRecordingId: "rec_c1");
            var c1 = Rec("rec_c1", parentAnchorRecordingId: "rec_p");
            var parent = Rec("rec_p");
            var recordings = new List<Recording> { c3, c2, c1, parent };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_p") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Equal(4, retired.Count);
            Assert.Contains("rec_c1", retired);
            Assert.Contains("rec_c2", retired);
            Assert.Contains("rec_c3", retired);
        }

        [Fact]
        public void Cascade_SelfParentRecording_TerminatesAndStaysVisibleWhenNotRetired()
        {
            // Corrupt save: a recording whose ParentAnchorRecordingId points
            // at itself. The closure must terminate (it does: the recording
            // is only added if its parent id is already in the set, and it
            // can never seed itself) and the self-parent recording stays
            // visible because it is not retired.
            var selfParent = Rec("rec_self", parentAnchorRecordingId: "rec_self");
            var unrelatedRetired = Rec("rec_other");
            var recordings = new List<Recording> { selfParent, unrelatedRetired };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_other") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_other", retired);
            Assert.DoesNotContain("rec_self", retired);
        }

        [Fact]
        public void Cascade_TwoNodeCycleNeitherRetired_TerminatesAndStaysVisible()
        {
            // Corrupt save: A.parent = B, B.parent = A, neither retired.
            // The closure must terminate (no seed to expand from) and leave
            // both visible. Guards against an infinite loop if a future
            // change ever seeds from one of them by accident.
            var a = Rec("rec_A", parentAnchorRecordingId: "rec_B");
            var b = Rec("rec_B", parentAnchorRecordingId: "rec_A");
            var recordings = new List<Recording> { a, b };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_unrelated") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.DoesNotContain("rec_A", retired);
            Assert.DoesNotContain("rec_B", retired);
        }

        [Fact]
        public void Cascade_TwoNodeCycleOneRetired_HidesBothAndTerminates()
        {
            // A.parent = B, B.parent = A, A retired. The closure adds B (its
            // parent A is retired), then A is already in the seed so the
            // Contains short-circuit prevents re-add and the loop terminates.
            var a = Rec("rec_A", parentAnchorRecordingId: "rec_B");
            var b = Rec("rec_B", parentAnchorRecordingId: "rec_A");
            var recordings = new List<Recording> { a, b };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_A") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_A", retired);
            Assert.Contains("rec_B", retired);
            Assert.Equal(2, retired.Count);
        }

        [Fact]
        public void Cascade_UnrelatedRecording_StaysVisible()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, child, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.DoesNotContain("rec_unrelated", retired);
        }

        [Fact]
        public void Cascade_ChildOfNonRetiredParent_StaysVisible()
        {
            var retiredParent = Rec("rec_retired");
            var liveParent = Rec("rec_live");
            var liveChild = Rec("rec_liveChild", parentAnchorRecordingId: "rec_live");
            var recordings = new List<Recording> { retiredParent, liveParent, liveChild };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_retired") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_retired", retired);
            Assert.DoesNotContain("rec_live", retired);
            Assert.DoesNotContain("rec_liveChild", retired);
        }

        [Fact]
        public void Cascade_NoRetirements_ReturnsEmpty()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(
                recordings, new List<RecordingRewindRetirement>());

            Assert.Empty(retired);
        }

        [Fact]
        public void Cascade_ParentNotRetiredButChildHasStaleDebrisParentId_StaysVisible()
        {
            // Negative test for the ParentAnchorRecordingId lookup landing on
            // a non-retired recording.
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_unrelated");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, child, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_parent", retired);
            Assert.DoesNotContain("rec_child", retired);
            Assert.DoesNotContain("rec_unrelated", retired);
        }

        [Fact]
        public void Cascade_LogsVerboseSummaryWhenChildrenAdded()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains(logLines, l =>
                l.Contains("[ERS]")
                && l.Contains("Rewind-retirement cascade")
                && l.Contains("parentAnchorAdded=1"));
        }

        [Fact]
        public void Cascade_NoChildrenAdded_DoesNotLog()
        {
            // Retired parent with no parent-anchored children: no cascade log
            // line, so quiet steady-state ERS rebuilds do not gain new noise.
            var parent = Rec("rec_parent");
            var unrelated = Rec("rec_unrelated");
            var recordings = new List<Recording> { parent, unrelated };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.DoesNotContain(logLines, l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
        }

        // =====================================================================
        // Chain-continuation cascade (rolled-back Re-Fly fork)
        // =====================================================================

        [Fact]
        public void ChainCascade_RetiredForkHead_HidesChainContinuation()
        {
            // A rolled-back Re-Fly retires only the fork chain HEAD; its
            // continuation (carrying the predicted orbit tail) shares the
            // (ChainId, ProvisionalForRpId) and a higher ChainIndex and must be
            // retired alongside it. The restored original lives on a different
            // chain and stays visible.
            var forkHead = RecChain("rec_forkhead", "chain_fork", 0, provisionalForRpId: "rp_1");
            var continuation = RecChain("rec_continuation", "chain_fork", 1, provisionalForRpId: "rp_1");
            var restoredOriginal = RecChain("rec_original", "chain_original", 1);
            var recordings = new List<Recording> { forkHead, continuation, restoredOriginal };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_forkhead", "rec_original") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_forkhead", retired);
            Assert.Contains("rec_continuation", retired);
            Assert.DoesNotContain("rec_original", retired);
        }

        [Fact]
        public void ChainCascade_IndependentCommittedChainMember_StaysVisible()
        {
            // A committed chain member that merely shares a ChainId but is NOT
            // provisional-for-the-rolled-back-RP must not be over-retired.
            var forkHead = RecChain("rec_forkhead", "chain_shared", 0, provisionalForRpId: "rp_1");
            var committedMember = RecChain("rec_committed", "chain_shared", 1, provisionalForRpId: null);
            var recordings = new List<Recording> { forkHead, committedMember };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_forkhead") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_forkhead", retired);
            Assert.DoesNotContain("rec_committed", retired);
        }

        [Fact]
        public void ChainCascade_DifferentProvisionalRp_StaysVisible()
        {
            // A continuation provisional for a DIFFERENT re-fly RP is not part of
            // this rolled-back fork and stays visible.
            var forkHead = RecChain("rec_forkhead", "chain_shared", 0, provisionalForRpId: "rp_1");
            var otherRpMember = RecChain("rec_otherrp", "chain_shared", 1, provisionalForRpId: "rp_2");
            var recordings = new List<Recording> { forkHead, otherRpMember };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_forkhead") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_forkhead", retired);
            Assert.DoesNotContain("rec_otherrp", retired);
        }

        [Fact]
        public void ChainCascade_LowerIndexMember_StaysVisible()
        {
            // Retirement names a higher-index member; a lower-index member of the
            // same fork (e.g. a kept origin-split HEAD) must not be dragged in by
            // the chain edge, which only propagates to higher indices.
            var lowerMember = RecChain("rec_lower", "chain_fork", 0, provisionalForRpId: "rp_1");
            var retiredMember = RecChain("rec_higher", "chain_fork", 1, provisionalForRpId: "rp_1");
            var recordings = new List<Recording> { lowerMember, retiredMember };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_higher") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_higher", retired);
            Assert.DoesNotContain("rec_lower", retired);
        }

        [Fact]
        public void ChainCascade_ContinuationDebris_AlsoHidden()
        {
            // Debris anchored to a chain continuation that is itself retired via
            // the chain edge must also retire (parent-anchor cascade picks it up
            // in the same fixed-point closure).
            var forkHead = RecChain("rec_forkhead", "chain_fork", 0, provisionalForRpId: "rp_1");
            var continuation = RecChain("rec_continuation", "chain_fork", 1, provisionalForRpId: "rp_1");
            var continuationDebris = RecChain(
                "rec_contdebris", "chain_other", 0,
                provisionalForRpId: "rp_1", parentAnchorRecordingId: "rec_continuation");
            var recordings = new List<Recording> { forkHead, continuation, continuationDebris };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_forkhead") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_forkhead", retired);
            Assert.Contains("rec_continuation", retired);
            Assert.Contains("rec_contdebris", retired);
        }

        [Fact]
        public void ChainCascade_LogsChainContinuationCount()
        {
            var forkHead = RecChain("rec_forkhead", "chain_fork", 0, provisionalForRpId: "rp_1");
            var continuation = RecChain("rec_continuation", "chain_fork", 1, provisionalForRpId: "rp_1");
            var recordings = new List<Recording> { forkHead, continuation };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_forkhead") };

            EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains(logLines, l =>
                l.Contains("[ERS]")
                && l.Contains("Rewind-retirement cascade")
                && l.Contains("chainContinuationAdded=1"));
        }

        [Fact]
        public void ChainCascade_PlaytestShape_HidesDuplicateProbeForkContinuation()
        {
            // Playtest 2026-05-20: "Kerbal X Probe" rendered as two ghosts after a
            // rolled-back Re-Fly. Fork chain 2856611e = rec_e0f42b57 (idx 0, HEAD,
            // retired) -> 982d6dee (idx 1, TIP, synthetic orbit tail). The restored
            // original 49538b60 lives on chain 59a82c8e. Pre-fix, 982d6dee escaped
            // retirement and rendered alongside 49538b60.
            var forkHead = RecChain("rec_e0f42b57", "2856611e", 0, provisionalForRpId: "rp_addf577");
            var forkTip = RecChain("982d6dee", "2856611e", 1, provisionalForRpId: "rp_addf577");
            var restoredOriginal = RecChain("49538b60", "59a82c8e", 1);
            var recordings = new List<Recording> { forkHead, forkTip, restoredOriginal };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_e0f42b57", "49538b60") };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains("rec_e0f42b57", retired);
            Assert.Contains("982d6dee", retired);
            Assert.DoesNotContain("49538b60", retired);
        }

        // =====================================================================
        // ComputeTimelineInactiveRecordingIds
        // =====================================================================

        [Fact]
        public void ComputeTimelineInactiveRecordingIds_RetiredParentCascade_MarksChildRewindRetired()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var inactive = EffectiveState.ComputeTimelineInactiveRecordingIds(
                recordings,
                new List<RecordingSupersedeRelation>(),
                retirements);

            Assert.Equal(TimelineInactiveReason.RewindRetired, inactive["rec_parent"]);
            Assert.Equal(TimelineInactiveReason.RewindRetired, inactive["rec_child"]);
        }

        // =====================================================================
        // IsRewindRetired cascade overload
        // =====================================================================

        [Fact]
        public void IsRewindRetired_Cascade_ReturnsTrueForParentAnchoredChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            Assert.True(EffectiveState.IsRewindRetired(parent, recordings, retirements));
            Assert.True(EffectiveState.IsRewindRetired(child, recordings, retirements));
        }

        [Fact]
        public void IsRewindRetired_RawOverload_ReturnsFalseForParentAnchoredChild()
        {
            // Raw overload (no recordings list) keeps its per-row contract so
            // EnsureRewindRetirementsForRollback's "seenIds" working set still
            // dedupes only direct rows being written.
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            Assert.False(EffectiveState.IsRewindRetired(child, retirements));
        }

        // =====================================================================
        // End-to-end through ComputeERS
        // =====================================================================

        [Fact]
        public void ComputeERS_RetiredParentCascade_OmitsOrphanDebrisChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var unrelated = Rec("rec_unrelated");

            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.AddCommittedInternal(child);
            RecordingStore.AddCommittedInternal(unrelated);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    Retire("rec_parent"),
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var ers = EffectiveState.ComputeERS();
            var visibleIds = ers.Select(r => r.RecordingId).ToList();

            Assert.DoesNotContain("rec_parent", visibleIds);
            Assert.DoesNotContain("rec_child", visibleIds);
            Assert.Contains("rec_unrelated", visibleIds);

            // ERS rebuild summary records the cascade-driven skip.
            Assert.Contains(logLines, l =>
                l.Contains("[ERS]") && l.Contains("skippedRewindRetired=2"));
        }

        // =====================================================================
        // Playtest-shape regression
        // =====================================================================

        [Fact]
        public void Cascade_PlaytestShape_HidesOrphanKerbalXDebrisChild()
        {
            // Mirrors the persistent.sfs shape from the 2026-05-19 playtest:
            // - rec_2c68978d retired via rrt_33919ead (rewound-out-supersede-fork).
            // - 3d4713df has parentAnchorRecordingId = rec_2c68978d, no retirement.
            // - ab1f54b0 (the restored recording) and its children stay visible.
            const string retiredFork = "rec_2c68978d84054474b804c579c92f5d40";
            const string orphanDebris = "3d4713df2ba449d99455de98db3085f4";
            const string restored = "ab1f54b089f54312b02add0aa049e156";
            const string restoredChild = "rec_0e69db2e1ea4428c913c9ad1d8da82d4";

            var recordings = new List<Recording>
            {
                Rec(retiredFork),
                Rec(orphanDebris, parentAnchorRecordingId: retiredFork),
                Rec(restored),
                Rec(restoredChild, parentAnchorRecordingId: restored),
            };
            var retirements = new List<RecordingRewindRetirement>
            {
                Retire(retiredFork, restored),
            };

            var retired = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);

            Assert.Contains(retiredFork, retired);
            Assert.Contains(orphanDebris, retired);
            Assert.DoesNotContain(restored, retired);
            Assert.DoesNotContain(restoredChild, retired);
        }

        // =====================================================================
        // Reversibility: removing the parent retirement reinstates the child.
        // Existing housekeeping paths (orphan cleanup, tree-discard purge,
        // legacy-Immutable load-time sweep) already remove retirement rows;
        // pin that no extra child-side cleanup is required.
        // =====================================================================

        // =====================================================================
        // Cache: live-store calls cache the cascade across version-stable
        // windows so per-frame consumers (ParsekKSC.Update per-rec,
        // RecordingsTableUI per-row, GhostMapPresence) do not pay the
        // fixed-point closure cost N times per frame AND do not re-emit the
        // Verbose cascade log on every call.
        // =====================================================================

        [Fact]
        public void LiveStoreCall_RepeatsCacheCascade_LogsOnceUntilVersionBump()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            RecordingStore.AddCommittedInternal(parent);
            RecordingStore.AddCommittedInternal(child);

            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>
                {
                    Retire("rec_parent"),
                },
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            // First call: cache miss -> compute path runs -> Verbose log fires.
            int logCountBefore = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            var first = EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            int logCountAfterFirst = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountBefore + 1, logCountAfterFirst);
            Assert.Contains("rec_child", first);

            // Repeat calls with the same versions: cache hit -> identical
            // HashSet reference returned, no new log lines.
            for (int i = 0; i < 5; i++)
            {
                var hit = EffectiveState.ComputeRewindRetiredRecordingIds(
                    RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
                Assert.Same(first, hit);
            }
            int logCountAfterRepeats = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountAfterFirst, logCountAfterRepeats);

            // Version bump invalidates: cascade recomputes and re-logs.
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            int logCountAfterBump = logLines.Count(l =>
                l.Contains("[ERS]") && l.Contains("Rewind-retirement cascade"));
            Assert.Equal(logCountAfterRepeats + 1, logCountAfterBump);
        }

        [Fact]
        public void AdHocCall_DoesNotPollLiveCache()
        {
            // Ad-hoc test-fixture call with private lists must not stash a
            // cascade into the live cache; otherwise a later live-store call
            // would hit a stale entry derived from the wrong recordings.
            var parent = Rec("rec_parent");
            RecordingStore.AddCommittedInternal(parent);
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                RecordingRewindRetirements = new List<RecordingRewindRetirement>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();

            var adHocRecordings = new List<Recording>
            {
                Rec("rec_adhoc_parent"),
                Rec("rec_adhoc_child", parentAnchorRecordingId: "rec_adhoc_parent"),
            };
            var adHocRetirements = new List<RecordingRewindRetirement>
            {
                Retire("rec_adhoc_parent"),
            };

            var adHocResult = EffectiveState.ComputeRewindRetiredRecordingIds(
                adHocRecordings, adHocRetirements);
            Assert.Contains("rec_adhoc_parent", adHocResult);
            Assert.Contains("rec_adhoc_child", adHocResult);

            // Live call must not see the ad-hoc result through the cache.
            var liveResult = EffectiveState.ComputeRewindRetiredRecordingIds(
                RecordingStore.CommittedRecordings, scenario.RecordingRewindRetirements);
            Assert.DoesNotContain("rec_adhoc_parent", liveResult);
            Assert.DoesNotContain("rec_adhoc_child", liveResult);
            Assert.Empty(liveResult);
        }

        [Fact]
        public void Reversibility_RemovingRetirement_ReinstatesChild()
        {
            var parent = Rec("rec_parent");
            var child = Rec("rec_child", parentAnchorRecordingId: "rec_parent");
            var recordings = new List<Recording> { parent, child };
            var retirements = new List<RecordingRewindRetirement> { Retire("rec_parent") };

            var retiredBefore = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            Assert.Contains("rec_child", retiredBefore);

            retirements.Clear();
            var retiredAfter = EffectiveState.ComputeRewindRetiredRecordingIds(recordings, retirements);
            Assert.Empty(retiredAfter);
        }
    }
}
