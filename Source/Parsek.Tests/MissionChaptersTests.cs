using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    // Chapter grouping (design-dock-event-graph.md 7.2, ChapterRoot shape 5.1, degradation 6.4,
    // edge cases 22 / 23 / 25, logging 15.2, test plan 16.1).
    //
    // ONE accreted two-tree fixture drives everything: the AB/CD cross-tree dock, the departing
    // piece that undocks off the merged stack (ForeignDockDeparture), the vessel-switch
    // continuation that later docks a same-tree partner (SwitchContinuation), a decouple and a
    // second dock INSIDE the departing piece's own line (so an expansion has to reach head keys,
    // /segN keys, @dockM keys and a downstream offshoot), and the degradation variants.
    //
    // [Collection("Sequential")] because ParsekLog's sink and the module suppression flags are
    // process-wide statics (and one cell drives MissionStore).
    [Collection("Sequential")]
    public class MissionChaptersTests : IDisposable
    {
        private const uint PidD = 300;
        private const uint PidB3 = 205;
        private const string GuidD = "dddddddddddddddddddddddddddddddd";
        private const string GuidB3 = "33333333333333333333333333333333";

        private readonly List<string> logLines = new List<string>();

        public MissionChaptersTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionChapters.SuppressLogging = false;
            DockEventGraph.SuppressLogging = true;          // not this file's subject
            MissionCrossTreeDock.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionChapters.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
        }

        // ------------------------------------------------------------------
        // Fixture
        //
        //  tb : B0 [0..100]                       - the foreign partner's solo pre-dock flight.
        //  ta : A0 [0..150] -dockbp(target=pid B, CROSS-TREE)-> AB [150..300]
        //       AB -undockbp-> { A1 [300..400] (continuing), B1 [300..340] (DEPARTED) }
        //       A1 -switchbp(VesselSwitchContinuation)-> A2 [400..450]
        //       A2 -dockbp2(target=pid D, SAME-TREE RECOVERED against D0)-> AD [450..600]
        //       B1 -b1split(DECOUPLE)-> { B2 [340..360] (continuing), B3 [340..350] (peel) }
        //       B2 -b2dock(target=0, UNSTAMPED)-> B4 [360..400]
        //       D0 [320..380] is A2's same-tree partner line (terminal-stamped, parentless).
        //
        //  Composition keys this produces (verified by ExpandChapter_* below):
        //    A0's line  : "A0" [0,150)  "A0@dock1" [150,300)  "A0/seg1" [300,450)
        //                 "A0/seg1@dock1" [450,600)
        //    B1's line  : "B1" [300,340)  "B1/seg1" [340,360)  "B1/seg1@dock1" [360,400)
        //    peels/roots: "B3", "D0"
        // ------------------------------------------------------------------

        private static RecordingTree PartnerTree()
            => CrossTreeDockFixture.Tree("tb", new[]
            {
                CrossTreeDockFixture.Rec("B0", CrossTreeDockFixture.PidB,
                    CrossTreeDockFixture.GuidB, "CB", 0, 0, 100, vessel: "Freighter CD"),
            });

        private static RecordingTree ControllerTree(
            uint dockTargetPid = CrossTreeDockFixture.PidB,
            bool departingChildIsDebris = false)
        {
            var a0 = CrossTreeDockFixture.Rec("A0", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA", 0, 0, 150, pods: 1, probes: 0,
                childBp: "dockbp", vessel: "Ship A");
            var ab = CrossTreeDockFixture.Rec("AB", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAB", 0, 150, 300, pods: 1, probes: 1,
                parentBp: "dockbp", childBp: "undockbp", vessel: "Stack AB");
            var a1 = CrossTreeDockFixture.Rec("A1", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA2", 0, 300, 400, pods: 1, probes: 0,
                parentBp: "undockbp", childBp: "switchbp", vessel: "Ship A");
            var a2 = CrossTreeDockFixture.Rec("A2", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA3", 0, 400, 450, pods: 1, probes: 0,
                parentBp: "switchbp", childBp: "dockbp2", vessel: "Ship A");
            var ad = CrossTreeDockFixture.Rec("AD", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAD", 0, 450, 600, pods: 1, probes: 1,
                parentBp: "dockbp2", vessel: "Stack AD");
            var d0 = CrossTreeDockFixture.Rec("D0", PidD, GuidD, "CD", 0, 320, 380,
                vessel: "Depot D");
            var b1 = CrossTreeDockFixture.Rec("B1", CrossTreeDockFixture.PidB,
                CrossTreeDockFixture.GuidB, "CB2", 0, 300, 340, parentBp: "undockbp",
                childBp: "b1split", debris: departingChildIsDebris, vessel: "Probe B1");
            var b2 = CrossTreeDockFixture.Rec("B2", CrossTreeDockFixture.PidB,
                CrossTreeDockFixture.GuidB, "CB3", 0, 340, 360, parentBp: "b1split",
                childBp: "b2dock", vessel: "Probe B1");
            var b3 = CrossTreeDockFixture.Rec("B3", PidB3, GuidB3, "CB4", 0, 340, 350,
                parentBp: "b1split", vessel: "Panel B3");
            var b4 = CrossTreeDockFixture.Rec("B4", CrossTreeDockFixture.PidB,
                CrossTreeDockFixture.GuidB, "CB5", 0, 360, 400, parentBp: "b2dock",
                vessel: "Probe B1");

            return CrossTreeDockFixture.Tree("ta",
                new[] { a0, ab, a1, a2, ad, d0, b1, b2, b3, b4 },
                new[]
                {
                    CrossTreeDockFixture.BP("dockbp", BranchPointType.Dock, 150,
                        new[] { "A0" }, new[] { "AB" },
                        targetPid: dockTargetPid, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("undockbp", BranchPointType.Undock, 300,
                        new[] { "AB" }, new[] { "A1", "B1" }, splitCause: "UNDOCK"),
                    CrossTreeDockFixture.BP("switchbp", BranchPointType.VesselSwitchContinuation,
                        400, new[] { "A1" }, new[] { "A2" }),
                    CrossTreeDockFixture.BP("dockbp2", BranchPointType.Dock, 450,
                        new[] { "A2" }, new[] { "AD" }, targetPid: PidD, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("b1split", BranchPointType.JointBreak, 340,
                        new[] { "B1" }, new[] { "B2", "B3" }, splitCause: "DECOUPLE"),
                    CrossTreeDockFixture.BP("b2dock", BranchPointType.Dock, 360,
                        new[] { "B2" }, new[] { "B4" }, targetPid: 0, mergeCause: "DOCK"),
                });
        }

        private static List<RecordingTree> AllTrees(
            uint dockTargetPid = CrossTreeDockFixture.PidB, bool departingChildIsDebris = false)
            => new List<RecordingTree>
            {
                PartnerTree(),
                ControllerTree(dockTargetPid, departingChildIsDebris),
            };

        private static List<ChapterRoot> Roots(
            List<RecordingTree> trees, Func<string, string, string> nameResolver = null)
        {
            DockEventGraph graph = DockEventGraph.Build(trees, null);
            return MissionChapters.CollectChapterRoots(graph, trees[1], nameResolver);
        }

        private static (MissionStructure structure, List<MissionCompositionNode> comp) Derive(
            RecordingTree tree)
        {
            MissionStructure s = MissionStructureBuilder.Build(tree);
            return (s, MissionCompositionBuilder.Build(s));
        }

        private static ChapterRoot RootFor(List<ChapterRoot> roots, string recordingId)
        {
            for (int i = 0; i < roots.Count; i++)
                if (roots[i].RootRecordingId == recordingId)
                    return roots[i];
            throw new InvalidOperationException("no chapter root for " + recordingId);
        }

        // ------------------------------------------------------------------
        // Roots
        // ------------------------------------------------------------------

        [Fact]
        public void CollectRoots_DerivesTheSwitchContinuationChapter()
        {
            // Design 7.2 kind 1: the vessel the player switched to and kept flying is its own
            // sub-story. Catches the switch child being missed because it is ChildRecordingIds[0]
            // of its branch point (which is exactly what the recorder writes) - a filter borrowed
            // from the undock rule would silently delete this whole chapter kind.
            ChapterRoot root = RootFor(Roots(AllTrees()), "A2");

            Assert.Equal(ChapterKind.SwitchContinuation, root.Kind);
            Assert.Equal("switchbp", root.SourceBranchPointId);
            Assert.Equal("Continuation: Ship A", root.Title);
        }

        [Fact]
        public void CollectRoots_DerivesTheForeignDockDepartureChapter()
        {
            // Design 7.2 kind 2: the piece that left after a rendezvous with someone else's
            // vessel. Catches the departure being attributed to the wrong side (the continuing
            // child) or the partner going unnamed.
            ChapterRoot root = RootFor(Roots(AllTrees()), "B1");

            Assert.Equal(ChapterKind.ForeignDockDeparture, root.Kind);
            Assert.Equal("undockbp", root.SourceBranchPointId);
            Assert.Equal("After docking with Freighter CD: Probe B1 departed", root.Title);
        }

        [Fact]
        public void CollectRoots_ForeignPartnerTitle_CarriesTheOtherMissionName()
        {
            // The partner lives in another tree, so its mission is worth naming (design 7.2 /
            // 6.5). Catches the resolver being ignored, and catches a SAME-tree partner getting a
            // pointless "(mission 'this same mission')" suffix.
            var roots = Roots(AllTrees(),
                (treeId, recId) => treeId == "tb" ? "CD Freighter" : "AB");

            Assert.Equal("After docking with Freighter CD (mission 'CD Freighter'): Probe B1 departed",
                RootFor(roots, "B1").Title);
            Assert.Equal("Continuation: Ship A", RootFor(roots, "A2").Title);
        }

        [Fact]
        public void CollectRoots_UnstampedDock_MintsNoDepartureChapter()
        {
            // Design 6.4 row 1 (the degradation contract): an old recording whose dock carries
            // TargetVesselPersistentId = 0 resolves UnstampedZero, so there is no foreign partner
            // to name and no foreign-departure chapter - while the switch-continuation chapter,
            // which needs no pid, still derives. Catches a chapter appearing on unresolved docks
            // with a placeholder partner name (the exact "degrade to today" violation).
            var roots = Roots(AllTrees(dockTargetPid: 0));

            Assert.Single(roots);
            Assert.Equal("A2", roots[0].RootRecordingId);
            Assert.Equal(ChapterKind.SwitchContinuation, roots[0].Kind);
        }

        [Fact]
        public void CollectRoots_DebrisDepartingChild_MintsNoChapter()
        {
            // Design 10 edge case 25's sibling: debris is never a leg, so a debris chapter could
            // only ever be an empty header. Catches a colliding debris pid minting a false
            // chapter above rows it does not own.
            var roots = Roots(AllTrees(departingChildIsDebris: true));

            Assert.Single(roots);
            Assert.Equal("A2", roots[0].RootRecordingId);
        }

        [Fact]
        public void CollectRoots_ContinuingUndockChild_IsNeverAChapter()
        {
            // Design 7.2: index 0 is the CONTINUING child - the mission's own vessel carrying on,
            // not a departure. Catches an off-by-one in the child scan, which would wrap the
            // mission's own main line in a "departed" chapter and let one toggle drop it.
            var roots = Roots(AllTrees());

            foreach (ChapterRoot r in roots)
                Assert.NotEqual("A1", r.RootRecordingId);
        }

        [Fact]
        public void CollectRoots_DualQualification_KeepsOneRootAndSwitchWins()
        {
            // Design 7.2's tie-break. The shape is hand-built (a recording has one origin branch
            // point in production), but the dedup must not depend on that: SwitchContinuation is
            // the structural fact, ForeignDockDeparture is the labelled heuristic, so a recording
            // reachable both ways must read as the switch. Catches a duplicate header pair over
            // one row, and catches the heuristic overwriting the structural title.
            List<RecordingTree> trees = AllTrees();
            trees[1].BranchPoints.Add(CrossTreeDockFixture.BP(
                "switch2", BranchPointType.VesselSwitchContinuation, 300,
                new[] { "AB" }, new[] { "B1" }));

            var roots = Roots(trees);

            int b1Roots = 0;
            foreach (ChapterRoot r in roots)
                if (r.RootRecordingId == "B1")
                {
                    b1Roots++;
                    Assert.Equal(ChapterKind.SwitchContinuation, r.Kind);
                }
            Assert.Equal(1, b1Roots);
        }

        [Fact]
        public void CollectRoots_OrderedByRootStartUT()
        {
            // Deterministic order (design 7.2): the headers must appear in the same order across
            // loads, or the Missions tab reshuffles its group headers between sessions.
            var roots = Roots(AllTrees());

            Assert.Equal(new[] { "B1", "A2" },
                new[] { roots[0].RootRecordingId, roots[1].RootRecordingId });
        }

        [Fact]
        public void CollectRoots_NullGraph_StillDerivesSwitchChapters()
        {
            // Design 6.4: switch-continuation chapters never needed the pid, so they survive a
            // caller with no graph at all (the reconcile path builds one, but a future caller may
            // not). Catches a null-graph dereference on that path.
            var roots = MissionChapters.CollectChapterRoots(null, ControllerTree(), null);

            Assert.Single(roots);
            Assert.Equal(ChapterKind.SwitchContinuation, roots[0].Kind);
        }

        [Fact]
        public void CollectRoots_LogsTheDerivationSummary()
        {
            // Design 15.2's one grep-stable line per derivation. Catches the diagnostic being
            // dropped, which is how a chapter that silently fails to derive becomes undebuggable
            // from the log alone.
            Roots(AllTrees());

            Assert.Contains(logLines, l => l.Contains("[Mission]")
                && l.Contains("Chapters: tree=ta roots=2 switch=1 foreignDeparture=1"));
        }

        // ------------------------------------------------------------------
        // Expansion
        // ------------------------------------------------------------------

        [Fact]
        public void ExpandChapter_ForeignDeparture_CollectsTheWholeDownstreamKeySet()
        {
            // Design 7.2: the toggle must reach EVERY selectable key the sub-story owns - the
            // head key, the /segN the decouple cut, the @dockM the later dock cut, and the peeled
            // branch's own line. Catches "chapter exclusion silently missing downstream
            // intervals", i.e. a chapter you dropped still rendering half of itself.
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);

            HashSet<string> keys = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "B1"), structure, comp);

            Assert.Equal(
                new SortedSet<string>(new[] { "B1", "B1/seg1", "B1/seg1@dock1", "B3" },
                    StringComparer.Ordinal),
                new SortedSet<string>(keys, StringComparer.Ordinal));
        }

        [Fact]
        public void ExpandChapter_SwitchContinuation_TakesOnlyIntervalsAtOrAfterTheSwitch()
        {
            // The documented UT floor (see ExpandChapterToIntervalKeys' doc comment): a switch
            // child is folded into the SAME physical vessel's line as the pre-switch legs, so
            // taking the whole owning line would put the mission's LAUNCH inside the chapter.
            // Here the post-switch dock's @dock1 key is the only interval that begins at or after
            // the switch. Catches the two failures that matter: swallowing the launch interval,
            // and (the mirror) returning nothing at all when a post-switch edge exists.
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);

            HashSet<string> keys = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "A2"), structure, comp);

            Assert.Equal(new[] { "A0/seg1@dock1" }, new List<string>(keys).ToArray());
        }

        [Fact]
        public void ExpandChapter_NeverReachesAnotherVesselsLine()
        {
            // Chapters are a grouping over the mission's OWN rows: the same-tree partner D0 and
            // the pre-switch launch intervals are outside every chapter here. Catches an
            // expansion that walks branch PARENTS (or the whole tree) and lets one toggle drop
            // vessels the chapter never contained.
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);
            var roots = Roots(trees);

            var union = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < roots.Count; i++)
                union.UnionWith(MissionChapters.ExpandChapterToIntervalKeys(
                    roots[i], structure, comp));

            Assert.DoesNotContain("D0", union);
            Assert.DoesNotContain("A0", union);
            Assert.DoesNotContain("A0@dock1", union);
            Assert.DoesNotContain("A0/seg1", union);
        }

        [Fact]
        public void ExpandChapter_ThroughATwoParentMerge_LeavesThePartnersPreDockFlightAlone()
        {
            // The per-line ENTRY UT (see CollectDownstreamLineEntries). A chapter whose vessel
            // later DOCKS someone reaches the merged stack, and the merged stack may be walked as
            // a member of the PARTNER's through-line - so a naive "every line the closure touches,
            // from the chapter root onward" rule would hand one click the partner's entire solo
            // flight. Both pre-dock intervals start at UT 0 here, so the assertion holds whichever
            // line the composition's shared visited set awards the merged stack to. Catches the
            // worst possible failure of a bulk toggle: dropping a vessel the chapter never
            // contained.
            var x0 = CrossTreeDockFixture.Rec("X0", 700, "70000000000000000000000000000000",
                "CX", 0, 0, 50, pods: 1, probes: 0, childBp: "xsplit", vessel: "Ship X");
            var x1 = CrossTreeDockFixture.Rec("X1", 700, "70000000000000000000000000000000",
                "CX2", 0, 50, 100, pods: 1, probes: 0, parentBp: "xsplit", childBp: "mrg",
                vessel: "Ship X");
            var e0 = CrossTreeDockFixture.Rec("E0", 800, "80000000000000000000000000000000",
                "CE", 0, 0, 100, childBp: "mrg", vessel: "Station E");
            var xe = CrossTreeDockFixture.Rec("XE", 700, "70000000000000000000000000000000",
                "CXE", 0, 100, 200, pods: 1, probes: 1, parentBp: "mrg", vessel: "Stack XE");
            RecordingTree tree = CrossTreeDockFixture.Tree("tm", new[] { e0, x0, x1, xe },
                new[]
                {
                    CrossTreeDockFixture.BP("xsplit", BranchPointType.VesselSwitchContinuation,
                        50, new[] { "X0" }, new[] { "X1" }),
                    CrossTreeDockFixture.BP("mrg", BranchPointType.Dock, 100,
                        new[] { "X1", "E0" }, new[] { "XE" }, mergeCause: "DOCK"),
                });
            var (structure, comp) = Derive(tree);
            var roots = MissionChapters.CollectChapterRoots(null, tree, null);

            HashSet<string> keys = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(roots, "X1"), structure, comp);

            // The merged stack DOES belong to the chapter - the chapter's vessel is half of it.
            // This is the positive half that catches the collapse the multimap exists for: with a
            // one-head-per-leg map the merged interval's owner never enters the reachable set,
            // the chapter's key set comes back EMPTY, and the header + its toggle vanish from the
            // tab with nothing to show for it.
            Assert.Contains("E0@dock1", keys);
            Assert.DoesNotContain("E0", keys);   // the partner's solo pre-dock flight
            Assert.DoesNotContain("X0", keys);   // the chapter's own pre-switch flight
        }

        [Fact]
        public void ExpandChapter_UnresolvableRoot_ReturnsEmptyNotNull()
        {
            // A root whose recording is gone (a topology change between derivation and use).
            // Catches a null return blowing up the row walker, and catches an empty chapter
            // being treated as "everything".
            var (structure, comp) = Derive(ControllerTree());

            HashSet<string> keys = MissionChapters.ExpandChapterToIntervalKeys(
                new ChapterRoot { RootRecordingId = "nope" }, structure, comp);

            Assert.NotNull(keys);
            Assert.Empty(keys);
        }

        [Fact]
        public void ExpandChapter_IsStableAcrossAnOptimizerSplitInsideTheChapter()
        {
            // Design 10 edge case 23. The optimizer's split convention is that the EARLIEST half
            // keeps the original recording id and the tail gets a new one on the same chain
            // (design-mission-abstractions.md open question 3b), so a through-line head - and
            // hence every key rooted on it - survives the cut. This drives the OUTCOME of that
            // convention by construction rather than through RecordingOptimizer.SplitAtSection:
            // that entry point needs full TrackSection + point fixtures, while the invariant under
            // test is purely the id/chain convention, which the construction reproduces exactly.
            // Catches a key set that renumbers under optimizer churn, which would silently
            // re-target (or strand) a player's chapter exclusion after any later flight.
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);
            HashSet<string> before = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "B1"), structure, comp);

            SplitB2InTwo(trees[1]);
            var (splitStructure, splitComp) = Derive(trees[1]);
            HashSet<string> after = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "B1"), splitStructure, splitComp);

            Assert.Equal(
                new SortedSet<string>(before, StringComparer.Ordinal),
                new SortedSet<string>(after, StringComparer.Ordinal));
        }

        // Splits B2 [340..360] into B2 [340..350] + B2b [350..360] the way the optimizer does:
        // the first half KEEPS the id, the tail is a new recording on the same (ChainId,
        // ChainBranch) at the next ChainIndex, and the outgoing branch point moves to the tail.
        private static void SplitB2InTwo(RecordingTree tree)
        {
            Recording b2 = tree.Recordings["B2"];
            b2.ExplicitEndUT = 350;
            b2.ChildBranchPointId = null;
            Recording b2b = CrossTreeDockFixture.Rec("B2b", CrossTreeDockFixture.PidB,
                CrossTreeDockFixture.GuidB, "CB3", 1, 350, 360, childBp: "b2dock",
                vessel: "Probe B1");
            b2b.TreeId = tree.Id;
            tree.Recordings["B2b"] = b2b;
            for (int i = 0; i < tree.BranchPoints.Count; i++)
                if (tree.BranchPoints[i].Id == "b2dock")
                    tree.BranchPoints[i].ParentRecordingIds = new List<string> { "B2b" };
        }

        [Fact]
        public void ResolveAnchorKey_IsTheChaptersEarliestInterval()
        {
            // The header row must sit above the chapter's FIRST row, not somewhere in its middle.
            // Catches an anchor picked by hash order (which would drift between loads) or by the
            // root recording id (which does not exist as a key for a switch chapter).
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);
            var roots = Roots(trees);

            Assert.Equal("B1", MissionChapters.ResolveChapterAnchorKey(
                MissionChapters.ExpandChapterToIntervalKeys(RootFor(roots, "B1"), structure, comp),
                comp));
            Assert.Equal("A0/seg1@dock1", MissionChapters.ResolveChapterAnchorKey(
                MissionChapters.ExpandChapterToIntervalKeys(RootFor(roots, "A2"), structure, comp),
                comp));
            Assert.Null(MissionChapters.ResolveChapterAnchorKey(
                new HashSet<string>(StringComparer.Ordinal), comp));
        }

        // ------------------------------------------------------------------
        // Tri-state + the toggle's write-through
        // ------------------------------------------------------------------

        [Fact]
        public void ClassifyChapterState_ReachesAllThreeStates()
        {
            // The tri-state checkbox's whole input (design 7.2). Catches Mixed collapsing into one
            // of the extremes, which would make one click on a partially-trimmed chapter do the
            // opposite of what the checkbox shows.
            var keys = new HashSet<string>(new[] { "k1", "k2" }, StringComparer.Ordinal);

            Assert.Equal(ChapterSelectionState.AllIncluded,
                MissionChapters.ClassifyChapterState(keys, new HashSet<string>()));
            Assert.Equal(ChapterSelectionState.Mixed,
                MissionChapters.ClassifyChapterState(keys,
                    new HashSet<string>(new[] { "k1" }, StringComparer.Ordinal)));
            Assert.Equal(ChapterSelectionState.AllExcluded,
                MissionChapters.ClassifyChapterState(keys,
                    new HashSet<string>(new[] { "k1", "k2" }, StringComparer.Ordinal)));
            // An empty chapter reads AllIncluded: nothing is excluded, and rendering "all
            // excluded" would claim the player dropped something they never had.
            Assert.Equal(ChapterSelectionState.AllIncluded,
                MissionChapters.ClassifyChapterState(
                    new HashSet<string>(StringComparer.Ordinal),
                    new HashSet<string>(new[] { "k1" }, StringComparer.Ordinal)));
        }

        [Fact]
        public void ChapterToggle_WritesExplicitKeysAndLeavesEverythingElseAlone()
        {
            // Design 7.2 / edge case 25: the toggle is a BULK EDIT through the existing
            // ExcludedIntervalKeys mechanism - explicit keys in, explicit keys out, no cascade and
            // no new namespace. This pins the pure half of the IMGUI handler (the handler itself
            // is covered by the source-text wiring gate). Catches an exclusion that leaks onto
            // rows outside the chapter, and catches an include that fails to fully undo itself.
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);
            HashSet<string> chapterKeys = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "B1"), structure, comp);

            var excluded = new HashSet<string>(new[] { "A0" }, StringComparer.Ordinal);

            excluded.UnionWith(chapterKeys);
            Assert.Equal(ChapterSelectionState.AllExcluded,
                MissionChapters.ClassifyChapterState(chapterKeys, excluded));
            Assert.Contains("A0", excluded);              // an unrelated trim is untouched
            Assert.DoesNotContain("A0/seg1@dock1", excluded);

            excluded.ExceptWith(chapterKeys);
            Assert.Equal(ChapterSelectionState.AllIncluded,
                MissionChapters.ClassifyChapterState(chapterKeys, excluded));
            Assert.Equal(new[] { "A0" }, new List<string>(excluded).ToArray());
        }

        // ------------------------------------------------------------------
        // Q6 reconcile observation
        // ------------------------------------------------------------------

        [Fact]
        public void WarnPredicate_RaisesOnlyOnAPartiallyExcludedChapter()
        {
            // Design Q6 / edge case 22, and the documented v1 approximation: with no persisted
            // per-chapter snapshot the observable shadow of "new topology appeared inside an
            // excluded chapter" is exactly Mixed. Catches the predicate firing on a fully
            // excluded chapter (nothing new appeared) or on a fully included one (nothing was
            // ever excluded) - either would make the warning noise nobody reads.
            var keys = new HashSet<string>(new[] { "k1", "k2", "k3" }, StringComparer.Ordinal);

            Assert.False(MissionChapters.ShouldWarnNewTopologyInsideExcludedChapter(
                keys, new HashSet<string>(), out int _));
            Assert.False(MissionChapters.ShouldWarnNewTopologyInsideExcludedChapter(
                keys, new HashSet<string>(keys, StringComparer.Ordinal), out int _));
            Assert.True(MissionChapters.ShouldWarnNewTopologyInsideExcludedChapter(
                keys, new HashSet<string>(new[] { "k1", "k2" }, StringComparer.Ordinal),
                out int included));
            Assert.Equal(1, included);
        }

        [Fact]
        public void ReconcileSelections_WarnsAboutNewTopologyInsideAnExcludedChapter_AndWritesNothing()
        {
            // The Q6 hook end to end (design 4's MissionStore row): the reconcile REPORTS and does
            // not auto-extend - auto-extending would be a silent write to player selection state,
            // and the standing open-question-3a contract says a newly recorded branch defaults to
            // included. Catches both halves: a missing warning (the player never learns their
            // excluded chapter grew a live piece) and a helpful "fix" that edits the selection.
            MissionStore.ResetForTesting();
            MissionStore.SuppressLogging = false;
            List<RecordingTree> trees = AllTrees();
            var (structure, comp) = Derive(trees[1]);
            HashSet<string> chapterKeys = MissionChapters.ExpandChapterToIntervalKeys(
                RootFor(Roots(trees), "B1"), structure, comp);

            MissionStore.EnsureDefaultsForTrees(trees);
            Mission m = null;
            foreach (Mission candidate in MissionStore.Missions)
                if (candidate.TreeId == "ta")
                    m = candidate;
            Assert.NotNull(m);
            // The chapter was excluded when it held everything but B3 - B3 is the "new branch
            // recorded later inside the excluded chapter".
            foreach (string k in chapterKeys)
                if (k != "B3")
                    m.ExcludedIntervalKeys.Add(k);
            int excludedBefore = m.ExcludedIntervalKeys.Count;
            logLines.Clear();

            MissionStore.ReconcileSelections(trees);

            Assert.Contains(logLines, l => l.Contains("[WARN]")
                && l.Contains("has new included topology")
                && l.Contains("After docking with Freighter CD"));
            Assert.Equal(excludedBefore, m.ExcludedIntervalKeys.Count);
            Assert.DoesNotContain("B3", m.ExcludedIntervalKeys);
        }
    }
}
