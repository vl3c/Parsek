using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Parsek.Tests
{
    // The load-time global dock-event graph (design-dock-event-graph.md 5.1 / 6.3 / 6.4 / 6.5).
    // One three-tree fixture drives every DockPartnerStatus: the AB/CD cross-tree shape, the
    // A->D same-tree recovery, a genuine two-parent merge, and the three degradation statuses.
    // [Collection("Sequential")] because ParsekLog's sink + the module suppression flag are
    // process-wide statics.
    [Collection("Sequential")]
    public class DockEventGraphTests : IDisposable
    {
        // ---- fixture ids / pids (Guid-free; the trees are hand-built and never committed) ----
        private const uint PidP = 500;
        private const uint PidQ = 501;
        private const uint PidD = 300;
        private const uint PidNowhere = 999_999;
        private const string GuidP = "pppppppppppppppppppppppppppppppp";
        private const string GuidQ = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";
        private const string GuidD = "dddddddddddddddddddddddddddddddd";
        // A DIFFERENT launch of the same craft as B (craft-baked pid collision, design edge 8).
        private const string GuidBOther = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

        private const double Dock2UT = 500.0;

        private readonly List<string> logLines = new List<string>();

        public DockEventGraphTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            DockEventGraph.SuppressLogging = false;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        // ------------------------------------------------------------------
        // Fixture: three committed trees exercising every resolution branch.
        //
        //  tb : B0 (pid B)  - the partner's solo pre-dock flight.
        //  ta : A0 -> dockbp(target=pid B, CROSS-TREE) -> AB -> undockbp -> { A1, B1 }
        //       A1 -> dockbp2(target=pid D, SAME-TREE RECOVERED) -> AD ; D0 is A1's
        //       terminal-stamped same-tree partner line (the A->D cross-session shape).
        //  tc : P + Q -> twoparentbp(TWO-PARENT) -> PQ -> zerobp(UNSTAMPED) -> PQ2 ->
        //       nomatchbp(NO MATCH) -> PQ3 -> guidrejbp(GUID REJECTED) -> PQ4.
        //       Bx is DEBRIS carrying pid B under a different launch guid: it makes the
        //       expected guid known (so the tb claim is conclusively rejected) while being
        //       excluded from the same-tree recovery, which is what isolates GuidRejected.
        // ------------------------------------------------------------------

        private static RecordingTree PartnerTree()
            => CrossTreeDockFixture.Tree("tb", new[]
            {
                CrossTreeDockFixture.Rec("B0", CrossTreeDockFixture.PidB, CrossTreeDockFixture.GuidB,
                    "CB", 0, 0, 100),
            });

        private static RecordingTree ControllerTree()
        {
            var a0 = CrossTreeDockFixture.Rec("A0", CrossTreeDockFixture.PidA, CrossTreeDockFixture.GuidA,
                "CA", 0, 0, CrossTreeDockFixture.DockUT, pods: 1, probes: 0, childBp: "dockbp");
            var ab = CrossTreeDockFixture.Rec("AB", CrossTreeDockFixture.PidA, CrossTreeDockFixture.GuidA,
                "CAB", 0, CrossTreeDockFixture.DockUT, CrossTreeDockFixture.UndockUT,
                pods: 1, probes: 1, parentBp: "dockbp", childBp: "undockbp", vessel: "Stack AB");
            var a1 = CrossTreeDockFixture.Rec("A1", CrossTreeDockFixture.PidA, CrossTreeDockFixture.GuidA,
                "CA2", 0, CrossTreeDockFixture.UndockUT, Dock2UT,
                pods: 1, probes: 0, parentBp: "undockbp", childBp: "dockbp2");
            var b1 = CrossTreeDockFixture.Rec("B1", CrossTreeDockFixture.PidB, CrossTreeDockFixture.GuidB,
                "CB2", 0, CrossTreeDockFixture.UndockUT, 380);
            var d0 = CrossTreeDockFixture.Rec("D0", PidD, GuidD, "CD", 0, 320, 360, vessel: "Depot D");
            var ad = CrossTreeDockFixture.Rec("AD", CrossTreeDockFixture.PidA, CrossTreeDockFixture.GuidA,
                "CAD", 0, Dock2UT, 600, pods: 1, probes: 1, parentBp: "dockbp2", vessel: "Stack AD");

            return CrossTreeDockFixture.Tree("ta",
                new[] { a0, ab, a1, b1, d0, ad },
                new[]
                {
                    CrossTreeDockFixture.BP("dockbp", BranchPointType.Dock, CrossTreeDockFixture.DockUT,
                        new[] { "A0" }, new[] { "AB" },
                        targetPid: CrossTreeDockFixture.PidB, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("undockbp", BranchPointType.Undock, CrossTreeDockFixture.UndockUT,
                        new[] { "AB" }, new[] { "A1", "B1" }, splitCause: "UNDOCK"),
                    CrossTreeDockFixture.BP("dockbp2", BranchPointType.Dock, Dock2UT,
                        new[] { "A1" }, new[] { "AD" }, targetPid: PidD, mergeCause: "DOCK"),
                });
        }

        private static RecordingTree DegradationTree()
        {
            var p = CrossTreeDockFixture.Rec("P", PidP, GuidP, "CP", 0, 0, 50,
                pods: 1, probes: 0, childBp: "twoparentbp");
            var q = CrossTreeDockFixture.Rec("Q", PidQ, GuidQ, "CQ", 0, 10, 50, childBp: "twoparentbp");
            var pq = CrossTreeDockFixture.Rec("PQ", PidP, GuidP, "CPQ", 0, 50, 100,
                pods: 1, probes: 1, parentBp: "twoparentbp", childBp: "zerobp", vessel: "Stack PQ");
            var pq2 = CrossTreeDockFixture.Rec("PQ2", PidP, GuidP, "CPQ", 1, 100, 150,
                parentBp: "zerobp", childBp: "nomatchbp");
            var pq3 = CrossTreeDockFixture.Rec("PQ3", PidP, GuidP, "CPQ", 2, 150, 200,
                parentBp: "nomatchbp", childBp: "guidrejbp");
            var pq4 = CrossTreeDockFixture.Rec("PQ4", PidP, GuidP, "CPQ", 3, 200, 250,
                parentBp: "guidrejbp");
            var bx = CrossTreeDockFixture.Rec("Bx", CrossTreeDockFixture.PidB, GuidBOther,
                "CBX", 0, 0, 10, debris: true);

            return CrossTreeDockFixture.Tree("tc",
                new[] { p, q, pq, pq2, pq3, pq4, bx },
                new[]
                {
                    CrossTreeDockFixture.BP("twoparentbp", BranchPointType.Dock, 50.0,
                        new[] { "P", "Q" }, new[] { "PQ" }, targetPid: PidQ, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("zerobp", BranchPointType.Dock, 100.0,
                        new[] { "PQ" }, new[] { "PQ2" }, targetPid: 0, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("nomatchbp", BranchPointType.Dock, 150.0,
                        new[] { "PQ2" }, new[] { "PQ3" }, targetPid: PidNowhere, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("guidrejbp", BranchPointType.Dock, 200.0,
                        new[] { "PQ3" }, new[] { "PQ4" },
                        targetPid: CrossTreeDockFixture.PidB, mergeCause: "DOCK"),
                });
        }

        private static List<RecordingTree> AllTrees()
            => new List<RecordingTree> { PartnerTree(), ControllerTree(), DegradationTree() };

        private static DockEventGraph BuildAll(Func<string, bool> visible = null)
            => DockEventGraph.Build(AllTrees(), visible);

        // ------------------------------------------------------------------
        // Build: statuses, nodes, indexes
        // ------------------------------------------------------------------

        [Fact]
        public void Build_ReachesEveryPartnerStatus()
        {
            // The resolution-order contract (design 6.3 step 2). Fails if any branch of the
            // ordered a..f cascade is unreachable, or if a later branch shadows an earlier one
            // (the exact class of bug that would make a cross-tree dock read as a same-tree
            // self-claim, or a guid rejection read as a plain no-match).
            var graph = BuildAll();

            Assert.Equal(DockPartnerStatus.CrossTree,
                graph.NodesByBranchPointId["dockbp"].Partner.Status);
            Assert.Equal(DockPartnerStatus.SameTreeRecovered,
                graph.NodesByBranchPointId["dockbp2"].Partner.Status);
            Assert.Equal(DockPartnerStatus.TwoParentSameTree,
                graph.NodesByBranchPointId["twoparentbp"].Partner.Status);
            Assert.Equal(DockPartnerStatus.UnstampedZero,
                graph.NodesByBranchPointId["zerobp"].Partner.Status);
            Assert.Equal(DockPartnerStatus.NoMatch,
                graph.NodesByBranchPointId["nomatchbp"].Partner.Status);
            Assert.Equal(DockPartnerStatus.GuidRejected,
                graph.NodesByBranchPointId["guidrejbp"].Partner.Status);
        }

        [Fact]
        public void Build_CrossTreeNode_CarriesTheClaimedRecordingAndTree()
        {
            // The AB/CD resolution (design 1.2 BP_d1). Fails if the claim lands on the merged
            // stack, on a post-undock continuation carrying the same pid, or in the wrong tree.
            var node = BuildAll().NodesByBranchPointId["dockbp"];

            Assert.Equal("B0", node.Partner.PartnerRecordingId);
            Assert.Equal("tb", node.Partner.PartnerTreeId);
            Assert.Equal("Vessel B0", node.Partner.PartnerVesselName);
            Assert.Equal(CrossTreeDockFixture.GuidB, node.Partner.PartnerLaunchGuid);
            Assert.Equal(CrossTreeDockFixture.PidB, node.PartnerPid);
            Assert.Equal("AB", node.MergedChildRecordingId);
            Assert.Equal("Stack AB", node.MergedChildVesselName);
        }

        [Fact]
        public void Build_SameTreeRecoveredNode_ClaimsThePartnersOwnLine()
        {
            // The A->D shape (design 1.2 BP_d2 / 6.2). Fails if the same-tree recovery is not
            // wired into the graph, or claims the stack instead of D's own terminal-stamped leaf.
            var node = BuildAll().NodesByBranchPointId["dockbp2"];

            Assert.Equal("D0", node.Partner.PartnerRecordingId);
            Assert.Equal("ta", node.Partner.PartnerTreeId);
            Assert.Equal("Depot D", node.Partner.PartnerVesselName);
            Assert.Equal("AD", node.MergedChildRecordingId);
        }

        [Fact]
        public void Build_UndockNodes_ArePresentWithNoResolution()
        {
            // Undock nodes carry no partner (design 6.3 step 1) but MUST exist: PR4's digest
            // and PR6's R3 marker read them. Fails if the type filter drops Undock, or if a
            // partner resolution leaks onto a split.
            var node = BuildAll().NodesByBranchPointId["undockbp"];

            Assert.Equal(BranchPointType.Undock, node.Kind);
            Assert.False(node.IsMerge);
            Assert.Equal(DockPartnerStatus.UnstampedZero, node.Partner.Status);
            Assert.Null(node.Partner.PartnerRecordingId);
            Assert.Equal(0u, node.PartnerPid);
            Assert.Null(node.MergeCause);
            Assert.Equal(new[] { "A1", "B1" }, node.ChildRecordingIds);
        }

        [Fact]
        public void Build_IgnoresNonDockBranchPointTypes()
        {
            // Only Dock / Board / Undock lift into the graph. Fails if a Launch / EVA /
            // Terminal branch point starts minting nodes (they carry no merge semantics and
            // would pollute every digest and tree index).
            var trees = AllTrees();
            trees[2].BranchPoints.Add(CrossTreeDockFixture.BP(
                "evabp", BranchPointType.EVA, 260.0, new[] { "PQ4" }, new[] { "PQ4" }));
            trees[2].BranchPoints.Add(CrossTreeDockFixture.BP(
                "termbp", BranchPointType.Terminal, 270.0, new[] { "PQ4" }, new string[0]));

            var graph = DockEventGraph.Build(trees, null);

            Assert.False(graph.NodesByBranchPointId.ContainsKey("evabp"));
            Assert.False(graph.NodesByBranchPointId.ContainsKey("termbp"));
            Assert.Equal(7, graph.Nodes.Count);
        }

        [Fact]
        public void Build_NodesSortedByUTThenBranchPointId()
        {
            // Deterministic order is what every downstream consumer (digest rows, seam-marker
            // cursor) assumes. dockbp (ta) and nomatchbp (tc) share UT=150, so the id tie-break
            // is exercised. Fails if the sort is dropped or the tie-break is not ordinal.
            var graph = BuildAll();

            var ids = new List<string>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                ids.Add(graph.Nodes[i].BranchPointId);
                if (i > 0)
                    Assert.True(graph.Nodes[i - 1].UT <= graph.Nodes[i].UT);
            }
            Assert.Equal(
                new[] { "twoparentbp", "zerobp", "dockbp", "nomatchbp", "guidrejbp", "undockbp", "dockbp2" },
                ids);
        }

        [Fact]
        public void Build_NodesByTreeId_RegistersCrossTreeNodeUnderBothTrees()
        {
            // Design 6.3 step 4: mission CD must see the dock that mission AB recorded. Fails
            // if the partner-side registration is dropped - the partner mission would go back
            // to knowing nothing about a dock that consumed its vessel.
            var graph = BuildAll();

            Assert.Contains(graph.NodesForTree("ta"), n => n.BranchPointId == "dockbp");
            Assert.Contains(graph.NodesForTree("tb"), n => n.BranchPointId == "dockbp");
            // The same-tree recovery registers once (owner == partner tree).
            Assert.Single(graph.NodesForTree("ta"), n => n.BranchPointId == "dockbp2");
            // tb owns no branch points of its own.
            Assert.Single(graph.NodesForTree("tb"));
        }

        [Fact]
        public void Build_ParticipantsByRecordingId_CoversParentsMergedChildAndPartner()
        {
            // The reverse index the Recordings tab and the digest read. Fails if any of the
            // three participant classes is dropped (a recording would then have no way to find
            // the dock it took part in).
            var graph = BuildAll();

            Assert.Contains(graph.ParticipantsByRecordingId["A0"], n => n.BranchPointId == "dockbp");
            Assert.Contains(graph.ParticipantsByRecordingId["AB"], n => n.BranchPointId == "dockbp");
            Assert.Contains(graph.ParticipantsByRecordingId["B0"], n => n.BranchPointId == "dockbp");
            Assert.Contains(graph.ParticipantsByRecordingId["D0"], n => n.BranchPointId == "dockbp2");
            Assert.Contains(graph.ParticipantsByRecordingId["P"], n => n.BranchPointId == "twoparentbp");
            Assert.Contains(graph.ParticipantsByRecordingId["Q"], n => n.BranchPointId == "twoparentbp");
        }

        [Fact]
        public void Build_DockConnectedTreePairs_CarriesTheCrossTreePairOnly()
        {
            // The adjacency the R6 double-clock advisory reads (design 7.7). Only CrossTree
            // nodes connect trees. Fails if a same-tree recovery or a two-parent merge starts
            // reporting a (tree, tree) self-pair, which would make every mission look
            // dock-connected to itself.
            var graph = BuildAll();

            Assert.Contains(("ta", "tb"), graph.DockConnectedTreePairs);
            Assert.Single(graph.DockConnectedTreePairs);
            Assert.True(graph.AreDockConnected("tb", "ta"));
            Assert.False(graph.AreDockConnected("ta", "tc"));
        }

        [Fact]
        public void Build_TwoParentNode_CarriesBothParentNames()
        {
            // TryDescribePartner names a two-parent partner WITHOUT touching any store, so the
            // names must be captured at build. Fails if ParentVesselNames drifts out of index
            // alignment with ParentRecordingIds (which would name the wrong vessel).
            var node = BuildAll().NodesByBranchPointId["twoparentbp"];

            Assert.Equal(new[] { "P", "Q" }, node.ParentRecordingIds);
            Assert.Equal(new[] { "Vessel P", "Vessel Q" }, node.ParentVesselNames);
            Assert.Null(node.Partner.PartnerRecordingId);
            Assert.Equal("tc", node.Partner.PartnerTreeId);
        }

        [Fact]
        public void Build_EmptyOrNullTrees_ProduceAnEmptyGraph()
        {
            // Design edge case 24: an empty slice must not crash any consumer.
            Assert.Empty(DockEventGraph.Build(null, null).Nodes);
            Assert.Empty(DockEventGraph.Build(new List<RecordingTree>(), null).Nodes);
            Assert.Empty(DockEventGraph.Build(new List<RecordingTree> { null }, null).Nodes);
        }

        [Fact]
        public void Build_StoresTheHostSignatureVerbatim()
        {
            // The host cache compares BuildSignature by ordinal equality; a core that dropped
            // or normalized it would make the cache rebuild (or never rebuild) every call.
            Assert.Equal("sig-1", DockEventGraph.Build(AllTrees(), null, "sig-1").BuildSignature);
        }

        // ------------------------------------------------------------------
        // Visibility flagging (design 6.3 step 3 / 6.4 last row)
        // ------------------------------------------------------------------

        [Fact]
        public void Build_SupersededMergedChild_FlagsTheNode_AndNamingDeclines()
        {
            // A re-fly-superseded subtree leaves its branch points in the tree, so the node is
            // still built - but every naming consumer must skip it. Fails if the flag is not
            // set (phantom story rows for a flight that was superseded away) or if
            // TryDescribePartner ignores it.
            var graph = BuildAll(id => id != "AB");

            DockEventNode dock = graph.NodesByBranchPointId["dockbp"];
            Assert.True(dock.VisibilityFlagged);
            // The Undock node hangs off the same (invisible) parent recording.
            Assert.True(graph.NodesByBranchPointId["undockbp"].VisibilityFlagged);
            Assert.False(graph.NodesByBranchPointId["dockbp2"].VisibilityFlagged);

            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "A0", null, out DockPartnerDescription _));
        }

        [Fact]
        public void Build_NullVisibilityPredicate_FlagsNothing()
        {
            // Headless / pre-ERS callers pass null. Fails if a null predicate is treated as
            // "nothing is visible", which would silently blank every dock label in game.
            var graph = BuildAll();

            for (int i = 0; i < graph.Nodes.Count; i++)
                Assert.False(graph.Nodes[i].VisibilityFlagged);
        }

        // ------------------------------------------------------------------
        // Logging (design 15.2)
        // ------------------------------------------------------------------

        [Fact]
        public void Build_EmitsTheGrepStableSummaryLine()
        {
            // The one diagnostic that makes a mis-resolution diagnosable from KSP.log alone.
            // Fails if any counter is dropped or renamed (grep contracts are token-exact).
            BuildAll();

            Assert.Contains(logLines, l =>
                l.Contains("[DockGraph]") && l.Contains("build nodes=7")
                && l.Contains("dock=6") && l.Contains("board=0") && l.Contains("undock=1")
                && l.Contains("twoParent=1") && l.Contains("crossTree=1")
                && l.Contains("sameTreeRecovered=1") && l.Contains("zero=1")
                && l.Contains("noMatch=1") && l.Contains("guidRejected=1")
                && l.Contains("visibilityFlagged=0") && l.Contains("ms="));
        }

        [Fact]
        public void Build_LogsEveryRecoveryAndGuidRejection()
        {
            // A guid rejection is the one failure where an affordance vanishes with no other
            // trace (design 6.3 step 2e). Fails if either per-node line is removed.
            BuildAll();

            Assert.Contains(logLines, l =>
                l.Contains("[DockGraph]") && l.Contains("sameTreeRecovered bp=dockbp2")
                && l.Contains("tree=ta") && l.Contains("claimed=D0"));
            Assert.Contains(logLines, l =>
                l.Contains("[DockGraph]") && l.Contains("guidRejected bp=guidrejbp")
                && l.Contains("tree=tc"));
        }

        [Fact]
        public void Build_SuppressLogging_SilencesTheGraphAndRestoresTheSiblingFlag()
        {
            // Per-frame callers set SuppressLogging; the build also borrows
            // MissionCrossTreeDock.SuppressLogging for its internal scan and MUST restore it
            // (a leaked true would silence the cross-tree diagnostics for the rest of the run).
            MissionCrossTreeDock.SuppressLogging = false;
            DockEventGraph.SuppressLogging = true;
            try
            {
                BuildAll();
            }
            finally
            {
                DockEventGraph.SuppressLogging = false;
            }

            Assert.DoesNotContain(logLines, l => l.Contains("[DockGraph]"));
            Assert.False(MissionCrossTreeDock.SuppressLogging);
        }

        [Fact]
        public void Build_LetsTheSameTreeScannerKeepItsOwnEvidenceLine()
        {
            // The graph's guidRejected counter covers the CROSS-TREE scan only (the scanner
            // returns claims, not rejections), so a dock whose ONLY pid match is a same-tree
            // recording with a conclusively different launch guid classifies NoMatch here and its
            // only trace is the scanner's own summary. Fails if a future "reduce log noise"
            // change suppresses that line during the build - the would-be claim would then vanish
            // with no trace at all, exactly what design 6.3 step 2e's evidence rule forbids.
            BuildAll();

            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("SameTreeDock:") && l.Contains("tree=ta"));
        }

        [Fact]
        public void Build_SuppressLogging_AlsoSilencesTheSameTreeScanner()
        {
            // A per-frame caller that suppresses the graph must not leak the sibling's per-tree
            // summary into the log instead. Fails if the suppression stops propagating.
            DockEventGraph.SuppressLogging = true;
            try
            {
                BuildAll();
            }
            finally
            {
                DockEventGraph.SuppressLogging = false;
            }

            Assert.DoesNotContain(logLines, l => l.Contains("SameTreeDock:"));
        }

        // ------------------------------------------------------------------
        // TryDescribePartner: direction correctness (design 6.5)
        // ------------------------------------------------------------------

        [Fact]
        public void Describe_TwoParent_NamesTheOtherParent_FromEitherSide()
        {
            // The regression this exists for: naming the VIEWER's own vessel as its partner.
            var graph = BuildAll();

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "twoparentbp", "P", null, out DockPartnerDescription fromP));
            Assert.Equal("Vessel Q", fromP.PartnerVesselName);
            Assert.Equal("Q", fromP.PartnerRecordingId);
            Assert.Equal(DockPartnerStatus.TwoParentSameTree, fromP.Status);

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "twoparentbp", "Q", null, out DockPartnerDescription fromQ));
            Assert.Equal("Vessel P", fromQ.PartnerVesselName);
            Assert.Equal("P", fromQ.PartnerRecordingId);
        }

        [Fact]
        public void Describe_TwoParent_DeclinesForANonParentViewer()
        {
            // Viewed from the merged child both parents are equally "the partner"; naming one
            // would be arbitrary, and viewing from an unrelated recording is a caller bug.
            // Fails if the viewer is not validated (which would name parent[1] to everyone).
            var graph = BuildAll();

            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "twoparentbp", "PQ", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "twoparentbp", "A0", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "twoparentbp", null, null, out DockPartnerDescription _));
        }

        [Fact]
        public void Describe_CrossTree_IsViewerRelative()
        {
            // Both sides of the AB/CD dock get named from the SAME call (design 6.5): the
            // controller side sees the partner vessel, the partner side sees the merged stack.
            // Fails if the direction collapses to one side - the controller side (today the
            // unnamed one) would stay unnamed.
            var graph = BuildAll();

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "A0", null, out DockPartnerDescription fromController));
            Assert.Equal("Vessel B0", fromController.PartnerVesselName);
            Assert.Equal("B0", fromController.PartnerRecordingId);
            Assert.Equal("tb", fromController.PartnerTreeId);
            Assert.Equal(DockPartnerStatus.CrossTree, fromController.Status);

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "B0", null, out DockPartnerDescription fromPartner));
            Assert.Equal("Stack AB", fromPartner.PartnerVesselName);
            Assert.Equal("AB", fromPartner.PartnerRecordingId);
            Assert.Equal("ta", fromPartner.PartnerTreeId);
        }

        [Fact]
        public void Describe_SameTreeRecovered_IsViewerRelative()
        {
            // The A->D case named from both lines inside one tree.
            var graph = BuildAll();

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp2", "A1", null, out DockPartnerDescription fromA));
            Assert.Equal("Depot D", fromA.PartnerVesselName);
            Assert.Equal("D0", fromA.PartnerRecordingId);

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp2", "D0", null, out DockPartnerDescription fromD));
            Assert.Equal("Stack AD", fromD.PartnerVesselName);
            Assert.Equal("AD", fromD.PartnerRecordingId);
            Assert.Equal("ta", fromD.PartnerTreeId);
        }

        [Fact]
        public void Describe_CrossTree_DeclinesForANonParticipantViewer()
        {
            // The claimed line can carry MORE than one recording - a switch / chain continuation
            // D0 -> D1, where the claim resolves the EARLIEST (D0). A permissive "anyone who is
            // not the claimed recording is the controller side" rule would tell D1 that its dock
            // partner is D0: its own vessel, under its own name. Fails if the participant guard
            // is dropped. ("Q" is a recording of an unrelated tree, standing in for any id that
            // is neither a parent, the merged child, nor the claim.)
            var graph = BuildAll();

            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "Q", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "dockbp", null, null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "dockbp2", "B1", null, out DockPartnerDescription _));

            // The three legitimate viewers still answer: a parent, the merged child, the claim.
            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "A0", null, out DockPartnerDescription fromParent));
            Assert.Equal("Vessel B0", fromParent.PartnerVesselName);
            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "AB", null, out DockPartnerDescription fromChild));
            Assert.Equal("Vessel B0", fromChild.PartnerVesselName);
            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "B0", null, out DockPartnerDescription fromClaim));
            Assert.Equal("Stack AB", fromClaim.PartnerVesselName);
        }

        [Fact]
        public void Describe_PlumbsTheMissionNameResolver()
        {
            // The core stays free of Mission / MissionStore references by taking a resolver;
            // fails if the resolver is ignored (every row would lose its "(mission 'X')" half)
            // or is called with the wrong tree/recording pair.
            var graph = BuildAll();
            var seen = new List<string>();

            Assert.True(DockEventGraph.TryDescribePartner(
                graph, "dockbp", "A0",
                (treeId, recId) => { seen.Add(treeId + "/" + recId); return "CD Freighter"; },
                out DockPartnerDescription d));

            Assert.Equal("CD Freighter", d.PartnerMissionName);
            Assert.Equal(new[] { "tb/B0" }, seen);
        }

        [Fact]
        public void Describe_UnresolvableStatuses_StaySilent()
        {
            // Design Q4 / 6.4: the tables are silent exactly where they are silent today. The
            // generic "unrecorded vessel" text is the digest's concern (PR4), not this call's.
            var graph = BuildAll();

            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "zerobp", "PQ", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "nomatchbp", "PQ2", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "guidrejbp", "PQ3", null, out DockPartnerDescription _));
        }

        [Fact]
        public void Describe_UndockNodesAndUnknownIds_Decline()
        {
            // An Undock node has no partner by construction; an unknown id must not throw.
            var graph = BuildAll();

            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "undockbp", "AB", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                graph, "no-such-bp", "AB", null, out DockPartnerDescription _));
            Assert.False(DockEventGraph.TryDescribePartner(
                null, "dockbp", "A0", null, out DockPartnerDescription _));
        }

        // ------------------------------------------------------------------
        // Source-text gate (design 16.1 last bullet)
        // ------------------------------------------------------------------

        [Fact]
        public void TheGraphFilesNeverReadCommittedRecordings()
        {
            // The ERS grep gate (scripts/grep-audit-ers-els.ps1) fails CI on a raw
            // `.CommittedRecordings` read, and this design deliberately adds NO allowlist
            // entry: the pure core is parameter-injected and the host routes visibility through
            // EffectiveState.ComputeERS(). Fails the moment someone reaches for the flat
            // committed list in either file - here, with the reason, instead of in CI.
            string repoRoot = ResolveRepoRoot();
            foreach (string rel in new[]
            {
                "Source/Parsek/DockEventGraph.cs",
                "Source/Parsek/DockEventGraphCache.cs",
                // PR4's digest provider inherits the same contract: it is a consumer of the graph
                // and takes its tree, mission and resolvers as parameters.
                "Source/Parsek/MissionEventDigest.cs",
                // PR5's chapter derivation likewise: graph, tree, structure, composition roots and
                // the name resolver all arrive as parameters.
                "Source/Parsek/MissionChapters.cs",
                // PR6's seam-marker computation likewise: the graph, the loop unit's member
                // windows, the committed-index map and the name resolver all arrive as parameters
                // (the loop-unit builder, itself pure, hands them over).
                "Source/Parsek/LoopSeamMarkerBuilder.cs",
                // ...and PR6's RUNTIME half: the cursor / dedup / same-frame message-joining rules
                // live outside the engine and the policy so they are testable without a live host.
                "Source/Parsek/LoopSeamMarkerRuntime.cs",
            })
            {
                string path = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), "missing source file: " + rel);
                Assert.DoesNotContain(".CommittedRecordings", File.ReadAllText(path));
            }

            // The pure cores additionally touch NO live store at all (design 2.4). The digest
            // takes the Mission as a PARAMETER, so MissionStore is on its forbidden list too -
            // reaching for the store there is how a "pure" module quietly becomes untestable.
            foreach (string rel in new[]
            {
                "Source/Parsek/DockEventGraph.cs",
                "Source/Parsek/MissionEventDigest.cs",
                "Source/Parsek/MissionChapters.cs",
                "Source/Parsek/LoopSeamMarkerBuilder.cs",
                "Source/Parsek/LoopSeamMarkerRuntime.cs",
            })
            {
                string core = File.ReadAllText(
                    Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
                Assert.DoesNotContain("RecordingStore.", core);
                Assert.DoesNotContain("ParsekScenario.", core);
                Assert.DoesNotContain("EffectiveState.", core);
                Assert.DoesNotContain("MissionStore.", core);
            }
        }

        private static string ResolveRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
            {
                if (Directory.Exists(Path.Combine(dir, "scripts"))
                    && Directory.Exists(Path.Combine(dir, "Source")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException(
                "Could not locate repo root from " + AppContext.BaseDirectory);
        }
    }
}
