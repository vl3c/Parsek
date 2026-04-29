using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Phase 8 of Rewind-to-Staging (design §5.3 / §5.5 / §6.6 step 2-3 /
    /// §7.17 / §7.43 / §10.4): guards the merge-time supersede commit.
    ///
    /// <para>
    /// Covers the terminal-kind matrix (Landed -&gt; Immutable; Crashed
    /// -&gt; CommittedProvisional), the forward-only merge-guarded subtree
    /// walk (every descendant in the closure gets a supersede relation; a
    /// mixed-parent descendant is excluded), the transient
    /// <see cref="Recording.SupersedeTargetId"/> clear, the active-marker
    /// clear, the ERS cache invalidation via supersede state version bump,
    /// and the regular tree-merge fallback when no session is active.
    /// </para>
    /// </summary>
    [Collection("Sequential")]
    public class SupersedeCommitTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();
        private readonly bool priorParsekLogSuppress;
        private readonly bool priorStoreSuppress;

        public SupersedeCommitTests()
        {
            priorParsekLogSuppress = ParsekLog.SuppressLogging;
            priorStoreSuppress = RecordingStore.SuppressLogging;

            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = false;
            RecordingStore.SuppressLogging = true;
            ParsekLog.VerboseOverrideForTesting = true;
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);

            RecordingStore.ResetForTesting();
            Ledger.ResetForTesting();
            EffectiveState.ResetCachesForTesting();
            ParsekScenario.ResetInstanceForTesting();
            SessionSuppressionState.ResetForTesting();
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
            SessionSuppressionState.ResetForTesting();
        }

        // ---------- Helpers -------------------------------------------------

        private static Recording Rec(string id, string treeId,
            string parentBranchPointId = null, string childBranchPointId = null,
            MergeState state = MergeState.Immutable,
            TerminalState? terminal = null,
            string supersedeTargetId = null,
            uint vesselPid = 0)
        {
            return new Recording
            {
                RecordingId = id,
                VesselName = id,
                TreeId = treeId,
                MergeState = state,
                TerminalStateValue = terminal,
                ParentBranchPointId = parentBranchPointId,
                ChildBranchPointId = childBranchPointId,
                SupersedeTargetId = supersedeTargetId,
                VesselPersistentId = vesselPid,
            };
        }

        private static BranchPoint Bp(string id, BranchPointType type,
            List<string> parents = null, List<string> children = null,
            uint targetPid = 0)
        {
            return new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0.0,
                ParentRecordingIds = parents ?? new List<string>(),
                ChildRecordingIds = children ?? new List<string>(),
                TargetVesselPersistentId = targetPid,
            };
        }

        private static RecordingSupersedeRelation Rel(string oldId, string newId)
        {
            return new RecordingSupersedeRelation
            {
                RelationId = "rsr_" + oldId + "_" + newId,
                OldRecordingId = oldId,
                NewRecordingId = newId,
                UT = 0.0,
            };
        }

        private static void InstallTree(string treeId, List<Recording> recordings,
            List<BranchPoint> branchPoints)
        {
            var tree = new RecordingTree
            {
                Id = treeId,
                TreeName = "Test_" + treeId,
                BranchPoints = branchPoints ?? new List<BranchPoint>(),
            };
            foreach (var rec in recordings)
            {
                tree.AddOrReplaceRecording(rec);
                RecordingStore.AddRecordingWithTreeForTesting(rec, treeId);
            }
            var trees = RecordingStore.CommittedTrees;
            for (int i = trees.Count - 1; i >= 0; i--)
                if (trees[i].Id == treeId) trees.RemoveAt(i);
            trees.Add(tree);
        }

        private static ParsekScenario InstallScenario(ReFlySessionMarker marker)
        {
            var scenario = new ParsekScenario
            {
                RecordingSupersedes = new List<RecordingSupersedeRelation>(),
                LedgerTombstones = new List<LedgerTombstone>(),
                RewindPoints = new List<RewindPoint>(),
                ActiveReFlySessionMarker = marker,
            };
            ParsekScenario.SetInstanceForTesting(scenario);
            scenario.BumpSupersedeStateVersion();
            EffectiveState.ResetCachesForTesting();
            SessionSuppressionState.ResetForTesting();
            return scenario;
        }

        private static ReFlySessionMarker Marker(
            string originId, string provisionalId,
            string sessionId = "sess_1", string treeId = "tree_1",
            string supersedeTargetId = null)
        {
            return new ReFlySessionMarker
            {
                SessionId = sessionId,
                TreeId = treeId,
                ActiveReFlyRecordingId = provisionalId,
                OriginChildRecordingId = originId,
                SupersedeTargetId = supersedeTargetId,
                RewindPointId = "rp_1",
                InvokedUT = 0.0,
            };
        }

        // Fixture: origin (suppressed) + 1 descendant (suppressed) + unrelated
        // (outside). Mirrors SessionSuppressionWiringTests.InstallOriginClosureFixture.
        private void InstallOriginClosureFixture(
            string originId, string insideId, string outsideId,
            TerminalState? originTerminal = null, TerminalState? insideTerminal = null)
        {
            var origin = Rec(originId, "tree_1",
                childBranchPointId: "bp_c", terminal: originTerminal);
            var inside = Rec(insideId, "tree_1",
                parentBranchPointId: "bp_c", terminal: insideTerminal);
            var outside = Rec(outsideId, "tree_1");
            var bp_c = Bp("bp_c", BranchPointType.Undock,
                parents: new List<string> { originId },
                children: new List<string> { insideId });
            InstallTree("tree_1",
                new List<Recording> { origin, inside, outside },
                new List<BranchPoint> { bp_c });
        }

        private static Recording AddProvisional(string recordingId, string treeId,
            TerminalState? terminal, string supersedeTargetId)
        {
            var provisional = Rec(recordingId, treeId,
                state: MergeState.NotCommitted,
                terminal: terminal,
                supersedeTargetId: supersedeTargetId);
            // Satisfy SupersedeCommit.AppendRelations supersede-target
            // invariant (>=1 trajectory point + non-null terminal). Tests
            // exercising the empty / null-terminal cases construct
            // provisionals directly.
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            RecordingStore.AddRecordingWithTreeForTesting(provisional, treeId);
            return provisional;
        }

        // ---------- Terminal kind matrix -----------------------------------

        [Fact]
        public void LandedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=Immutable")
                && l.Contains("terminalKind=Landed"));
        }

        [Fact]
        public void CrashedTerminal_ProducesCommittedProvisional()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("terminalKind=Crashed"));
        }

        [Fact]
        public void SplashedTerminal_ProducesImmutable()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Splashed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
        }

        [Fact]
        public void OrbitingNonFocusStableLeaf_ProducesCommittedProvisional()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_focus", Controllable = true },
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_origin", Controllable = true },
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=CommittedProvisional")
                && l.Contains("classifierReason=stableLeafUnconcluded"));
        }

        [Fact]
        public void OrbitingFocusStableLeaf_ProducesImmutable()
        {
            const string bpId = "bp_stage";
            var origin = Rec("rec_origin", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = bpId;
            var marker = Marker("rec_origin", "rec_provisional");
            var scenario = InstallScenario(marker);
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_1",
                BranchPointId = bpId,
                FocusSlotIndex = 1,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot { SlotIndex = 0, OriginChildRecordingId = "rec_other", Controllable = true },
                    new ChildSlot { SlotIndex = 1, OriginChildRecordingId = "rec_origin", Controllable = true },
                }
            });

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.Immutable, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("mergeState=Immutable")
                && l.Contains("classifierReason=stableTerminalFocusSlot"));
        }

        [Fact]
        public void OrbitingStableLeaf_SlotLookupFailure_ThrowsInsteadOfFallback()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Orbiting, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.CommitSupersede(
                    scenario.ActiveReFlySessionMarker, provisional));

            Assert.Contains("Site B-1 slot lookup failed", ex.Message);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("aborting because stable-leaf classification cannot safely fall back"));
        }

        [Fact]
        public void ChainTipOrbitingStableLeaf_SlotLookupFailure_ThrowsInsteadOfFallback()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional_head", "tree_1",
                null, supersedeTargetId: "rec_origin");
            provisional.ParentBranchPointId = "bp_missing";
            provisional.ChainId = "chain_stable";
            provisional.ChainIndex = 0;
            var tip = Rec("rec_provisional_tip", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Orbiting);
            tip.ChainId = "chain_stable";
            tip.ChainIndex = 1;
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_1");
            var tree = RecordingStore.CommittedTrees.Single(t => t.Id == "tree_1");
            tree.AddOrReplaceRecording(provisional);
            tree.AddOrReplaceRecording(tip);
            var marker = Marker("rec_origin", "rec_provisional_head");
            var scenario = InstallScenario(marker);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.FlipMergeStateAndClearTransient(
                    marker, provisional, scenario, preserveMarker: false));

            Assert.Contains("Site B-1 slot lookup failed", ex.Message);
            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Site B-1 slot lookup failed")
                && l.Contains("terminal=Orbiting")
                && l.Contains("aborting because stable-leaf classification cannot safely fall back"));
        }

        // ---------- Subtree supersede ---------------------------------------

        [Fact]
        public void SubtreeSupersede_AllDescendantsGetRelations()
        {
            // origin -> inside1 -> inside2 linear chain; every id in the
            // closure must produce one supersede relation pointing at the
            // provisional.
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_1");
            var inside1 = Rec("rec_inside1", "tree_1",
                parentBranchPointId: "bp_1", childBranchPointId: "bp_2");
            var inside2 = Rec("rec_inside2", "tree_1",
                parentBranchPointId: "bp_2");
            var bp1 = Bp("bp_1", BranchPointType.Undock,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_inside1" });
            var bp2 = Bp("bp_2", BranchPointType.Undock,
                parents: new List<string> { "rec_inside1" },
                children: new List<string> { "rec_inside2" });
            InstallTree("tree_1",
                new List<Recording> { origin, inside1, inside2 },
                new List<BranchPoint> { bp1, bp2 });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            var rels = scenario.RecordingSupersedes;
            Assert.Equal(3, rels.Count);

            // Every relation points at the provisional.
            foreach (var rel in rels)
                Assert.Equal("rec_provisional", rel.NewRecordingId);

            var oldIds = new HashSet<string>(rels.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.Contains("rec_inside1", oldIds);
            Assert.Contains("rec_inside2", oldIds);

            // Log assertions.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("rel=")
                && l.Contains("old=rec_origin") && l.Contains("new=rec_provisional"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("Added 3 supersede relations")
                && l.Contains("rooted at rec_origin"));
        }

        [Fact]
        public void MixedParentDescendant_NotIncluded()
        {
            // Two roots: rec_origin (in subtree) + rec_other (outside).
            // bp_c has BOTH as parents (mixed) so the walk must halt before
            // adding rec_inside to the closure (§7.40).
            var origin = Rec("rec_origin", "tree_1", childBranchPointId: "bp_c");
            var other = Rec("rec_other", "tree_1", childBranchPointId: "bp_c");
            var inside = Rec("rec_inside", "tree_1", parentBranchPointId: "bp_c");
            var bp_c = Bp("bp_c", BranchPointType.Dock,
                parents: new List<string> { "rec_origin", "rec_other" },
                children: new List<string> { "rec_inside" });
            InstallTree("tree_1",
                new List<Recording> { origin, other, inside },
                new List<BranchPoint> { bp_c });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Only the origin is superseded; rec_inside is a mixed-parent
            // halt and must NOT be in the relation list.
            var oldIds = new HashSet<string>(
                scenario.RecordingSupersedes.Select(r => r.OldRecordingId));
            Assert.Contains("rec_origin", oldIds);
            Assert.DoesNotContain("rec_inside", oldIds);
            Assert.DoesNotContain("rec_other", oldIds);
        }

        // ---------- Chain extension / Unfinished Flight ----------------------

        [Fact]
        public void ChainExtendsThroughCrashedReFly()
        {
            // The crashed provisional commits as CommittedProvisional so the
            // slot-level Unfinished Flights predicate can keep a real RP slot
            // open. This fixture predates slot resolution and only guards the
            // commit-state / visibility half of the chain-extension behavior.
            var origin = Rec("rec_origin", "tree_1",
                parentBranchPointId: "bp_parent",
                terminal: TerminalState.Destroyed);
            var bp_parent = Bp("bp_parent", BranchPointType.Undock,
                parents: new List<string> { "rec_root" },
                children: new List<string> { "rec_origin" });
            InstallTree("tree_1",
                new List<Recording> { origin },
                new List<BranchPoint> { bp_parent });
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_origin");

            var rps = new List<RewindPoint>
            {
                new RewindPoint
                {
                    RewindPointId = "rp_1",
                    BranchPointId = "bp_parent",
                    ChildSlots = new List<ChildSlot>(),
                },
            };
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            scenario.RewindPoints = rps;

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            Assert.Equal(MergeState.CommittedProvisional, provisional.MergeState);

            // The §7.43 chain-extension behavior is that the provisional stays
            // visible and committed-ish; separate tests cover slot-level UF
            // membership once a RewindPoint child slot exists.
            var provisionalVisible = EffectiveState.IsVisible(provisional, scenario.RecordingSupersedes);
            Assert.True(provisionalVisible,
                "provisional must be visible in ERS after commit (nothing supersedes it)");

            // Origin is now superseded → NOT visible.
            Assert.False(EffectiveState.IsVisible(
                origin, scenario.RecordingSupersedes));
        }

        [Fact]
        public void AppendRelations_ChainExtension_RootsAtSupersedeTargetPriorTip()
        {
            var origin = Rec("rec_origin", "tree_1");
            var priorTip = Rec("rec_refly1", "tree_1");
            InstallTree("tree_1",
                new List<Recording> { origin, priorTip },
                new List<BranchPoint>());
            var provisional = AddProvisional("rec_refly2", "tree_1",
                TerminalState.Destroyed, supersedeTargetId: "rec_refly1");
            var marker = Marker("rec_origin", "rec_refly2",
                supersedeTargetId: "rec_refly1");
            var scenario = InstallScenario(marker);

            SupersedeCommit.AppendRelations(marker, provisional, scenario);

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_refly1"
                    && r.NewRecordingId == "rec_refly2");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin"
                    && r.NewRecordingId == "rec_refly2");
            Assert.Equal("rec_refly2", EffectiveState.EffectiveRecordingId(
                "rec_origin",
                new List<RecordingSupersedeRelation>
                {
                    Rel("rec_origin", "rec_refly1"),
                    scenario.RecordingSupersedes[0],
                }));
        }

        [Fact]
        public void HybridStarAndLinearGraph_ResolvesDominantTipAndAllSlotTrails()
        {
            const string bpId = "bp_probe_split";
            var origin = Rec("probeOrig", "tree_probe", parentBranchPointId: bpId);
            var reFly1 = Rec("probeReFly1", "tree_probe", parentBranchPointId: bpId);
            var reFly2 = Rec("probeReFly2", "tree_probe", parentBranchPointId: bpId);
            InstallTree("tree_probe",
                new List<Recording> { origin, reFly1, reFly2 },
                new List<BranchPoint>());
            var reFly3 = AddProvisional("probeReFly3", "tree_probe",
                TerminalState.Destroyed, supersedeTargetId: "probeReFly1");
            reFly3.ParentBranchPointId = bpId;

            var marker = Marker("probeOrig", "probeReFly3",
                treeId: "tree_probe",
                supersedeTargetId: "probeReFly1");
            var scenario = InstallScenario(marker);
            scenario.RecordingSupersedes.Add(Rel("probeOrig", "probeReFly1"));
            scenario.RecordingSupersedes.Add(Rel("probeOrig", "probeReFly2"));
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_probe",
                BranchPointId = bpId,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "probeOrig",
                        Controllable = true,
                    },
                },
            });

            SupersedeCommit.AppendRelations(marker, reFly3, scenario);

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "probeReFly1"
                    && r.NewRecordingId == "probeReFly3");
            Assert.Equal("probeReFly3", EffectiveState.EffectiveRecordingId(
                "probeOrig", scenario.RecordingSupersedes));

            Assert.True(RecordingsTableUI.TryResolveRewindPointForRecording(
                reFly3, out var rpForNewTip, out int slotForNewTip));
            Assert.Same(scenario.RewindPoints[0], rpForNewTip);
            Assert.Equal(0, slotForNewTip);

            Assert.True(RecordingsTableUI.TryResolveRewindPointForRecording(
                reFly2, out var rpForOrphanBranch, out int slotForOrphanBranch));
            Assert.Same(scenario.RewindPoints[0], rpForOrphanBranch);
            Assert.Equal(0, slotForOrphanBranch);
        }

        // ---------- Transient fields / marker cleanup ----------------------

        [Fact]
        public void MergeState_Clears_SupersedeTargetId()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.Equal("rec_origin", provisional.SupersedeTargetId);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(provisional.SupersedeTargetId);
        }

        [Fact]
        public void Commit_ClearsActiveMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            Assert.Null(scenario.ActiveReFlySessionMarker);

            // §10.4 End reason=merged log.
            Assert.Contains(logLines, l =>
                l.Contains("[ReFlySession]") && l.Contains("End reason=merged")
                && l.Contains("sess=sess_1"));
        }

        [Fact]
        public void Commit_BumpsStateVersion()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int versionBefore = scenario.SupersedeStateVersion;
            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);
            int versionAfter = scenario.SupersedeStateVersion;

            Assert.NotEqual(versionBefore, versionAfter);
        }

        [Fact]
        public void Commit_InvalidatesErsCache_OriginVanishes()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside",
                originTerminal: TerminalState.Destroyed);
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            // Before commit: origin is session-suppressed (filtered out of ERS
            // by the marker), inside also suppressed, outside + provisional
            // visible.
            var ersBefore = EffectiveState.ComputeERS();
            var idsBefore = new HashSet<string>(ersBefore.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsBefore);
            Assert.Contains("rec_outside", idsBefore);

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // After commit: origin is superseded (filtered out of ERS by
            // supersede relations), not session-suppressed. The ERS cache
            // must rebuild — origin must still be absent; provisional must
            // be present.
            var ersAfter = EffectiveState.ComputeERS();
            var idsAfter = new HashSet<string>(ersAfter.Select(r => r.RecordingId));
            Assert.DoesNotContain("rec_origin", idsAfter);
            Assert.DoesNotContain("rec_inside", idsAfter);
            Assert.Contains("rec_outside", idsAfter);
            Assert.Contains("rec_provisional", idsAfter);
        }

        // ---------- Idempotence + edge cases --------------------------------

        [Fact]
        public void Commit_IsIdempotent_ReinvocationSkipsExistingRelations()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));
            var marker = scenario.ActiveReFlySessionMarker;

            SupersedeCommit.CommitSupersede(marker, provisional);
            int firstCount = scenario.RecordingSupersedes.Count;

            // Reset the marker and call again with the same provisional. Commit
            // must be a no-op for the relations that already exist (defensive
            // idempotence per Phase 8 scope).
            scenario.ActiveReFlySessionMarker = marker;
            SupersedeCommit.CommitSupersede(marker, provisional);
            int secondCount = scenario.RecordingSupersedes.Count;

            Assert.Equal(firstCount, secondCount);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("skip existing relation"));
        }

        [Fact]
        public void NoActiveMarker_SkipsSupersede_RegularTreeCommit()
        {
            // No scenario-installed marker. TryCommitReFlySupersede invoked from
            // MergeDialog.MergeCommit should short-circuit and leave the
            // supersede list untouched.
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(marker: null);

            MergeDialog.TryCommitReFlySupersede();

            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("no active re-fly session marker"));
        }

        [Fact]
        public void TryCommitReFlySupersede_NoProvisionalRecording_WarnsAndKeepsMarker()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var scenario = InstallScenario(
                Marker("rec_origin", "rec_ghost_provisional"));

            // Provisional recording id does not exist in the store.
            MergeDialog.TryCommitReFlySupersede();

            // Marker stays in place so the Phase 13 load-time sweep can clean
            // it up deterministically.
            Assert.NotNull(scenario.ActiveReFlySessionMarker);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") && l.Contains("not found in committed list"));
        }

        [Fact]
        public void NullMarker_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            InstallScenario(marker: null);

            // Direct call with null marker → safe no-op + warn.
            SupersedeCommit.CommitSupersede(null, provisional);

            Assert.Equal(MergeState.NotCommitted, provisional.MergeState);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("marker is null"));
        }

        [Fact]
        public void NullProvisional_Commit_IsNoOp()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            InstallScenario(Marker("rec_origin", "rec_provisional"));

            SupersedeCommit.CommitSupersede(
                ParsekScenario.Instance.ActiveReFlySessionMarker, null);

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]") && l.Contains("provisional is null"));
        }

        // ---------- Self-supersede guards (bug/rewind-self-supersede-and-followups) -----

        /// <summary>
        /// Regression: Limbo-restore kept the origin recording alive across an
        /// RP-quicksave reload, the re-fly continued writing into that SAME
        /// recording. Item 11 moved the in-place detection up to
        /// <see cref="RewindInvoker.AtomicMarkerWrite"/> so the marker now
        /// points directly at the origin id with no placeholder; at merge
        /// time <c>provisional.RecordingId == marker.OriginChildRecordingId</c>
        /// is the natural state, not a redirect outcome. The caller-side
        /// guard in <see cref="MergeDialog.TryCommitReFlySupersede"/> must
        /// detect the in-place-continuation case, skip the journaled merge
        /// entirely (no self-supersede row), flip MergeState, clear the
        /// marker, and return Completed.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_SkipsMergeAndFinalizes()
        {
            // Build a tree where the origin recording is itself the recording
            // that the re-fly continues writing into. The marker's origin and
            // active pointers are the same id; no separate provisional exists.
            var origin = Rec("rec_origin", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: null);
            // Non-empty trajectory satisfies the supersede-target invariant
            // (item 10) on the in-place continuation path's defensive
            // ValidateSupersedeTarget call, even though that invariant is
            // not reached because the in-place guard short-circuits first.
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_origin", provisionalId: "rec_origin");
            var scenario = InstallScenario(marker);

            int versionBefore = scenario.SupersedeStateVersion;
            var result = MergeDialog.TryCommitReFlySupersede();

            // 1) No supersede row written.
            Assert.Empty(scenario.RecordingSupersedes);
            // 2) Marker cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // 3) MergeState flipped to Immutable (Landed terminal).
            Assert.Equal(MergeState.Immutable, origin.MergeState);
            // 4) Result is Completed.
            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            // 5) Supersede state version bumped.
            Assert.NotEqual(versionBefore, scenario.SupersedeStateVersion);
            // 6) INFO log advertises the in-place-continuation diagnosis.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation")
                && l.Contains("rec_origin"));
        }

        /// <summary>
        /// After an in-place continuation merge, the recording's RP must be
        /// reaped so the row stops satisfying <see cref="EffectiveState.IsUnfinishedFlight"/>
        /// (terminal=Destroyed AND matching RP). Without the reap, the row
        /// stays duplicated in the Unfinished Flights virtual group even
        /// though the player has already "committed" the re-flight by
        /// merging — observed in the 10:47 playtest. Reap must run even
        /// when the re-fly itself crashed (terminal stays Destroyed).
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_ReapsRpAndPromotesOutOfUnfinishedFlights()
        {
            const string kBpId = "bp_breakup_test";
            var origin = Rec("rec_origin", "tree_1",
                parentBranchPointId: kBpId,
                state: MergeState.CommittedProvisional,
                terminal: TerminalState.Destroyed);
            // Non-empty trajectory satisfies the supersede-target invariant
            // in case the in-place guard ever falls through.
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = kBpId,
                        Type = BranchPointType.Breakup,
                        UT = 0.0,
                        ChildRecordingIds = new List<string> { "rec_origin" },
                    },
                },
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_origin", provisionalId: "rec_origin");
            var scenario = InstallScenario(marker);
            // Seed the RP that pins the recording into the Unfinished Flights
            // group: it shares its BranchPointId with the recording's
            // ParentBranchPointId, and its only slot points at the origin id.
            // SessionProvisional=false so the reaper considers it (session-
            // provisional RPs are reaped via a different code path).
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_test_uf_reap",
                BranchPointId = kBpId,
                UT = 0.0,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_origin",
                    },
                },
                SessionProvisional = false,
            });

            // Pre-merge: the recording IS an Unfinished Flight.
            Assert.True(EffectiveState.IsUnfinishedFlight(origin),
                "expected origin recording to satisfy IsUnfinishedFlight pre-merge " +
                "(terminal=Destroyed + matching RP via ParentBranchPointId)");
            Assert.Single(scenario.RewindPoints);

            // Avoid touching the disk when the in-place merge calls
            // RewindPointReaper to delete the quicksave file.
            int deletes = 0;
            RewindPointReaper.DeleteQuicksaveForTesting = _ =>
            {
                deletes++;
                return true;
            };
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            // Post-merge:
            // 1) The RP is reaped, so the recording no longer matches an RP
            //    even though terminal stays Destroyed.
            Assert.Empty(scenario.RewindPoints);
            // 2) IsUnfinishedFlight returns false — promoted out of UF.
            Assert.False(EffectiveState.IsUnfinishedFlight(origin),
                "expected origin recording to drop out of IsUnfinishedFlight post-merge " +
                "(RP reaped, no matching slot)");
            // 3) MergeState flipped to Immutable (Destroyed terminal).
            Assert.Equal(MergeState.Immutable, origin.MergeState);
            // 4) Marker cleared.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            // 5) Reaper attempted to delete the quicksave file.
            Assert.Equal(1, deletes);
            // 6) INFO log advertises the reap count.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation reaped 1 orphaned RP")
                && l.Contains("post-merge"));
        }

        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_OrbitingNonFocus_ForcedImmutable_DoesNotSealSlot()
        {
            const string kBpId = "bp_inplace_stable_leaf";
            var origin = Rec("rec_probe", "tree_1",
                parentBranchPointId: kBpId,
                state: MergeState.NotCommitted,
                terminal: TerminalState.Orbiting);
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = kBpId,
                        Type = BranchPointType.Breakup,
                        UT = 0.0,
                        ChildRecordingIds = new List<string> { "rec_probe" },
                    },
                },
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_probe", provisionalId: "rec_probe");
            var scenario = InstallScenario(marker);
            var probeSlot = new ChildSlot
            {
                SlotIndex = 1,
                OriginChildRecordingId = "rec_probe",
                Controllable = true,
            };
            scenario.RewindPoints.Add(new RewindPoint
            {
                RewindPointId = "rp_inplace_stable_leaf",
                BranchPointId = kBpId,
                UT = 0.0,
                FocusSlotIndex = 0,
                SessionProvisional = false,
                ChildSlots = new List<ChildSlot>
                {
                    new ChildSlot
                    {
                        SlotIndex = 0,
                        OriginChildRecordingId = "rec_focus",
                        Controllable = true,
                    },
                    probeSlot,
                },
            });

            int deletes = 0;
            RewindPointReaper.DeleteQuicksaveForTesting = _ =>
            {
                deletes++;
                return true;
            };
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            Assert.Equal(MergeState.Immutable, origin.MergeState);
            Assert.False(probeSlot.Sealed);
            Assert.Empty(scenario.RewindPoints);
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(1, deletes);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("classifierReason=stableLeafUnconcluded")
                && l.Contains("mergeState=CommittedProvisional"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation forced")
                && l.Contains("CommittedProvisional")
                && l.Contains("Immutable"));
        }

        // The runtime `old==new` self-skip defense in
        // `SupersedeCommit.AppendRelations` was removed when the placeholder-
        // and-redirect cascade was retired (item 11), but PR #590 re-introduced
        // it to support the in-place-continuation `AppendRelations` call path:
        // when `marker.OriginChildRecordingId == provisional.RecordingId`, the
        // session-suppressed-subtree closure includes the origin itself, and a
        // row where `old == new` would form a 1-node `EffectiveRecordingId`
        // cycle. The 4-arg overload added an `extraSelfSkipRecordingIds`
        // parameter for the optimizer-split case where the in-place provisional
        // has been split into chain HEAD + TIP (and the three-segment variant
        // where HEAD/MIDDLE/TIP are all part of the new flight): the caller
        // passes the TIP as `provisional` so `ValidateSupersedeTarget` sees a
        // non-null terminal payload, and names the other chain members in
        // `extraSelfSkipRecordingIds` so none of them ends up with a row
        // pointing at another member. The runtime self-skip guard is exercised
        // by `TryCommitReFlySupersede_InPlaceContinuation_LoneOrigin_FiltersSelfLinkOnly`;
        // the extra-self-skip set is exercised by
        // `TryCommitReFlySupersede_InPlaceContinuation_OptimizerSplit_ResolvesChainTipAndWritesSiblingRows`
        // and
        // `TryCommitReFlySupersede_InPlaceContinuation_ThreeSegmentChain_NoMemberSupersededByAnotherMember`.

        // ---------- Supersede-target invariant (item 10) -------------------

        [Fact]
        public void AppendRelations_EmptyProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: "rec_origin");
            Assert.Empty(provisional.Points);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("empty Points", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=empty Points")
                && l.Contains("refusing to write supersede rows"));
        }

        [Fact]
        public void AppendRelations_NullTerminalProvisional_RefusesAndWarns()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = Rec("rec_provisional", "tree_1",
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: "rec_origin");
            provisional.Points.Add(new TrajectoryPoint { ut = 0.0 });
            provisional.Points.Add(new TrajectoryPoint { ut = 1.0 });
            Assert.Null(provisional.TerminalStateValue);
            RecordingStore.AddRecordingWithTreeForTesting(provisional, "tree_1");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;

#if DEBUG
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SupersedeCommit.AppendRelations(
                    scenario.ActiveReFlySessionMarker, provisional, scenario));
            Assert.Contains("invariant violation", ex.Message);
            Assert.Contains("null TerminalState", ex.Message);
#else
            var subtree = SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            Assert.Empty(subtree);
#endif

            Assert.Equal(countBefore, scenario.RecordingSupersedes.Count);
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations invariant violation")
                && l.Contains("provisional=rec_provisional")
                && l.Contains("reason=null TerminalState")
                && l.Contains("refusing to write supersede rows"));
        }

        // ---------- Chain-sibling expansion (item 23) ----------------------

        [Fact]
        public void AppendRelations_ChainHeadOrigin_WritesSupersedeRowPerSegment()
        {
            // Merge-time RecordingOptimizer.SplitAtSection produces a HEAD
            // (BP-linked, ChildBranchPointId=null after the move at
            // RecordingStore.cs:2018-2019) + TIP (terminal=Destroyed) chain
            // sharing both ChainId and ChainBranch. Marker points at the
            // HEAD. AppendRelations must write a supersede row for BOTH
            // segments so the TIP doesn't survive the merge as a stale
            // "kerbal destroyed in atmo" row alongside the new "kerbal
            // lived" provisional.
            var head = Rec("rec_head", "tree_1", parentBranchPointId: "bp_split");
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            var tip = Rec("rec_tip", "tree_1");
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.TerminalStateValue = TerminalState.Destroyed;

            var bp_split = Bp("bp_split", BranchPointType.EVA,
                parents: new List<string> { "rec_parent" },
                children: new List<string> { "rec_head" });

            InstallTree("tree_1",
                new List<Recording> { head, tip },
                new List<BranchPoint> { bp_split });

            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_head");
            var scenario = InstallScenario(Marker("rec_head", "rec_provisional"));

            SupersedeCommit.CommitSupersede(scenario.ActiveReFlySessionMarker, provisional);

            // Both chain segments must have a supersede row pointing at the
            // provisional. Neither row should be missed and the TIP must not
            // be left visible as an orphan in ERS.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_head" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_tip" && r.NewRecordingId == "rec_provisional");
        }

        // ---------- In-place continuation supersede append (bug fix) ------
        // Bug fix (in-place-supersede): when the merge is an "in-place
        // continuation" (provisional.RecordingId == origin), the prior code
        // skipped AppendRelations entirely, so chain siblings / parent
        // recordings inside the suppressed subtree never got supersede rows
        // and stayed visible after merge. The fix calls AppendRelations on
        // the in-place path too, relying on the restored old==new self-skip
        // inside AppendRelations to filter the trivial self-link.

        /// <summary>
        /// In-place continuation with sibling chain segments: AppendRelations
        /// must write rows for the siblings (including a destroyed-final-state
        /// segment) while skipping the trivial self-link for the origin. The
        /// MergeState flip + marker clear behaviour is unchanged.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_AppendsSupersedeRowsForSiblings()
        {
            // Build a chain-shaped suppressed subtree:
            //   parentBP --> head (ChainId=A, branch=0, idx=0; in-place target)
            //                 |
            //                 + sibling tip (ChainId=A, branch=0, idx=1; terminal=Destroyed,
            //                                represents the optimizer-split TIP carrying
            //                                terminal payload + Points)
            //   bp_parent --child--> rec_other_sib (separate parent BP child;
            //                                       prior-attempt sibling whose
            //                                       supersede row IS the one we
            //                                       care about — the playtest's
            //                                       "Kerbal X Probe" pattern)
            // The marker points at the head (origin == provisional == head).
            // ResolveChainTerminalRecording walks to the tip, AppendRelations
            // validates against the tip's terminal, and the prior-attempt
            // sibling gets a row pointing at the tip while both head and tip
            // self-links are filtered.
            const string kBpId = "bp_parent_for_inplace";
            const string kBpChild = "bp_child_under_head";
            var head = Rec("rec_head", "tree_1",
                parentBranchPointId: kBpId,
                childBranchPointId: kBpChild,
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_a";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            // Head has Points (it's still half a trajectory) but no terminal
            // — that's the post-optimizer-split state SplitAtSection leaves.
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            // Tip carries the terminal payload + non-empty Points (must
            // satisfy ValidateSupersedeTarget once it becomes the resolved
            // supersede target).
            var tip = Rec("rec_tip", "tree_1",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            tip.ChainId = "chain_a";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.Points.Add(new TrajectoryPoint { ut = 1.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 2.0 });

            // Prior-attempt sibling under bp_child_under_head — the row we
            // need written for the original Bug 2 fix to actually land.
            var otherSibling = Rec("rec_other_sib", "tree_1",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            otherSibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            RecordingStore.AddRecordingWithTreeForTesting(head, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_1");
            RecordingStore.AddRecordingWithTreeForTesting(otherSibling, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = kBpId,
                        Type = BranchPointType.Breakup,
                        UT = 0.0,
                        ChildRecordingIds = new List<string> { "rec_head" },
                    },
                    new BranchPoint
                    {
                        Id = kBpChild,
                        Type = BranchPointType.Breakup,
                        UT = 1.0,
                        ParentRecordingIds = new List<string> { "rec_head" },
                        ChildRecordingIds = new List<string> { "rec_other_sib" },
                    },
                },
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            tree.AddOrReplaceRecording(otherSibling);
            RecordingStore.CommittedTrees.Add(tree);

            // origin == provisional == "rec_head"
            var marker = Marker(originId: "rec_head", provisionalId: "rec_head");
            var scenario = InstallScenario(marker);

            // Stub the quicksave file delete so the reaper inside the in-place
            // path doesn't try to touch disk.
            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            // Bug 2 assertion: prior-attempt sibling got a supersede row
            // pointing at the resolved chain tip. Without the in-place
            // AppendRelations fix the row would be missing entirely; without
            // the chain-tip resolve fix the row would not be written because
            // the head's null-terminal would fail ValidateSupersedeTarget.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_other_sib" && r.NewRecordingId == "rec_tip");
            // Neither HEAD nor TIP gets a row (both halves of the in-place
            // chain are part of the new flight).
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_tip");

            // Marker cleared, MergeState flipped to Immutable on the head
            // (in-place force-Immutable rule), tip stays default Immutable.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, head.MergeState);

            // Log assertions: in-place append summary log fires; chain-tip
            // resolve diagnostic fires; tip self-link skip + head extra-skip
            // both fire; summary reports both skip counts.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation supersede append")
                && l.Contains("wrote 1 relation"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("resolved chain tip for supersede target")
                && l.Contains("head=rec_head")
                && l.Contains("tip=rec_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_tip")
                && l.Contains("new=rec_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_head")
                && l.Contains("new=rec_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 1 supersede relations")
                && l.Contains("skippedSelfLink=1")
                && l.Contains("skippedExtraSelfLink=1"));
        }

        /// <summary>
        /// In-place continuation where the closure is exactly the origin (no
        /// chain siblings or BP descendants): AppendRelations finds only the
        /// origin self-link, skips it via the old==new guard, and the
        /// supersede list stays empty. The merge still completes; this case
        /// matches the existing
        /// <c>TryCommitReFlySupersede_InPlaceContinuation_SkipsMergeAndFinalizes</c>
        /// fixture but locks in the new "self-link skipped" log evidence
        /// emitted by the restored guard.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_LoneOrigin_FiltersSelfLinkOnly()
        {
            var origin = Rec("rec_origin", "tree_1",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: null);
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_1");
            var tree = new RecordingTree
            {
                Id = "tree_1",
                TreeName = "Test_tree_1",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_origin", provisionalId: "rec_origin");
            var scenario = InstallScenario(marker);

            var result = MergeDialog.TryCommitReFlySupersede();
            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);

            // The lone-origin closure means the only candidate is the
            // self-link, which is skipped: list stays empty.
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Equal(MergeState.Immutable, origin.MergeState);

            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_origin")
                && l.Contains("new=rec_origin"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation supersede append")
                && l.Contains("wrote 0 relation"));
        }

        /// <summary>
        /// AppendRelations self-link guard: when the subtree closure contains
        /// the provisional's own id (which happens whenever
        /// origin == provisional, i.e. in-place continuation), the row is
        /// skipped instead of producing a 1-node cycle in EffectiveRecordingId.
        /// Other ids in the closure still get rows. Direct
        /// AppendRelations call so this test is independent of the
        /// MergeDialog wiring above.
        /// </summary>
        [Fact]
        public void AppendRelations_SelfLinkSkipped_OtherSubtreeIdsStillWriteRows()
        {
            // Closure: head (origin == provisional) + tip (chain sibling).
            const string kBpId = "bp_self_test";
            var head = Rec("rec_self_head", "tree_self",
                parentBranchPointId: kBpId,
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed);
            head.ChainId = "chain_self";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var tip = Rec("rec_self_tip", "tree_self",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            tip.ChainId = "chain_self";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;

            RecordingStore.AddRecordingWithTreeForTesting(head, "tree_self");
            RecordingStore.AddRecordingWithTreeForTesting(tip, "tree_self");
            var tree = new RecordingTree
            {
                Id = "tree_self",
                TreeName = "tree_self",
                BranchPoints = new List<BranchPoint>
                {
                    new BranchPoint
                    {
                        Id = kBpId,
                        Type = BranchPointType.Breakup,
                        UT = 0.0,
                        ChildRecordingIds = new List<string> { "rec_self_head" },
                    },
                },
            };
            tree.AddOrReplaceRecording(head);
            tree.AddOrReplaceRecording(tip);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_self_head", provisionalId: "rec_self_head",
                treeId: "tree_self");
            var scenario = InstallScenario(marker);

            int countBefore = scenario.RecordingSupersedes.Count;
            var subtree = SupersedeCommit.AppendRelations(marker, head, scenario);

            // Closure includes both head and tip; head is filtered as
            // self-link, tip becomes a row.
            Assert.Contains("rec_self_head", subtree);
            Assert.Contains("rec_self_tip", subtree);
            int countAfter = scenario.RecordingSupersedes.Count;
            Assert.Equal(countBefore + 1, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_self_tip" && r.NewRecordingId == "rec_self_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_self_head" && r.NewRecordingId == "rec_self_head");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_self_head")
                && l.Contains("new=rec_self_head"));
        }

        // ---------- Optimizer-split chain-tip resolve (review follow-up) --
        // Bug fix (review follow-up to in-place-supersede): MergeCommit runs
        // RecordingStore.RunOptimizationPass() BEFORE TryCommitReFlySupersede.
        // If the in-place provisional crossed an env boundary, the optimizer's
        // SplitAtSection moves VesselSnapshot + TerminalStateValue from the
        // HEAD to a fresh TIP recording (RecordingOptimizer.cs lines 513-514
        // and 536-537). The HEAD then has TerminalStateValue == null, which
        // fails ValidateSupersedeTarget's null-terminal clause inside
        // AppendRelations: throws in DEBUG, returns empty in RELEASE — and
        // the sibling supersede rows the in-place fix needs are NOT written.
        // The fix uses EffectiveState.ResolveChainTerminalRecording to find
        // the TIP, passes it to AppendRelations as the validated target,
        // and adds the HEAD's id to extraSelfSkipRecordingIds so neither
        // the HEAD self-link nor the TIP self-link write a row (both halves
        // of the in-place chain are part of the new flight).

        /// <summary>
        /// In-place continuation where MergeCommit's optimizer pass has split
        /// the head into HEAD + TIP. The HEAD has no terminal; the TIP has
        /// the Destroyed terminal. Sibling rec_old_sib is the prior attempt's
        /// sibling that DOES need a supersede row. Without the chain-tip
        /// resolve fix, AppendRelations would refuse on HEAD's null terminal
        /// and rec_old_sib would stay visible after merge.
        ///
        /// <para>
        /// Mirrors a scenario that actually composes through the live
        /// MergeDialog wiring (the rewritten
        /// <c>TryCommitReFlySupersede_InPlaceContinuation_AppendsSupersedeRowsForSiblings</c>
        /// covers the same shape via a slightly different fixture); keep
        /// both because the second variant catches different combinations of
        /// closure + chain ordering and also pins the supersede ID match
        /// when the marker / tree wiring lives under separate keys.
        /// </para>
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_OptimizerSplit_ResolvesChainTipAndWritesSiblingRows()
        {
            // Tree shape:
            //   bp_parent --child--> rec_origin (HEAD, in-place provisional, ChainId=chain_inplace, idx=0,
            //                                    no terminal post-split, no snapshot post-split)
            //                                    |
            //                                    + chain sibling: rec_origin_tip (chain_inplace idx=1,
            //                                                                     Destroyed terminal,
            //                                                                     has Points)
            //   bp_parent --child--> rec_old_sib (prior attempt's sibling, NotCommitted-or-Immutable,
            //                                     Destroyed, separate ChainId or none)
            //
            // Marker points at HEAD = rec_origin. Closure walks via the
            // bp_parent BranchPoint AND chain expansion.
            const string kBpParent = "bp_parent_split";
            const string kBpChild = "bp_child_for_origin";
            // HEAD: no terminal, no snapshot — what the optimizer left after
            // moving them to the TIP.
            var head = Rec("rec_origin", "tree_split",
                parentBranchPointId: kBpParent,
                childBranchPointId: kBpChild,
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: null);
            head.ChainId = "chain_inplace";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            // TIP: terminal + snapshot live here post-split. Same chain,
            // same tree, idx=1.
            var tip = Rec("rec_origin_tip", "tree_split",
                parentBranchPointId: null,
                childBranchPointId: null,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            tip.ChainId = "chain_inplace";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.Points.Add(new TrajectoryPoint { ut = 1.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 2.0 });

            // Old sibling from the prior attempt — sibling under bp_child_for_origin
            // so it appears in the closure's BP-walk descendants.
            var oldSibling = Rec("rec_old_sib", "tree_split",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            oldSibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var bpParent = Bp(kBpParent, BranchPointType.Breakup,
                parents: new List<string>(),
                children: new List<string> { "rec_origin" });
            var bpChild = Bp(kBpChild, BranchPointType.Breakup,
                parents: new List<string> { "rec_origin" },
                children: new List<string> { "rec_old_sib" });

            InstallTree("tree_split",
                new List<Recording> { head, tip, oldSibling },
                new List<BranchPoint> { bpParent, bpChild });

            // Marker: in-place continuation (origin == provisional == HEAD).
            var marker = Marker(originId: "rec_origin", provisionalId: "rec_origin",
                treeId: "tree_split");
            var scenario = InstallScenario(marker);

            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            // 1) AppendRelations did NOT throw / did NOT silently return empty:
            //    the prior-attempt sibling rec_old_sib has a row pointing at
            //    the TIP id (the resolved chain target).
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_old_sib" && r.NewRecordingId == "rec_origin_tip");

            // 2) Neither HEAD nor TIP gets a supersede row. Both halves of
            //    the in-place chain are part of the new flight; superseding
            //    either would collapse ERS via EffectiveRecordingId redirect.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin_tip");

            // 3) Marker cleared, MergeState forced to Immutable on the head
            //    (this is the in-place behaviour; the tip stays at its
            //    default Immutable from the optimizer).
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, head.MergeState);

            // 4) The chain-tip-resolve diagnostic log line fires with both
            //    HEAD and TIP ids so future "why did the supersede target
            //    flip" questions can be answered from KSP.log alone.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("resolved chain tip for supersede target")
                && l.Contains("head=rec_origin")
                && l.Contains("tip=rec_origin_tip")
                && l.Contains("chainIndex=1")
                && l.Contains("tipTerminal=Destroyed"));

            // 5) AppendRelations summary now reports skippedExtraSelfLink>=1
            //    because the HEAD id was passed via extraSelfSkipRecordingIds.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 1 supersede relations")
                && l.Contains("skippedExtraSelfLink=1"));

            // 6) Verbose extra-self-skip line names HEAD id and points at TIP.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_origin")
                && l.Contains("new=rec_origin_tip"));
        }

        /// <summary>
        /// Direct AppendRelations test: when the caller passes the resolved
        /// chain TIP and adds the HEAD id to extraSelfSkipRecordingIds, the
        /// HEAD's closure entry is filtered, the TIP's self-link is also
        /// filtered (old==new guard), and only the unrelated sibling gets a
        /// row. Independent of the dialog wiring above.
        /// </summary>
        [Fact]
        public void AppendRelations_ExtraSelfSkip_FiltersHeadWhileTipIsTheTarget()
        {
            const string kBpParent = "bp_p_es";
            const string kBpChild = "bp_c_es";
            var head = Rec("rec_es_head", "tree_es",
                parentBranchPointId: kBpParent,
                childBranchPointId: kBpChild,
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_es";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });

            var tip = Rec("rec_es_tip", "tree_es",
                state: MergeState.Immutable,
                terminal: TerminalState.Landed);
            tip.ChainId = "chain_es";
            tip.ChainBranch = 0;
            tip.ChainIndex = 1;
            tip.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var sibling = Rec("rec_es_sib", "tree_es",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            sibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var bpParent = Bp(kBpParent, BranchPointType.Breakup,
                parents: new List<string>(),
                children: new List<string> { "rec_es_head" });
            var bpChild = Bp(kBpChild, BranchPointType.Breakup,
                parents: new List<string> { "rec_es_head" },
                children: new List<string> { "rec_es_sib" });

            InstallTree("tree_es",
                new List<Recording> { head, tip, sibling },
                new List<BranchPoint> { bpParent, bpChild });

            // Marker points at HEAD (in-place continuation; origin == provisional == head).
            var marker = Marker(originId: "rec_es_head", provisionalId: "rec_es_head",
                treeId: "tree_es");
            var scenario = InstallScenario(marker);

            // Caller (= MergeDialog in production) passes TIP as provisional
            // + HEAD id in extraSelfSkipRecordingIds.
            int countBefore = scenario.RecordingSupersedes.Count;
            var subtree = SupersedeCommit.AppendRelations(marker, tip, scenario,
                extraSelfSkipRecordingIds: new[] { "rec_es_head" });

            // Closure contains all three: head, tip, sibling.
            Assert.Contains("rec_es_head", subtree);
            Assert.Contains("rec_es_tip", subtree);
            Assert.Contains("rec_es_sib", subtree);

            // Exactly one row written: the sibling pointing at the tip.
            int countAfter = scenario.RecordingSupersedes.Count;
            Assert.Equal(countBefore + 1, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_sib" && r.NewRecordingId == "rec_es_tip");
            // Head NOT redirected to tip.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_head");
            // Tip NOT a self-link row.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_es_tip");

            // Logs: tip self-link skip + head extra-self-link skip both fire.
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_es_tip")
                && l.Contains("new=rec_es_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_es_head")
                && l.Contains("new=rec_es_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 1 supersede relations")
                && l.Contains("skippedSelfLink=1")
                && l.Contains("skippedExtraSelfLink=1"));
        }

        /// <summary>
        /// 3-arg AppendRelations overload (the existing entry point used by
        /// CommitSupersede + MergeJournalOrchestrator) is unchanged: passes
        /// extraSelfSkipRecordingIds=null and behaves exactly like before.
        /// Pin this so the journaled merge path is not silently affected.
        /// </summary>
        [Fact]
        public void AppendRelations_LegacyThreeArgOverload_NoExtraSkip_BehavesAsBefore()
        {
            InstallOriginClosureFixture("rec_origin", "rec_inside", "rec_outside");
            var provisional = AddProvisional("rec_provisional", "tree_1",
                TerminalState.Landed, supersedeTargetId: "rec_origin");
            var scenario = InstallScenario(Marker("rec_origin", "rec_provisional"));

            int countBefore = scenario.RecordingSupersedes.Count;
            // 3-arg call site (no extra skip).
            SupersedeCommit.AppendRelations(
                scenario.ActiveReFlySessionMarker, provisional, scenario);
            int countAfter = scenario.RecordingSupersedes.Count;

            // Two rows written (origin + inside, both pointing at the
            // provisional). No extra-self-skip count > 0.
            Assert.Equal(countBefore + 2, countAfter);
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_origin" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_inside" && r.NewRecordingId == "rec_provisional");
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 2 supersede relations")
                && l.Contains("skippedExtraSelfLink=0"));
        }

        // ---------- Multi-segment chain skip set (review follow-up #2) ----
        // Reviewer flagged that the prior fix only added the HEAD id to
        // extraSelfSkipRecordingIds. For a 3+-segment in-place chain
        // (HEAD -> MIDDLE -> TIP), AppendRelations would write a row
        // old=MIDDLE new=TIP and silently collapse MIDDLE in ERS via
        // EffectiveRecordingId redirect, even though MIDDLE is part of the
        // SAME new in-place flight. The fix builds the skip set from the
        // full chain membership (TreeId + ChainId + ChainBranch matches
        // from RecordingStore.CommittedRecordings — the same scope
        // EffectiveState.ComputeSubtreeClosureInternal +
        // EnqueueChainSiblings use) so no in-place chain segment ends up
        // with a row pointing at another member.

        /// <summary>
        /// HEAD -> MIDDLE -> TIP in-place chain (three segments) plus a
        /// prior-attempt sibling under a child BP. The prior-attempt sibling
        /// must still get a supersede row pointing at the resolved tip;
        /// none of HEAD/MIDDLE/TIP must end up with a row pointing at
        /// another chain member.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_ThreeSegmentChain_NoMemberSupersededByAnotherMember()
        {
            const string kBpParent = "bp_p_3seg";
            const string kBpChild = "bp_c_3seg";
            // HEAD: in-place provisional, no terminal post-split, has Points,
            // chainIndex=0.
            var head = Rec("rec_3seg_head", "tree_3seg",
                parentBranchPointId: kBpParent,
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_3seg";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            // STALE OLD EXO: same chain identity from the original flight,
            // but no CreatingSessionId. This is the fresh-log regression:
            // it must be superseded by the new tip, not protected as part
            // of the in-place replacement flight.
            var staleOldExo = Rec("rec_3seg_stale_old_exo", "tree_3seg",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            staleOldExo.ChainId = "chain_3seg";
            staleOldExo.ChainBranch = 0;
            staleOldExo.ChainIndex = 1;
            staleOldExo.Points.Add(new TrajectoryPoint { ut = 1.2 });
            staleOldExo.Points.Add(new TrajectoryPoint { ut = 1.5 });

            // MIDDLE: new session-owned chain segment, chainIndex=2,
            // also no terminal, has Points.
            var middle = Rec("rec_3seg_middle", "tree_3seg",
                state: MergeState.Immutable,
                terminal: null);
            middle.ChainId = "chain_3seg";
            middle.ChainBranch = 0;
            middle.ChainIndex = 2;
            middle.CreatingSessionId = "sess_1";
            middle.ProvisionalForRpId = "rp_1";
            middle.Points.Add(new TrajectoryPoint { ut = 1.0 });
            middle.Points.Add(new TrajectoryPoint { ut = 2.0 });

            // TIP: chainIndex=3, carries the terminal payload + Points.
            // ChildBranchPointId moves to the TIP after the optimizer cascade
            // (RecordingStore.cs:2018-2019), so the BP-walk runs from here.
            var tip = Rec("rec_3seg_tip", "tree_3seg",
                childBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            tip.ChainId = "chain_3seg";
            tip.ChainBranch = 0;
            tip.ChainIndex = 3;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.Points.Add(new TrajectoryPoint { ut = 2.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 3.0 });

            // Prior-attempt sibling under bp_child — the row we need written
            // for the original Bug 2 fix to actually land. NOT a chain member
            // (different ChainId) so it must NOT be in the chain skip set.
            var oldSibling = Rec("rec_3seg_old_sib", "tree_3seg",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            oldSibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var bpParent = Bp(kBpParent, BranchPointType.Breakup,
                parents: new List<string>(),
                children: new List<string> { "rec_3seg_head" });
            var bpChild = Bp(kBpChild, BranchPointType.Breakup,
                parents: new List<string> { "rec_3seg_tip" },
                children: new List<string> { "rec_3seg_old_sib" });

            InstallTree("tree_3seg",
                new List<Recording> { head, staleOldExo, middle, tip, oldSibling },
                new List<BranchPoint> { bpParent, bpChild });

            var marker = Marker(originId: "rec_3seg_head", provisionalId: "rec_3seg_head",
                treeId: "tree_3seg");
            var scenario = InstallScenario(marker);

            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            // 1) Prior-attempt sibling gets a row pointing at the TIP.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_3seg_old_sib" && r.NewRecordingId == "rec_3seg_tip");
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_3seg_stale_old_exo" && r.NewRecordingId == "rec_3seg_tip");

            // 2) NO row from any chain member to any other chain member.
            //    This is the regression: with the previous skip-set-of-just-HEAD
            //    code, MIDDLE -> TIP would be written and silently collapse
            //    MIDDLE in ERS.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_3seg_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_3seg_middle");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_3seg_tip");

            // 3) Marker cleared, MergeState forced to Immutable on the head.
            Assert.Null(scenario.ActiveReFlySessionMarker);
            Assert.Equal(MergeState.Immutable, head.MergeState);

            // 4) chain-skip-set: log line audits the membership decision.
            //    Members are unordered in HashSet<string> enumeration, so
            //    assert presence by substring matches rather than exact order.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("chain-skip-set:")
                && l.Contains("chainId=chain_3seg")
                && l.Contains("chainBranch=0")
                && l.Contains("treeId=tree_3seg")
                && l.Contains("rec_3seg_head")
                && l.Contains("rec_3seg_middle")
                && l.Contains("rec_3seg_tip")
                && !l.Contains("rec_3seg_stale_old_exo")
                && l.Contains("size=3"));

            // 5) AppendRelations summary: 2 rows written, 1 self-link
            //    skipped (the TIP), 2 extra-self-link skips (HEAD + MIDDLE).
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("Added 2 supersede relations")
                && l.Contains("skippedSelfLink=1")
                && l.Contains("skippedExtraSelfLink=2"));

            // 6) Per-skip Verbose lines fire for HEAD and MIDDLE (extra-self)
            //    and for TIP (self-link guard).
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_3seg_head")
                && l.Contains("new=rec_3seg_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip extra-self-link")
                && l.Contains("old=rec_3seg_middle")
                && l.Contains("new=rec_3seg_tip"));
            Assert.Contains(logLines, l =>
                l.Contains("[Supersede]")
                && l.Contains("AppendRelations: skip self-link")
                && l.Contains("old=rec_3seg_tip")
                && l.Contains("new=rec_3seg_tip"));

            // 7) MergeDialog summary log says "full chain (3 member(s))" so
            //    the count is auditable from the high-level summary too.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("in-place continuation supersede append")
                && l.Contains("wrote 2 relation")
                && l.Contains("full chain (3 member(s))"));
        }

        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_UntaggedOptimizerSplit_UsesContiguousTip()
        {
            var head = Rec("rec_untag_head", "tree_untag",
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_untag";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var staleOldTail = Rec("rec_untag_stale_old_tail", "tree_untag",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            staleOldTail.ChainId = "chain_untag";
            staleOldTail.ChainBranch = 0;
            staleOldTail.ChainIndex = 1;
            staleOldTail.Points.Add(new TrajectoryPoint { ut = 0.4 });
            staleOldTail.Points.Add(new TrajectoryPoint { ut = 0.6 });

            var middle = Rec("rec_untag_middle", "tree_untag",
                state: MergeState.Immutable,
                terminal: null);
            middle.ChainId = "chain_untag";
            middle.ChainBranch = 0;
            middle.ChainIndex = 2;
            middle.Points.Add(new TrajectoryPoint { ut = 1.0 });
            middle.Points.Add(new TrajectoryPoint { ut = 2.0 });

            var tip = Rec("rec_untag_tip", "tree_untag",
                state: MergeState.Immutable,
                terminal: TerminalState.Orbiting);
            tip.ChainId = "chain_untag";
            tip.ChainBranch = 0;
            tip.ChainIndex = 3;
            tip.Points.Add(new TrajectoryPoint { ut = 2.0 });
            tip.Points.Add(new TrajectoryPoint { ut = 3.0 });

            InstallTree("tree_untag",
                new List<Recording> { head, staleOldTail, middle, tip },
                new List<BranchPoint>());

            // Match the captured failure mode: the flat committed list contains
            // optimizer split children, but the committed tree lookup still
            // resolves the in-place head to itself. The continuity fallback must
            // use the flat list to find the real post-optimizer tip.
            RecordingStore.CommittedTrees[0].Recordings.Remove("rec_untag_stale_old_tail");
            RecordingStore.CommittedTrees[0].Recordings.Remove("rec_untag_middle");
            RecordingStore.CommittedTrees[0].Recordings.Remove("rec_untag_tip");

            var marker = Marker(originId: "rec_untag_head", provisionalId: "rec_untag_head",
                treeId: "tree_untag");
            var scenario = InstallScenario(marker);

            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_untag_stale_old_tail"
                    && r.NewRecordingId == "rec_untag_tip");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_untag_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_untag_middle");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_untag_tip");
            Assert.Null(scenario.ActiveReFlySessionMarker);

            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("contiguous split bounds")
                && l.Contains("head=rec_untag_head")
                && l.Contains("tip=rec_untag_tip")
                && l.Contains("rec_untag_middle")
                && !l.Contains("rec_untag_stale_old_tail"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("chain-skip-set:")
                && l.Contains("rec_untag_head")
                && l.Contains("rec_untag_middle")
                && l.Contains("rec_untag_tip")
                && !l.Contains("rec_untag_stale_old_tail")
                && l.Contains("size=3"));
        }

        [Fact]
        public void ResolveSessionOwnedChainTerminalRecording_IgnoresHigherIndexStaleTail()
        {
            var head = Rec("rec_target_head", "tree_target",
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_target";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_target";

            var sessionTip = Rec("rec_target_session_tip", "tree_target",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            sessionTip.ChainId = "chain_target";
            sessionTip.ChainBranch = 0;
            sessionTip.ChainIndex = 2;
            sessionTip.CreatingSessionId = "sess_target";

            var staleHigherTail = Rec("rec_target_stale_tail", "tree_target",
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            staleHigherTail.ChainId = "chain_target";
            staleHigherTail.ChainBranch = 0;
            staleHigherTail.ChainIndex = 9;

            InstallTree("tree_target",
                new List<Recording> { head, sessionTip, staleHigherTail },
                new List<BranchPoint>());

            Recording resolved = MergeDialog.ResolveSessionOwnedChainTerminalRecording(
                head, "sess_target");

            Assert.Same(sessionTip, resolved);
        }

        /// <summary>
        /// Same ChainId, DIFFERENT ChainBranch: a prior-attempt sibling that
        /// happens to share ChainId with the in-place chain but lives on a
        /// different ChainBranch is NOT a current chain member. It must
        /// still get a supersede row when it sits in the closure (e.g. a
        /// child of the parent BP of the in-place head). The chain-skip-set
        /// predicate matches EnqueueChainSiblings' TreeId+ChainId+ChainBranch
        /// triple, so different ChainBranch ids are correctly excluded from
        /// the skip set.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_SameChainIdDifferentBranch_StillSuperseded()
        {
            const string kBpParent = "bp_p_diffbr";
            const string kBpChild = "bp_c_diffbr";
            var head = Rec("rec_diffbr_head", "tree_diffbr",
                parentBranchPointId: kBpParent,
                childBranchPointId: kBpChild,
                state: MergeState.NotCommitted,
                terminal: null);
            head.ChainId = "chain_shared";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.CreatingSessionId = "sess_1";
            head.ProvisionalForRpId = "rp_1";
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });

            var tip = Rec("rec_diffbr_tip", "tree_diffbr",
                state: MergeState.Immutable,
                terminal: TerminalState.Landed);
            tip.ChainId = "chain_shared";
            tip.ChainBranch = 0; // same branch as head -> in chain skip set
            tip.ChainIndex = 1;
            tip.CreatingSessionId = "sess_1";
            tip.ProvisionalForRpId = "rp_1";
            tip.Points.Add(new TrajectoryPoint { ut = 1.0 });

            // Prior-attempt sibling: same tree, same ChainId, but DIFFERENT
            // ChainBranch. Lives under bp_child as a BP descendant so it
            // enters the closure via the BP walk, not chain expansion.
            // Closure walk WILL include it but the chain-skip-set predicate
            // will NOT (mismatched ChainBranch).
            var sibling = Rec("rec_diffbr_old_sib", "tree_diffbr",
                parentBranchPointId: kBpChild,
                state: MergeState.Immutable,
                terminal: TerminalState.Destroyed);
            sibling.ChainId = "chain_shared";
            sibling.ChainBranch = 99; // distinct branch
            sibling.ChainIndex = 0;
            sibling.Points.Add(new TrajectoryPoint { ut = 5.0 });

            var bpParent = Bp(kBpParent, BranchPointType.Breakup,
                parents: new List<string>(),
                children: new List<string> { "rec_diffbr_head" });
            var bpChild = Bp(kBpChild, BranchPointType.Breakup,
                parents: new List<string> { "rec_diffbr_head" },
                children: new List<string> { "rec_diffbr_old_sib" });

            InstallTree("tree_diffbr",
                new List<Recording> { head, tip, sibling },
                new List<BranchPoint> { bpParent, bpChild });

            var marker = Marker(originId: "rec_diffbr_head", provisionalId: "rec_diffbr_head",
                treeId: "tree_diffbr");
            var scenario = InstallScenario(marker);

            RewindPointReaper.DeleteQuicksaveForTesting = _ => true;
            try
            {
                var result = MergeDialog.TryCommitReFlySupersede();
                Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            }
            finally
            {
                RewindPointReaper.ResetTestOverrides();
            }

            // Prior-attempt sibling on the OTHER chain branch IS superseded.
            Assert.Contains(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_diffbr_old_sib" && r.NewRecordingId == "rec_diffbr_tip");
            // Neither chain member of the in-place flight is superseded.
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_diffbr_head");
            Assert.DoesNotContain(scenario.RecordingSupersedes,
                r => r.OldRecordingId == "rec_diffbr_tip");

            // Skip set has size 2 (head + tip only). The same-ChainId-but-
            // different-ChainBranch sibling is NOT in the set.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("chain-skip-set:")
                && l.Contains("chainId=chain_shared")
                && l.Contains("chainBranch=0")
                && l.Contains("size=2"));
        }

        /// <summary>
        /// Lone-origin in-place (no chain split, no ChainId set): the
        /// chain-skip-set degenerates to a single-element set with just the
        /// provisional's own RecordingId. AppendRelations' old==new
        /// self-link guard catches the trivial origin entry, and the empty
        /// closure produces no rows. Pin the new chain-skip-set log line so
        /// the lone-origin case is auditable too. Mirrors
        /// <c>InPlaceContinuation_LoneOrigin_FiltersSelfLinkOnly</c>'s
        /// fixture but adds the new log assertion.
        /// </summary>
        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_NoChain_ChainSkipSetLogsSizeOne()
        {
            var origin = Rec("rec_no_chain_origin", "tree_nc",
                state: MergeState.NotCommitted,
                terminal: TerminalState.Landed,
                supersedeTargetId: null);
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });
            // ChainId left null -> degenerate fallback path in the chain-skip-set logic.
            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_nc");
            var tree = new RecordingTree
            {
                Id = "tree_nc",
                TreeName = "tree_nc",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_no_chain_origin",
                provisionalId: "rec_no_chain_origin", treeId: "tree_nc");
            var scenario = InstallScenario(marker);

            var result = MergeDialog.TryCommitReFlySupersede();
            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);

            // No supersede rows (lone origin: only candidate is self-link).
            Assert.Empty(scenario.RecordingSupersedes);

            // chain-skip-set log fires with size=1 and chainId=<none>.
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("chain-skip-set:")
                && l.Contains("chainId=<none>")
                && l.Contains("rec_no_chain_origin")
                && l.Contains("size=1"));
        }

        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_NoSplitNullTerminal_RepairsFromSceneExitSituation()
        {
            var origin = Rec("rec_scene_exit_origin", "tree_scene_exit",
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: null);
            origin.SceneExitSituation = 2; // Vessel.Situations.SPLASHED
            origin.Points.Add(new TrajectoryPoint { ut = 0.0 });
            origin.Points.Add(new TrajectoryPoint { ut = 1.0 });

            RecordingStore.AddRecordingWithTreeForTesting(origin, "tree_scene_exit");
            var tree = new RecordingTree
            {
                Id = "tree_scene_exit",
                TreeName = "tree_scene_exit",
                BranchPoints = new List<BranchPoint>(),
            };
            tree.AddOrReplaceRecording(origin);
            RecordingStore.CommittedTrees.Add(tree);

            var marker = Marker(originId: "rec_scene_exit_origin",
                provisionalId: "rec_scene_exit_origin",
                treeId: "tree_scene_exit",
                sessionId: "sess_scene_exit");
            var scenario = InstallScenario(marker);

            var result = MergeDialog.TryCommitReFlySupersede();

            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            Assert.Equal(TerminalState.Splashed, origin.TerminalStateValue);
            Assert.Empty(scenario.RecordingSupersedes);
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]") &&
                l.Contains("repaired null terminal") &&
                l.Contains("rec_scene_exit_origin") &&
                l.Contains("terminal=Splashed"));
        }

        [Fact]
        public void TryCommitReFlySupersede_InPlaceContinuation_ContiguousHoleStaysOnHeadAndRepairs()
        {
            var head = Rec("rec_hole_head", "tree_hole",
                state: MergeState.NotCommitted,
                terminal: null,
                supersedeTargetId: null);
            head.ChainId = "chain_hole";
            head.ChainBranch = 0;
            head.ChainIndex = 0;
            head.SceneExitSituation = (int)Vessel.Situations.FLYING;
            head.Points.Add(new TrajectoryPoint { ut = 0.0 });
            head.Points.Add(new TrajectoryPoint { ut = 1.0 });

            var middleAfterHole = Rec("rec_hole_middle", "tree_hole",
                state: MergeState.Immutable,
                terminal: null);
            middleAfterHole.ChainId = "chain_hole";
            middleAfterHole.ChainBranch = 0;
            middleAfterHole.ChainIndex = 2;
            middleAfterHole.Points.Add(new TrajectoryPoint { ut = 2.0 });
            middleAfterHole.Points.Add(new TrajectoryPoint { ut = 3.0 });

            var tipAfterHole = Rec("rec_hole_tip", "tree_hole",
                state: MergeState.Immutable,
                terminal: TerminalState.Orbiting);
            tipAfterHole.ChainId = "chain_hole";
            tipAfterHole.ChainBranch = 0;
            tipAfterHole.ChainIndex = 3;
            tipAfterHole.Points.Add(new TrajectoryPoint { ut = 3.0 });
            tipAfterHole.Points.Add(new TrajectoryPoint { ut = 4.0 });

            InstallTree("tree_hole",
                new List<Recording> { head, middleAfterHole, tipAfterHole },
                new List<BranchPoint>());

            // Simulate a flat-list optimizer artifact where later same-chain records
            // exist, but the committed tree still resolves the in-place origin to
            // itself. The contiguous walk must not jump the 1s hole from HEAD to MIDDLE.
            RecordingStore.CommittedTrees[0].Recordings.Remove("rec_hole_middle");
            RecordingStore.CommittedTrees[0].Recordings.Remove("rec_hole_tip");

            List<Recording> members = MergeDialog.ResolveContiguousInPlaceChainMembers(head);
            Assert.Single(members);
            Assert.Same(head, members[0]);

            var marker = Marker(originId: "rec_hole_head",
                provisionalId: "rec_hole_head",
                treeId: "tree_hole",
                sessionId: "sess_hole");
            var scenario = InstallScenario(marker);

            var result = MergeDialog.TryCommitReFlySupersede();

            Assert.Equal(MergeDialog.ReFlyMergeCommitResult.Completed, result);
            Assert.Equal(TerminalState.SubOrbital, head.TerminalStateValue);
            Assert.Equal(2, scenario.RecordingSupersedes.Count);
            Assert.All(scenario.RecordingSupersedes, r =>
                Assert.Equal("rec_hole_head", r.NewRecordingId));
            Assert.Contains(scenario.RecordingSupersedes, r =>
                r.OldRecordingId == "rec_hole_middle");
            Assert.Contains(scenario.RecordingSupersedes, r =>
                r.OldRecordingId == "rec_hole_tip");
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("resolver audit")
                && l.Contains("sessionOwnedSize=0")
                && l.Contains("contiguousSize=1")
                && l.Contains("contiguousTip=rec_hole_head"));
            Assert.Contains(logLines, l =>
                l.Contains("[MergeDialog]")
                && l.Contains("repaired null terminal")
                && l.Contains("rec_hole_head")
                && l.Contains("terminal=SubOrbital"));
        }

        [Fact]
        public void ValidateSupersedeTarget_ReasonStrings()
        {
            string reason;

            Assert.False(SupersedeCommit.ValidateSupersedeTarget(null, out reason));
            Assert.Equal("null recording", reason);

            var emptyPoints = new Recording { Points = new List<TrajectoryPoint>(), TerminalStateValue = TerminalState.Landed };
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(emptyPoints, out reason));
            Assert.Equal("empty Points", reason);

            var nullTerminal = new Recording { TerminalStateValue = null };
            nullTerminal.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.False(SupersedeCommit.ValidateSupersedeTarget(nullTerminal, out reason));
            Assert.Equal("null TerminalState", reason);

            var ok = new Recording { TerminalStateValue = TerminalState.Landed };
            ok.Points.Add(new TrajectoryPoint { ut = 0.0 });
            Assert.True(SupersedeCommit.ValidateSupersedeTarget(ok, out reason));
            Assert.Null(reason);
        }
    }
}
