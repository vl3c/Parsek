using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    // The mission event digest (design-dock-event-graph.md 7.1, = issue-1's T2.3). The fixture is
    // the AB/CD two-tree shape plus the A->D same-tree recovery and a degradation tree, mirroring
    // DockEventGraphTests so a status reached there has a row asserted here.
    // [Collection("Sequential")]: ParsekLog's sink and the module suppression flags are
    // process-wide statics.
    [Collection("Sequential")]
    public class MissionEventDigestTests : IDisposable
    {
        private const uint PidD = 300;
        private const uint PidP = 500;
        private const uint PidQ = 501;
        private const uint PidNowhere = 999_999;
        private const string GuidD = "dddddddddddddddddddddddddddddddd";
        private const string GuidP = "pppppppppppppppppppppppppppppppp";
        private const string GuidQ = "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq";

        private const double Dock2UT = 500.0;

        private readonly List<string> logLines = new List<string>();

        public MissionEventDigestTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionEventDigest.SuppressLogging = false;
            DockEventGraph.SuppressLogging = true;   // the graph's own summary is not under test
            MissionCrossTreeDock.SuppressLogging = true;
            ParsekTimeFormat.KerbinTimeOverrideForTesting = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            ParsekTimeFormat.ResetForTesting();
            MissionEventDigest.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
        }

        // ------------------------------------------------------------------
        // Fixture (same shape as DockEventGraphTests):
        //  tb : B0 - the partner's solo pre-dock flight [0..100].
        //  ta : A0 -> dockbp(target=pid B, CROSS-TREE) -> AB -> undockbp -> { A1, B1 }
        //       A1 -> dockbp2(target=pid D, SAME-TREE RECOVERED) -> AD ; D0 is a disconnected
        //       root (the A->D cross-session shape).
        //  tc : P + Q -> twoparentbp -> PQ -> zerobp(UNSTAMPED) -> PQ2 -> nomatchbp(NO MATCH)
        //       -> PQ3.
        // ------------------------------------------------------------------

        private static RecordingTree PartnerTree(double b0End = 100)
            => CrossTreeDockFixture.Tree("tb", new[]
            {
                CrossTreeDockFixture.Rec("B0", CrossTreeDockFixture.PidB, CrossTreeDockFixture.GuidB,
                    "CB", 0, 0, b0End),
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
                parentBp: "nomatchbp");

            return CrossTreeDockFixture.Tree("tc",
                new[] { p, q, pq, pq2, pq3 },
                new[]
                {
                    CrossTreeDockFixture.BP("twoparentbp", BranchPointType.Dock, 50.0,
                        new[] { "P", "Q" }, new[] { "PQ" }, targetPid: PidQ, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("zerobp", BranchPointType.Dock, 100.0,
                        new[] { "PQ" }, new[] { "PQ2" }, targetPid: 0, mergeCause: "DOCK"),
                    CrossTreeDockFixture.BP("nomatchbp", BranchPointType.Dock, 150.0,
                        new[] { "PQ2" }, new[] { "PQ3" }, targetPid: PidNowhere, mergeCause: "DOCK"),
                });
        }

        // (treeId, recordingId) -> mission name / treeId -> mission id: the two resolvers the UI
        // supplies so the pure core never references MissionStore.
        private static string MissionName(string treeId, string recordingId)
        {
            switch (treeId)
            {
                case "ta": return "AB";
                case "tb": return "CD Freighter";
                default: return null;
            }
        }

        private static string MissionId(string treeId) => "mission-" + treeId;

        private static Mission MissionFor(string treeId)
            => new Mission("mission-" + treeId, treeId, MissionName(treeId, null) ?? treeId);

        private static List<RecordingTree> AllTrees(double b0End = 100)
            => new List<RecordingTree> { PartnerTree(b0End), ControllerTree(), DegradationTree() };

        private static (DockEventGraph graph, List<RecordingTree> trees) BuildGraph(
            double b0End = 100, Func<string, bool> visible = null)
        {
            List<RecordingTree> trees = AllTrees(b0End);
            return (DockEventGraph.Build(trees, visible), trees);
        }

        private static RecordingTree Tree(List<RecordingTree> trees, string id)
            => trees.Find(t => t.Id == id);

        private static List<MissionEventRow> Digest(
            string treeId, double b0End = 100, Func<string, bool> visible = null,
            Mission mission = null)
        {
            var (graph, trees) = BuildGraph(b0End, visible);
            return MissionEventDigest.Build(graph, Tree(trees, treeId),
                mission ?? MissionFor(treeId), MissionName, MissionId);
        }

        private static MissionEventRow Row(List<MissionEventRow> rows, string bpId, string verb)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].SourceBranchPointId == bpId && rows[i].Verb == verb)
                    return rows[i];
            throw new InvalidOperationException(
                "no row for bp=" + (bpId ?? "<null>") + " verb=" + verb);
        }

        // ------------------------------------------------------------------
        // Order + the controller column (design 1.2 left column)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_IsOrderedByUT()
        {
            // The digest IS the chronological story; an unordered list is not a story. Fails if
            // the sort is dropped, or if a later-added row source (terminal / gap) is appended
            // instead of merged.
            var rows = Digest("ta");

            for (int i = 1; i < rows.Count; i++)
                Assert.True(rows[i - 1].UT <= rows[i].UT,
                    "row " + i + " (" + rows[i].Verb + ") is out of order");
            Assert.Equal(
                new[] { "Launched", "Docked with", "Undocked", "Launched", "Docked with" },
                rows.ConvertAll(r => r.Verb).ToArray());
        }

        [Fact]
        public void Digest_ControllerTree_NamesThePartnerAndItsMission()
        {
            // The left column of the worked example: "B docked with CD (mission 'CD Freighter')"
            // with a GoTo. Fails if the digest loses the partner half (back to today's silent
            // composition label) or wires the GoTo to its own side of the dock.
            var row = Row(Digest("ta"), "dockbp", MissionEventDigest.VerbDockedWith);

            Assert.Equal(CrossTreeDockFixture.DockUT, row.UT);
            Assert.Equal("Vessel A0", row.SubjectName);
            Assert.Equal("Vessel B0 (mission 'CD Freighter')", row.PartnerText);
            Assert.Equal("B0", row.GoToRecordingId);
            Assert.Equal("mission-tb", row.GoToMissionId);
            Assert.Equal("Vessel A0 docked with Vessel B0 (mission 'CD Freighter')",
                MissionEventDigest.FormatRowText(row));
        }

        [Fact]
        public void Digest_ControllerTree_UndockRowNamesTheDepartingVessel()
        {
            // The story of an undock is the piece that LEFT, not the stack that continued
            // (child [0] is the continuing vessel by recorder convention). Fails if the row
            // names the continuing side - the mission would read "A undocked" at every split.
            var row = Row(Digest("ta"), "undockbp", MissionEventDigest.VerbUndocked);

            Assert.Equal(CrossTreeDockFixture.UndockUT, row.UT);
            Assert.Equal("Vessel B1", row.SubjectName);
            Assert.Equal("", row.PartnerText);
            Assert.Null(row.GoToRecordingId);
        }

        [Fact]
        public void Digest_MultipleRoots_EachGetsItsOwnLaunchRow()
        {
            // Design edge case 13: a tree can hold recordings with no incoming branch-point edge
            // (a post-switch line, and here D's own pre-dock flight). Fails if launch rows come
            // off RootRecordingId alone - the second line would begin with no beginning.
            var launches = Digest("ta").FindAll(r => r.Verb == MissionEventDigest.VerbLaunched);

            Assert.Equal(2, launches.Count);
            Assert.Contains(launches, r => r.SubjectName == "Vessel A0" && r.UT == 0);
            Assert.Contains(launches, r => r.SubjectName == "Depot D" && r.UT == 320);
        }

        [Fact]
        public void Digest_SameTreeRecoveredDock_IsNamedOnceWithNoCrossMissionGoTo()
        {
            // The A->D shape: both sides live in ONE tree, so the dock is named (from the
            // recovered claim) but appears exactly once and offers no navigation to the mission
            // the player is already reading (design Q8). Fails if the node is emitted twice
            // (owning + partner side) or mints a self-referential GoTo.
            var rows = Digest("ta");
            var dock2 = rows.FindAll(r => r.SourceBranchPointId == "dockbp2");

            Assert.Single(dock2);
            Assert.Equal(MissionEventDigest.VerbDockedWith, dock2[0].Verb);
            Assert.Equal("Vessel A1", dock2[0].SubjectName);
            Assert.Equal("Depot D", dock2[0].PartnerText);   // same tree: no "(mission ...)" half
            Assert.Null(dock2[0].GoToRecordingId);
            Assert.Null(dock2[0].GoToMissionId);
        }

        // ------------------------------------------------------------------
        // The partner column (design 1.2 right column)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_PartnerTree_ShowsTheDockItNeverRecorded()
        {
            // The whole point of registering a cross-tree node under BOTH trees: mission CD must
            // learn that its vessel was consumed by a dock recorded elsewhere, and be able to go
            // there. Fails if the digest only walks the tree's OWN branch points - the partner
            // mission would go back to knowing nothing.
            var rows = Digest("tb");

            Assert.Equal(2, rows.Count);
            Assert.Equal(MissionEventDigest.VerbLaunched, rows[0].Verb);
            Assert.Equal("Vessel B0", rows[0].SubjectName);

            MissionEventRow dockedBy = rows[1];
            Assert.Equal(MissionEventDigest.VerbDockedBy, dockedBy.Verb);
            Assert.Equal(CrossTreeDockFixture.DockUT, dockedBy.UT);
            Assert.Equal("Vessel B0", dockedBy.SubjectName);
            Assert.Equal("Stack AB (mission 'AB')", dockedBy.PartnerText);
            Assert.Equal("AB", dockedBy.GoToRecordingId);
            Assert.Equal("mission-ta", dockedBy.GoToMissionId);
            Assert.Equal("dockbp", dockedBy.SourceBranchPointId);
        }

        // ------------------------------------------------------------------
        // Degradation (design 6.4 / Q4)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_NoMatch_UsesTheGenericUnrecordedText()
        {
            // Design Q4: the generic text lives in the DIGEST only (the tables stay silent). A
            // stamped dock whose partner was never recorded is still a real event, and saying so
            // is honest; fails if the row goes silent (an unexplained composition jump) or if the
            // text leaks into TryDescribePartner's contract.
            var row = Row(Digest("tc"), "nomatchbp", MissionEventDigest.VerbDockedWith);

            Assert.Equal(MissionEventDigest.UnrecordedPartnerText, row.PartnerText);
            Assert.Null(row.GoToRecordingId);
            Assert.Equal("Vessel PQ2 docked with an unrecorded vessel",
                MissionEventDigest.FormatRowText(row));
        }

        [Fact]
        public void Digest_UnstampedZero_EmitsAVerbOnlyRow()
        {
            // The degrade-to-today path (old recordings, EVA grabs, v1 Board merges): the dock
            // happened, so the row exists, but nothing is named. Fails if an unstamped merge
            // starts claiming a partner (which is exactly what the pid-only guesses this design
            // rejects would produce).
            var row = Row(Digest("tc"), "zerobp", MissionEventDigest.VerbDockedWith);

            Assert.Equal("", row.PartnerText);
            Assert.Null(row.GoToRecordingId);
            Assert.Null(row.GoToMissionId);
        }

        [Fact]
        public void Digest_TwoParentMerge_NamesTheOtherParentWithoutAMissionSuffix()
        {
            // A two-parent same-tree merge closes both lines inside one mission; the partner is
            // nameable (issue-1's T1.4 case) but the mission name would be the reader's own.
            // Fails if the viewer direction is not applied and the row names its own vessel.
            var row = Row(Digest("tc"), "twoparentbp", MissionEventDigest.VerbDockedWith);

            Assert.Equal("Vessel P", row.SubjectName);
            Assert.Equal("Vessel Q", row.PartnerText);
            Assert.Null(row.GoToRecordingId);
        }

        [Fact]
        public void Digest_VisibilityFlaggedNodes_AreSkippedOnBothSides()
        {
            // A re-fly-superseded merged child leaves its branch points in the tree. Fails if the
            // digest tells the story of a flight that was superseded away - phantom rows the
            // player cannot navigate to, on BOTH the owning and the partner mission.
            var ta = Digest("ta", visible: id => id != "AB");
            var tb = Digest("tb", visible: id => id != "AB");

            Assert.DoesNotContain(ta, r => r.SourceBranchPointId == "dockbp");
            Assert.DoesNotContain(ta, r => r.SourceBranchPointId == "undockbp");
            Assert.DoesNotContain(tb, r => r.SourceBranchPointId == "dockbp");
            // The unaffected same-tree dock still tells its half of the story.
            Assert.Contains(ta, r => r.SourceBranchPointId == "dockbp2");
        }

        [Fact]
        public void Digest_EmptyTreeAndNullInputs_ProduceAnEmptyDigest()
        {
            // Design edge case 24: an empty slice renders "(no events)", it does not crash a
            // draw pass. Also covers a null graph / null mission (a caller before the host cache
            // has built, and a tree with no mission yet).
            Assert.Empty(MissionEventDigest.Build(null, null, null, MissionName, MissionId));
            Assert.Empty(MissionEventDigest.Build(
                null, new RecordingTree { Id = "empty" }, null, MissionName, MissionId));

            var (graph, trees) = BuildGraph();
            Assert.NotEmpty(MissionEventDigest.Build(
                null, Tree(trees, "ta"), null, null, null));      // launch rows still derive
            Assert.NotEmpty(MissionEventDigest.Build(
                graph, Tree(trees, "ta"), null, null, null));     // null resolvers are tolerated
        }

        // ------------------------------------------------------------------
        // Undock suppression (design 7.1 step 2)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_DebrisOnlyUndock_EmitsNoRow()
        {
            // A jettison is not a story beat. Fails if every fairing / spent stage separation
            // gets its own row, burying the beats that ARE the story.
            var x0 = CrossTreeDockFixture.Rec("X0", 700, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                "CX", 0, 0, 100, childBp: "splitbp");
            var x1 = CrossTreeDockFixture.Rec("X1", 700, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx",
                "CX", 1, 100, 200, parentBp: "splitbp");
            var junk = CrossTreeDockFixture.Rec("Junk", 701, null, "CJ", 0, 100, 120,
                parentBp: "splitbp", debris: true);
            var tree = CrossTreeDockFixture.Tree("tx", new[] { x0, x1, junk }, new[]
            {
                CrossTreeDockFixture.BP("splitbp", BranchPointType.Undock, 100.0,
                    new[] { "X0" }, new[] { "X1", "Junk" }, splitCause: "UNDOCK"),
            });
            var trees = new List<RecordingTree> { tree };
            var graph = DockEventGraph.Build(trees, null);

            var rows = MissionEventDigest.Build(graph, tree, MissionFor("tx"), MissionName, MissionId);

            Assert.DoesNotContain(rows, r => r.Verb == MissionEventDigest.VerbUndocked);
            // The debris recording is not a launch either (it never launched; it fell off).
            Assert.DoesNotContain(rows, r => r.SubjectName == "Vessel Junk");
        }

        // ------------------------------------------------------------------
        // R5 gap rows (design 7.1 step 3 / 7.4)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_GapRow_AppearsOnlyForAnIncludedLinkPastTheThreshold()
        {
            // R5: the partner journey a player just switched on begins with unrecorded loiter, and
            // the digest STATES that rather than letting the ghost teleport across it. Fails if
            // the gap is stated for a link the mission never included (noise on every partner
            // mission) or interpolated over.
            Mission linked = MissionFor("tb");
            linked.IncludedForeignDockLinkIds.Add("dockbp");

            // Claimed line ends at 80, dock at 150 -> 70s gap, past the 60s threshold.
            var withGap = Digest("tb", b0End: 80, mission: linked);
            MissionEventRow gap = Row(withGap, "dockbp", MissionEventDigest.VerbGap);
            Assert.Equal(80.0, gap.UT);
            Assert.Equal(70.0, gap.GapSeconds);
            Assert.Equal("Vessel B0", gap.SubjectName);
            Assert.Equal("(loiter, 1m 10s - not recorded)",
                MissionEventDigest.FormatRowText(gap));
            // Stated BEFORE the dock it explains (UT = the claimed line's end).
            Assert.True(withGap.IndexOf(gap)
                < withGap.FindIndex(r => r.Verb == MissionEventDigest.VerbDockedBy));

            // Same link, 50s gap: under the threshold, so nothing is stated.
            Assert.DoesNotContain(Digest("tb", b0End: 100, mission: linked),
                r => r.Verb == MissionEventDigest.VerbGap);

            // Past the threshold but the link is NOT included: still nothing (the gap statement
            // is an explanation of a journey the mission opted into).
            Assert.DoesNotContain(Digest("tb", b0End: 80, mission: MissionFor("tb")),
                r => r.Verb == MissionEventDigest.VerbGap);
        }

        // ------------------------------------------------------------------
        // Terminal rows (design 7.1 step 4)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_TerminalLeaf_EmitsItsVerdictRow_ExceptDockedAndBoarded()
        {
            // A line end the tree already states costs nothing to say. Docked / Boarded are
            // deliberately skipped: that line ended by being absorbed into a merge which already
            // has its own row, and emitting both would tell one event twice under two verbs.
            var (graph, trees) = BuildGraph();
            RecordingTree ta = Tree(trees, "ta");
            ta.Recordings["AD"].TerminalStateValue = TerminalState.Landed;
            ta.Recordings["B1"].TerminalStateValue = TerminalState.Docked;

            var rows = MissionEventDigest.Build(graph, ta, MissionFor("ta"), MissionName, MissionId);

            Assert.Contains(rows, r => r.Verb == "Landed" && r.SubjectName == "Stack AD"
                                       && r.UT == 600);
            Assert.DoesNotContain(rows, r => r.Verb == "Docked");
        }

        // ------------------------------------------------------------------
        // Logging (design 15.2)
        // ------------------------------------------------------------------

        [Fact]
        public void Digest_EmitsTheGrepStableSummaryLine()
        {
            // The one diagnostic that makes a missing / duplicated story row diagnosable from
            // KSP.log alone. Fails if a counter is dropped or renamed (grep contracts are
            // token-exact), or if the summary starts firing per row.
            Digest("ta", visible: id => id != "AB");

            Assert.Contains(logLines, l =>
                l.Contains("[Mission]") && l.Contains("EventDigest: tree=ta")
                && l.Contains("rows=3") && l.Contains("docks=1") && l.Contains("undocks=0")
                && l.Contains("gaps=0") && l.Contains("skippedFlagged=2"));
        }

        [Fact]
        public void Digest_SuppressLogging_SilencesTheSummary()
        {
            // Per-frame callers (a future draw path that does not cache) must be able to go
            // quiet, exactly like the sibling derivation modules.
            MissionEventDigest.SuppressLogging = true;
            try
            {
                Digest("ta");
            }
            finally
            {
                MissionEventDigest.SuppressLogging = false;
            }

            Assert.DoesNotContain(logLines, l => l.Contains("EventDigest:"));
        }

        // ------------------------------------------------------------------
        // Row phrasing
        // ------------------------------------------------------------------

        [Fact]
        public void FormatRowText_PhrasesEveryRowShape()
        {
            // The renderer's words are pure and testable, so a phrasing regression (a doubled
            // verb, a dangling partner clause, a culture-formatted duration) fails here rather
            // than in a screenshot.
            Assert.Equal("Vessel A0 launched", MissionEventDigest.FormatRowText(
                new MissionEventRow { Verb = MissionEventDigest.VerbLaunched, SubjectName = "Vessel A0" }));
            Assert.Equal("Vessel B1 undocked", MissionEventDigest.FormatRowText(
                new MissionEventRow { Verb = MissionEventDigest.VerbUndocked, SubjectName = "Vessel B1", PartnerText = "" }));
            Assert.Equal("Vessel B0 docked by Stack AB (mission 'AB')",
                MissionEventDigest.FormatRowText(new MissionEventRow
                {
                    Verb = MissionEventDigest.VerbDockedBy,
                    SubjectName = "Vessel B0",
                    PartnerText = "Stack AB (mission 'AB')",
                }));
            Assert.Equal("Stack AD landed", MissionEventDigest.FormatRowText(
                new MissionEventRow { Verb = "Landed", SubjectName = "Stack AD" }));
            Assert.Equal("(loiter, 2m 0s - not recorded)", MissionEventDigest.FormatRowText(
                new MissionEventRow { Verb = MissionEventDigest.VerbGap, GapSeconds = 120 }));
            // A row whose subject the tree cannot name still renders as one line, not as a
            // NullReferenceException inside a draw pass.
            Assert.Equal("? undocked", MissionEventDigest.FormatRowText(
                new MissionEventRow { Verb = MissionEventDigest.VerbUndocked }));
        }
    }
}
