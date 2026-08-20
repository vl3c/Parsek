using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    // Loop seam markers R2 / R3 (design-dock-event-graph.md 7.3, 6.4, 10 edge cases 16-21, 14).
    //
    // Two surfaces are covered here, both pure:
    //   - LoopSeamMarkerBuilder: WHICH markers a unit gets, on WHICH member, with WHAT words.
    //   - GhostPlaybackLogic.TryResolveSeamMarkerToEmit: the per-frame cursor + dedup the flight
    //     engine drives (the GameObject side is deliberately left to opportunistic in-game
    //     observation - design 16.2 gives seam markers no in-game cell in v1).
    //
    // [Collection("Sequential")] because ParsekLog's sink and the module suppression flags are
    // process-wide statics.
    [Collection("Sequential")]
    public class SeamMarkerTests : IDisposable
    {
        private const double DockUT = CrossTreeDockFixture.DockUT;      // 150
        private const double UndockUT = CrossTreeDockFixture.UndockUT;  // 300
        private const string ControllerMission = "AB";
        private const string PartnerMission = "CD Freighter";

        private readonly List<string> logLines = new List<string>();

        public SeamMarkerTests()
        {
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            LoopSeamMarkerBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = true;      // only this module's lines are under test
            MissionCrossTreeDock.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            LoopSeamMarkerBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
            MissionCrossTreeDock.SuppressLogging = false;
            MissionLoopUnitBuilder.SuppressLogging = false;
        }

        // ------------------------------------------------------------------
        // Fixture: the AB/CD shape (design 1.2), sized so the seams are exact.
        //
        //   tb: B0 [0 .. 150]  - the partner's solo flight, ENDING AT THE DOCK (so its member
        //                        window end coincides with the dock UT: the R3 shape).
        //   ta: A0 [0 .. 150] -> dockbp(Dock, target=pid B) -> AB [150 .. 300]
        //                     -> undockbp -> { A1 [300 .. 400], B1 [300 .. 380] }
        //
        // Committed order (the index space member windows use): B0=0, A0=1, AB=2, A1=3, B1=4.
        // ------------------------------------------------------------------

        private const int IdxB0 = 0;
        private const int IdxA0 = 1;
        private const int IdxAB = 2;
        private const int IdxA1 = 3;
        private const int IdxB1 = 4;

        private static RecordingTree PartnerTree(uint pid = CrossTreeDockFixture.PidB,
            string guid = CrossTreeDockFixture.GuidB)
            => CrossTreeDockFixture.Tree("tb", new[]
            {
                CrossTreeDockFixture.Rec("B0", pid, guid, "CB", 0, 0, DockUT, vessel: "Freighter CD"),
            });

        // targetPid 0 reproduces the pre-stamping / EVA / Board population (design 6.4 row 1).
        private static RecordingTree ControllerTree(uint targetPid = CrossTreeDockFixture.PidB)
        {
            var a0 = CrossTreeDockFixture.Rec("A0", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA", 0, 0, DockUT, pods: 1, probes: 0,
                childBp: "dockbp", vessel: "Lander A");
            var ab = CrossTreeDockFixture.Rec("AB", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAB", 0, DockUT, UndockUT, pods: 1, probes: 1,
                parentBp: "dockbp", childBp: "undockbp", vessel: "Stack AB");
            var a1 = CrossTreeDockFixture.Rec("A1", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA2", 0, UndockUT, 400,
                parentBp: "undockbp", vessel: "Lander A");
            var b1 = CrossTreeDockFixture.Rec("B1", CrossTreeDockFixture.PidB,
                CrossTreeDockFixture.GuidB, "CB2", 0, UndockUT, 380,
                parentBp: "undockbp", vessel: "Freighter CD");

            return CrossTreeDockFixture.Tree("ta", new[] { a0, ab, a1, b1 }, new[]
            {
                CrossTreeDockFixture.BP("dockbp", BranchPointType.Dock, DockUT,
                    new[] { "A0" }, new[] { "AB" }, targetPid: targetPid, mergeCause: "DOCK"),
                CrossTreeDockFixture.BP("undockbp", BranchPointType.Undock, UndockUT,
                    new[] { "AB" }, new[] { "A1", "B1" }, splitCause: "UNDOCK"),
            });
        }

        private static DockEventGraph Graph(uint targetPid = CrossTreeDockFixture.PidB)
            => DockEventGraph.Build(
                new List<RecordingTree> { PartnerTree(), ControllerTree(targetPid) }, null);

        private static Dictionary<string, int> IndexById()
            => new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "B0", IdxB0 }, { "A0", IdxA0 }, { "AB", IdxAB },
                { "A1", IdxA1 }, { "B1", IdxB1 },
            };

        // Member windows keyed by committed index. Each entry is (index, start, end) so a test can
        // state exactly the trimmed selection it is about.
        private static Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> Windows(
            params (int index, double start, double end)[] entries)
        {
            var d = new Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow>();
            foreach (var e in entries)
                d[e.index] = new GhostPlaybackLogic.LoopUnit.MemberWindow(e.start, e.end);
            return d;
        }

        private static string MissionNameForTree(string treeId)
            => treeId == "ta" ? ControllerMission : treeId == "tb" ? PartnerMission : null;

        private static List<GhostPlaybackLogic.LoopSeamMarker> Build(
            DockEventGraph graph, string treeId,
            Dictionary<int, GhostPlaybackLogic.LoopUnit.MemberWindow> windows,
            out int r2, out int r3,
            Func<string, string> names = null)
            => LoopSeamMarkerBuilder.Build(
                graph, treeId, windows, IndexById(),
                names ?? MissionNameForTree, out r2, out r3);

        // ==================================================================
        // R2 (MergeAppear): the stack grew because a partner joined
        // ==================================================================

        [Fact]
        public void R2_DockedStretchWithNoPartnerLegs_NamesThePartnerAndItsMission()
        {
            // Regression: mission AB loops, its ghost DOUBLES in size at the dock UT because CD's
            // half materialised out of a snapshot, and today nothing on screen says why.
            var markers = Build(
                Graph(), "ta",
                Windows((IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT), (IdxA1, UndockUT, 400)),
                out int r2, out int r3);

            Assert.Equal(1, r2);
            Assert.Equal(0, r3);
            GhostPlaybackLogic.LoopSeamMarker m = Assert.Single(markers);
            Assert.Equal(GhostPlaybackLogic.SeamMarkerKind.MergeAppear, m.Kind);
            Assert.Equal(IdxAB, m.MemberIndex);          // on the MERGED child, not on A0
            Assert.Equal(DockUT, m.SeamUT);
            Assert.Equal(DockUT + LoopSeamMarkerBuilder.SeamWindowSeconds, m.WindowEndUT);
            Assert.Equal("joined by Freighter CD - see mission 'CD Freighter'", m.Text);
        }

        [Fact]
        public void R2_NotEmitted_WhenThePartnerIsAlsoAMember()
        {
            // Regression: with the partner-journey link ON, both halves are replayed and the two
            // ghosts visibly meet - an "unexplained appearance" message would be a lie.
            var markers = Build(
                Graph(), "ta",
                Windows((IdxB0, 0, DockUT), (IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT)),
                out int r2, out int _);

            Assert.Equal(0, r2);
            Assert.DoesNotContain(markers,
                x => x.Kind == GhostPlaybackLogic.SeamMarkerKind.MergeAppear);
        }

        [Fact]
        public void R2_UnstampedZeroPartner_StillFires_WithGenericText()
        {
            // Design 6.4 row 1: the SEAM is real on a pre-stamping recording even though the
            // partner is permanently unidentifiable. Regression: degrading to silence would leave
            // the oldest recordings - the ones a player is most likely to re-watch - unexplained.
            var markers = Build(
                Graph(targetPid: 0), "ta",
                Windows((IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT)),
                out int r2, out int _);

            Assert.Equal(1, r2);
            Assert.Equal("joined by another vessel", Assert.Single(markers).Text);
        }

        [Fact]
        public void R2_NotEmitted_WhenTheSeamIsOutsideTheMergedMembersTrimmedWindow()
        {
            // Regression: an interval-level start-trim that begins the merged child AFTER the dock
            // means the player never sees the growth; explaining an off-screen moment is noise.
            var markers = Build(
                Graph(), "ta",
                Windows((IdxAB, DockUT + 50, UndockUT)),
                out int r2, out int _);

            Assert.Equal(0, r2);
            Assert.Empty(markers);
        }

        [Fact]
        public void R2_TwoParentMerge_NamesTheParentThisMissionDoesNotReplay()
        {
            // TryDescribePartner declines a two-parent node seen from the merged child (naming one
            // of two parents would be arbitrary). Regression: membership is what breaks the tie -
            // dropping this branch would silently lose every same-tree rendezvous seam.
            var tree = TwoParentTree();
            DockEventGraph graph = DockEventGraph.Build(new List<RecordingTree> { tree }, null);
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "P", 0 }, { "Q", 1 }, { "PQ", 2 },
            };

            // Q excluded from the mission -> Q's half materialises inside PQ.
            var markers = LoopSeamMarkerBuilder.Build(
                graph, "tp", Windows((0, 0, 50), (2, 50, 100)), indexById,
                _ => "Station Build", out int r2, out int _);

            Assert.Equal(1, r2);
            Assert.Equal("joined by Tug Q - see mission 'Station Build'", Assert.Single(markers).Text);

            // Both parents replayed -> the meeting explains itself.
            var bothIncluded = LoopSeamMarkerBuilder.Build(
                graph, "tp", Windows((0, 0, 50), (1, 10, 50), (2, 50, 100)), indexById,
                _ => "Station Build", out int r2Both, out int _);
            Assert.Equal(0, r2Both);
            Assert.Empty(bothIncluded);
        }

        private static RecordingTree TwoParentTree()
        {
            var p = CrossTreeDockFixture.Rec("P", 500, "pppppppppppppppppppppppppppppppp", "CP", 0,
                0, 50, pods: 1, probes: 0, childBp: "twoparentbp", vessel: "Core P");
            var q = CrossTreeDockFixture.Rec("Q", 501, "qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq", "CQ", 0,
                10, 50, childBp: "twoparentbp", vessel: "Tug Q");
            var pq = CrossTreeDockFixture.Rec("PQ", 500, "pppppppppppppppppppppppppppppppp", "CPQ", 0,
                50, 100, pods: 1, probes: 1, parentBp: "twoparentbp", vessel: "Stack PQ");
            return CrossTreeDockFixture.Tree("tp", new[] { p, q, pq }, new[]
            {
                CrossTreeDockFixture.BP("twoparentbp", BranchPointType.Dock, 50.0,
                    new[] { "P", "Q" }, new[] { "PQ" }, targetPid: 501, mergeCause: "DOCK"),
            });
        }

        // ==================================================================
        // R3 (DockedVanish): a line ends AT a dock this mission does not replay
        // ==================================================================

        [Fact]
        public void R3_PartnerMissionWithLinkOff_ExplainsItsGhostVanishingAtTheDock()
        {
            // Regression (design 1.1): mission CD loops with the partner-journey link OFF, so its
            // ghost simply blinks out at its recorded end. The vessel did not die - it docked, and
            // the combined flight lives in the OTHER mission. The node lives in tree ta and is
            // registered under tb by the graph, which is what makes this reachable at all.
            var markers = Build(
                Graph(), "tb", Windows((IdxB0, 0, DockUT)), out int r2, out int r3);

            Assert.Equal(0, r2);
            Assert.Equal(1, r3);
            GhostPlaybackLogic.LoopSeamMarker m = Assert.Single(markers);
            Assert.Equal(GhostPlaybackLogic.SeamMarkerKind.DockedVanish, m.Kind);
            Assert.Equal(IdxB0, m.MemberIndex);
            Assert.Equal(DockUT, m.SeamUT);
            // Partner = the merged stack (viewer IS the claimed recording); the "continues in"
            // mission is the one that owns the merge, which is where the story actually went.
            Assert.Equal("docked to Stack AB - continues in mission 'AB'", m.Text);
        }

        [Fact]
        public void R3_ExcludedDockInterval_FiresOnThePreDockMember()
        {
            // Edge case 18: the mission excluded its own `@dock` interval, so the merged child is
            // NOT a member and A0's line just stops at the dock UT. Regression: without this the
            // most deliberate way to trim a mission produces the most unexplained vanish.
            var markers = Build(
                Graph(), "ta", Windows((IdxA0, 0, DockUT)), out int r2, out int r3);

            Assert.Equal(0, r2);
            Assert.Equal(1, r3);
            GhostPlaybackLogic.LoopSeamMarker m = Assert.Single(markers);
            Assert.Equal(GhostPlaybackLogic.SeamMarkerKind.DockedVanish, m.Kind);
            Assert.Equal(IdxA0, m.MemberIndex);
            Assert.Equal("docked to Freighter CD - continues in mission 'AB'", m.Text);
        }

        [Fact]
        public void R3_NotEmitted_WhenTheMergedChildIsAMember()
        {
            // Regression: the mission replays straight through the dock, so no line ends there -
            // firing here would label a continuous flight as a disappearance.
            var markers = Build(
                Graph(), "ta",
                Windows((IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT)),
                out int _, out int r3);

            Assert.Equal(0, r3);
            Assert.DoesNotContain(markers,
                x => x.Kind == GhostPlaybackLogic.SeamMarkerKind.DockedVanish);
        }

        [Fact]
        public void R3_NotEmitted_WhenTheMemberWindowEndsAwayFromTheDock()
        {
            // Regression: "coincides" is LoopTiming.BoundaryEpsilon, not "somewhere near". A member
            // that ends for its own reasons must not be labelled as having docked.
            var markers = Build(
                Graph(), "tb", Windows((IdxB0, 0, DockUT - 5.0)), out int _, out int r3);

            Assert.Equal(0, r3);
            Assert.Empty(markers);
        }

        [Fact]
        public void R3_NotEmitted_ForANonParticipantMemberEndingAtTheSameUT()
        {
            // Regression: coincidence is not custody. A1 is a child of the UNDOCK, never absorbed
            // by the dock; trimming it to end exactly at the dock UT must not mint "docked to".
            var markers = Build(
                Graph(), "ta", Windows((IdxA1, 120, DockUT)), out int _, out int r3);

            Assert.Equal(0, r3);
            Assert.Empty(markers);
        }

        [Fact]
        public void R3_UnresolvedPartner_FiresWithGenericText()
        {
            // Design 6.4: on an unresolvable status the partner is NOT named - while the vanish
            // itself is still real. Regression: degrading to silence here would leave exactly the
            // pre-stamping recordings (the ones with no other affordance) unexplained, and naming a
            // partner the resolver declined would assert an identity nothing established.
            var markers = Build(
                Graph(targetPid: 0), "ta", Windows((IdxA0, 0, DockUT)), out int _, out int r3);

            Assert.Equal(1, r3);
            Assert.Equal("docked to another vessel", Assert.Single(markers).Text);
        }

        // ==================================================================
        // Board merges get boarding words, not docking words
        // ==================================================================

        [Fact]
        public void Board_R2_SaysBoardedNotDocked()
        {
            // A kerbal climbing into a pod is not a docking. Regression: the merge branch covers
            // Dock AND Board, so a single verb would describe the wrong event - and since Board
            // merges carry no partner pid in v1 (design Q1), the GENERIC arm is the one players
            // actually see, which is exactly where a copy-paste verb would hide.
            var markers = Build(
                BoardGraph(), "ta",
                Windows((IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT)),
                out int r2, out int _);

            Assert.Equal(1, r2);
            Assert.Equal("boarded by another kerbal", Assert.Single(markers).Text);
        }

        [Fact]
        public void Board_R3_SaysBoardedAndNamesTheContinuingMission()
        {
            var markers = Build(
                BoardGraph(), "ta", Windows((IdxA0, 0, DockUT)), out int _, out int r3);

            Assert.Equal(1, r3);
            Assert.Equal("boarded - continues in mission 'AB'", Assert.Single(markers).Text);
        }

        [Fact]
        public void Board_NamedPartner_StillSaysBoarded()
        {
            // The named arm too: the verb comes from the branch-point kind, not from whether a
            // partner happened to resolve.
            Assert.Equal("boarded by Bob Kerman - see mission 'CD Freighter'",
                LoopSeamMarkerBuilder.FormatMergeAppear(
                    BranchPointType.Board, "Bob Kerman", "CD Freighter"));
            Assert.Equal("boarded", LoopSeamMarkerBuilder.FormatDockedVanish(
                BranchPointType.Board, "Stack AB", null));
        }

        // ta with the merge recorded as a BOARD (unstamped, as v1 records every board).
        private static DockEventGraph BoardGraph()
        {
            var a0 = CrossTreeDockFixture.Rec("A0", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA", 0, 0, DockUT, pods: 1, probes: 0,
                childBp: "dockbp", vessel: "Lander A");
            var ab = CrossTreeDockFixture.Rec("AB", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAB", 0, DockUT, UndockUT, pods: 1, probes: 1,
                parentBp: "dockbp", vessel: "Stack AB");
            RecordingTree tree = CrossTreeDockFixture.Tree("ta", new[] { a0, ab }, new[]
            {
                CrossTreeDockFixture.BP("dockbp", BranchPointType.Board, DockUT,
                    new[] { "A0" }, new[] { "AB" }, targetPid: 0, mergeCause: "BOARD"),
            });
            return DockEventGraph.Build(new List<RecordingTree> { tree }, null);
        }

        // ==================================================================
        // List shape, degradation, logging
        // ==================================================================

        [Fact]
        public void Markers_AreSortedBySeamUT()
        {
            // The runtime cursor is a MONOTONE walk over this list; an unsorted list would silently
            // skip markers (the walk advances past anything behind the clock). Two merges, and the
            // graph enumerates the LATER one first here (nodes are sorted by UT, but tree branch
            // order is not), so this is a real ordering assertion, not a one-element tautology.
            RecordingTree tree = TwoDockTree();
            DockEventGraph graph = DockEventGraph.Build(
                new List<RecordingTree> { PartnerTree(), tree }, null);
            var indexById = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "B0", IdxB0 }, { "A0", 1 }, { "AB", 2 }, { "A1", 3 }, { "AD", 4 },
            };

            var markers = LoopSeamMarkerBuilder.Build(
                graph, "ta",
                Windows((1, 0, DockUT), (2, DockUT, UndockUT), (3, UndockUT, 500.0), (4, 500.0, 600.0)),
                indexById, MissionNameForTree, out int r2, out int _);

            Assert.Equal(2, r2);
            Assert.Equal(2, markers.Count);
            Assert.Equal(DockUT, markers[0].SeamUT);
            Assert.Equal(500.0, markers[1].SeamUT);
            // The second dock's partner pid matches nothing anywhere (NoMatch): generic words, but
            // the seam is still stated.
            Assert.Equal("joined by another vessel", markers[1].Text);
        }

        // ta with a SECOND dock after the undock: A1 -> dockbp2(target = a pid no tree carries)
        // -> AD. The second node exercises NoMatch and gives the sort assertion two markers.
        private static RecordingTree TwoDockTree()
        {
            var a0 = CrossTreeDockFixture.Rec("A0", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA", 0, 0, DockUT, pods: 1, probes: 0,
                childBp: "dockbp", vessel: "Lander A");
            var ab = CrossTreeDockFixture.Rec("AB", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAB", 0, DockUT, UndockUT, pods: 1, probes: 1,
                parentBp: "dockbp", childBp: "undockbp", vessel: "Stack AB");
            var a1 = CrossTreeDockFixture.Rec("A1", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CA2", 0, UndockUT, 500.0,
                parentBp: "undockbp", childBp: "dockbp2", vessel: "Lander A");
            var ad = CrossTreeDockFixture.Rec("AD", CrossTreeDockFixture.PidA,
                CrossTreeDockFixture.GuidA, "CAD", 0, 500.0, 600.0, pods: 1, probes: 1,
                parentBp: "dockbp2", vessel: "Stack AD");

            return CrossTreeDockFixture.Tree("ta", new[] { a0, ab, a1, ad }, new[]
            {
                CrossTreeDockFixture.BP("dockbp2", BranchPointType.Dock, 500.0,
                    new[] { "A1" }, new[] { "AD" }, targetPid: 999_999, mergeCause: "DOCK"),
                CrossTreeDockFixture.BP("dockbp", BranchPointType.Dock, DockUT,
                    new[] { "A0" }, new[] { "AB" },
                    targetPid: CrossTreeDockFixture.PidB, mergeCause: "DOCK"),
                CrossTreeDockFixture.BP("undockbp", BranchPointType.Undock, UndockUT,
                    new[] { "AB" }, new[] { "A1" }, splitCause: "UNDOCK"),
            });
        }

        [Fact]
        public void NullGraph_ProducesNoMarkers()
        {
            // The degradation contract: KSC, the Tracking Station, the Missions-window mirror and
            // every pre-existing test build units with no graph and must behave exactly as before.
            var markers = LoopSeamMarkerBuilder.Build(
                null, "ta", Windows((IdxAB, DockUT, UndockUT)), IndexById(),
                MissionNameForTree, out int r2, out int r3);

            Assert.Empty(markers);
            Assert.Equal(0, r2);
            Assert.Equal(0, r3);
        }

        [Fact]
        public void LoopUnitBuiltWithoutAGraph_HasNullSeamMarkers()
        {
            // The byte-identity claim of design 5.2, asserted on the REAL builder entry point:
            // every existing Build call site keeps producing exactly the unit it produced before.
            var tree = ControllerTree();
            var committed = new List<Recording>
            {
                tree.Recordings["A0"], tree.Recordings["AB"],
                tree.Recordings["A1"], tree.Recordings["B1"],
            };
            var missions = new List<Mission>
            {
                new Mission("m1", "ta", ControllerMission) { LoopPlayback = true },
            };
            MissionLoopUnitBuilder.SuppressLogging = true;

            GhostPlaybackLogic.LoopUnitSet set = MissionLoopUnitBuilder.Build(
                missions, new[] { tree }, committed, 30.0);

            Assert.Equal(1, set.Count);
            Assert.True(set.TryGetUnitForMember(0, out GhostPlaybackLogic.LoopUnit unit));
            Assert.Null(unit.SeamMarkers);
        }

        [Fact]
        public void Build_LogsTheSummaryLine()
        {
            // Design 15.2: this line is the only proof the computation ran and what it produced.
            Build(Graph(), "ta",
                Windows((IdxA0, 0, DockUT), (IdxAB, DockUT, UndockUT)), out int _, out int _2);

            Assert.Contains(logLines, l => l.Contains("[SeamMarker]")
                && l.Contains("unit tree=ta") && l.Contains("markers=1")
                && l.Contains("r2=1") && l.Contains("r3=0"));
        }

        [Fact]
        public void Build_SuppressLogging_SilencesTheSummaryLine()
        {
            // The loop-unit builder forwards its own suppression here; a per-frame UI mirror must
            // not flood the log with one line per rebuild.
            LoopSeamMarkerBuilder.SuppressLogging = true;
            Build(Graph(), "ta", Windows((IdxAB, DockUT, UndockUT)), out int _, out int _2);

            Assert.DoesNotContain(logLines, l => l.Contains("[SeamMarker]"));
        }

        // ==================================================================
        // Runtime cursor + dedup (the engine's per-frame check)
        // ==================================================================

        private static List<GhostPlaybackLogic.LoopSeamMarker> TwoMarkers()
            => new List<GhostPlaybackLogic.LoopSeamMarker>
            {
                new GhostPlaybackLogic.LoopSeamMarker(
                    100.0, 110.0, GhostPlaybackLogic.SeamMarkerKind.DockedVanish, 1, "first"),
                new GhostPlaybackLogic.LoopSeamMarker(
                    200.0, 210.0, GhostPlaybackLogic.SeamMarkerKind.MergeAppear, 2, "second"),
            };

        // Thin wrapper so the cursor cells read as the question they ask; the skip counters have
        // their own dedicated cells below.
        private static int Resolve(
            IReadOnlyList<GhostPlaybackLogic.LoopSeamMarker> markers, int memberIndex,
            double spanLoopUT, ref int cursor, HashSet<long> fired, long cycle)
            => GhostPlaybackLogic.TryResolveSeamMarkerToEmit(
                markers, memberIndex, spanLoopUT, ref cursor, fired, cycle, out int _, out int _2);

        [Fact]
        public void Cursor_FiresOnceInsideTheWindowThenStaysQuiet()
        {
            // Regression: the check runs per member per frame. Without the dedup a seam would post
            // a ScreenMessage every frame for ten seconds of loop time.
            var markers = TwoMarkers();
            int cursor = 0;
            var fired = new HashSet<long>();

            int hit = Resolve(markers, 1, 100.5, ref cursor, fired, 3);
            Assert.Equal(0, hit);
            fired.Add(GhostPlaybackLogic.SeamMarkerDedupKey(hit, 3));

            Assert.Equal(-1, Resolve(markers, 1, 100.6, ref cursor, fired, 3));
            Assert.Equal(-1, Resolve(markers, 1, 109.9, ref cursor, fired, 3));
        }

        [Fact]
        public void Cursor_RefiresOnTheNextLoopCycle()
        {
            // A looping mission explains its seam once PER LOOP: the player watching the second
            // cycle deserves the same sentence as the player watching the first.
            var markers = TwoMarkers();
            int cursor = 0;
            var fired = new HashSet<long>();

            int first = Resolve(markers, 1, 100.5, ref cursor, fired, 0);
            fired.Add(GhostPlaybackLogic.SeamMarkerDedupKey(first, 0));
            Assert.Equal(-1, Resolve(markers, 1, 101.0, ref cursor, fired, 0));

            // New cycle: the engine resets the cursor and clears the dedup for this member.
            cursor = 0;
            fired.Clear();
            Assert.Equal(0, Resolve(markers, 1, 100.5, ref cursor, fired, 1));
            // The key itself is cycle-scoped even without the clear.
            Assert.NotEqual(
                GhostPlaybackLogic.SeamMarkerDedupKey(0, 0),
                GhostPlaybackLogic.SeamMarkerDedupKey(0, 1));
        }

        [Fact]
        public void Cursor_IgnoresMarkersBelongingToOtherMembers()
        {
            // Regression: markers ride ONE sorted list shared by the whole unit. Keying on the
            // window alone would fire every member's message on every member's clock.
            var markers = TwoMarkers();
            int cursor = 0;
            Assert.Equal(-1, Resolve(markers, 2, 100.5, ref cursor, null, 0));
            Assert.Equal(1, Resolve(markers, 2, 200.5, ref cursor, null, 0));
        }

        [Fact]
        public void Cursor_DoesNotFireBeforeTheSeamOrAfterTheWindow()
        {
            var markers = TwoMarkers();
            int before = 0;
            Assert.Equal(-1, Resolve(markers, 1, 99.0, ref before, null, 0));
            int after = 0;
            Assert.Equal(-1, Resolve(markers, 1, 110.5, ref after, null, 0));
        }

        [Fact]
        public void Cursor_AtTheExactSeamBoundary_FiresExactlyOnce()
        {
            // Edge case 16: at the dock UT both the pre-dock member and the merged member render
            // for one frame, and the span clock can land exactly on the seam. Fire once, not twice.
            var markers = TwoMarkers();
            int cursor = 0;
            var fired = new HashSet<long>();

            int hit = Resolve(markers, 1, 100.0, ref cursor, fired, 0);
            Assert.Equal(0, hit);
            fired.Add(GhostPlaybackLogic.SeamMarkerDedupKey(hit, 0));
            // Same frame-boundary UT again (a second dispatch within the epsilon): no second fire.
            Assert.Equal(-1, Resolve(
                markers, 1, 100.0 - LoopTiming.BoundaryEpsilon / 2.0, ref cursor, fired, 0));
        }

        [Fact]
        public void Cursor_EmptyOrNullMarkerList_IsANoOp()
        {
            // The steady-state path for every mission that never crossed a dock.
            int cursor = 0;
            Assert.Equal(-1, Resolve(null, 0, 10.0, ref cursor, null, 0));
            Assert.Equal(-1, Resolve(
                new List<GhostPlaybackLogic.LoopSeamMarker>(), 0, 10.0, ref cursor, null, 0));
        }

        [Fact]
        public void Cursor_ReportsAWindowTheClockSteppedRightOver()
        {
            // A frame at >=1000x warp can advance the loop clock past the whole 10s window, and the
            // marker then never fires. That stays the behavior (a line nobody could read is worse
            // than none) - but the logging rules say a silent path must leave evidence, so the walk
            // reports what it passed. Regression: without the counter this is indistinguishable
            // from a marker that was never computed.
            var markers = TwoMarkers();
            int cursor = 0;
            int hit = GhostPlaybackLogic.TryResolveSeamMarkerToEmit(
                markers, 1, 5000.0, ref cursor, null, 7,
                out int skipped, out int firstSkipped);

            Assert.Equal(-1, hit);
            Assert.Equal(1, skipped);              // only member 1's marker counts, not member 2's
            Assert.Equal(0, firstSkipped);
        }

        [Fact]
        public void Cursor_DoesNotReportAWindowItAlreadyFiredIn()
        {
            // The counter means "passed WITHOUT firing". A marker that fired and then fell behind
            // the clock is the normal course of a loop, and reporting it would make every healthy
            // seam look like a warp skip.
            var markers = TwoMarkers();
            int cursor = 0;
            var fired = new HashSet<long>();
            int hit = Resolve(markers, 1, 100.5, ref cursor, fired, 0);
            fired.Add(GhostPlaybackLogic.SeamMarkerDedupKey(hit, 0));

            GhostPlaybackLogic.TryResolveSeamMarkerToEmit(
                markers, 1, 5000.0, ref cursor, fired, 0, out int skipped, out int firstSkipped);

            Assert.Equal(0, skipped);
            Assert.Equal(-1, firstSkipped);
        }

        // ==================================================================
        // LoopSeamMarkerRuntime: the state the engine keeps between frames
        // ==================================================================

        [Fact]
        public void Runtime_ResetsWhenTheMarkerListIsSwapped()
        {
            // THE defect this class exists for: a signature-gated loop-unit rebuild (toggling a
            // partner-journey link or an interval checkbox mid-flight, or committing a recording)
            // hands the engine a DIFFERENT marker list with NO loop-cycle change. Carrying the old
            // cursor and fired-keys across that swap suppresses whatever marker now sits at that
            // index for the rest of the cycle - and after a commit the committed-index space has
            // moved, so the state is not even about the same recording any more.
            var runtime = new LoopSeamMarkerRuntime();
            var before = TwoMarkers();

            Assert.Equal(0, runtime.TryTake(before, 1, 100.5, 4, out int _, out int _2));
            Assert.Equal(-1, runtime.TryTake(before, 1, 100.6, 4, out int _3, out int _4));

            // Same cycle, same member, same UT - but a rebuilt list.
            var after = TwoMarkers();
            Assert.Equal(0, runtime.TryTake(after, 1, 100.6, 4, out int _5, out int _6));
        }

        [Fact]
        public void Runtime_ResetDropsEveryMembersState()
        {
            // The engine calls this when it is handed a different unit SET, so nothing survives into
            // an index space it was not keyed in.
            var runtime = new LoopSeamMarkerRuntime();
            var markers = TwoMarkers();
            runtime.TryTake(markers, 1, 100.5, 0, out int _, out int _2);
            runtime.TryTake(markers, 2, 200.5, 0, out int _3, out int _4);
            Assert.Equal(2, runtime.TrackedMemberCount);

            runtime.Reset();

            Assert.Equal(0, runtime.TrackedMemberCount);
            // And the same marker is due again on the same cycle.
            Assert.Equal(0, runtime.TryTake(markers, 1, 100.5, 0, out int _5, out int _6));
        }

        [Fact]
        public void Runtime_FiresOncePerCycleAndAgainOnTheNext()
        {
            var runtime = new LoopSeamMarkerRuntime();
            var markers = TwoMarkers();

            Assert.Equal(0, runtime.TryTake(markers, 1, 100.5, 0, out int _, out int _2));
            Assert.Equal(-1, runtime.TryTake(markers, 1, 100.9, 0, out int _3, out int _4));
            Assert.Equal(0, runtime.TryTake(markers, 1, 100.5, 1, out int _5, out int _6));
        }

        [Fact]
        public void Runtime_NoMarkers_TracksNothing()
        {
            // The steady-state path: a mission that never crossed a dock must not even allocate
            // per-member state.
            var runtime = new LoopSeamMarkerRuntime();
            Assert.Equal(-1, runtime.TryTake(null, 1, 100.5, 0, out int _, out int _2));
            Assert.Equal(-1, runtime.TryTake(
                new List<GhostPlaybackLogic.LoopSeamMarker>(), 1, 100.5, 0, out int _3, out int _4));
            Assert.Equal(0, runtime.TrackedMemberCount);
        }

        // ==================================================================
        // SeamMessageAccumulator: one seam moment is one screen line
        // ==================================================================

        [Fact]
        public void Accumulator_JoinsMarkersRaisedInTheSameFrame()
        {
            // THE defect this class exists for: two markers can share one seam instant (a two-parent
            // merge with the docked stretch excluded raises an R3 per parent at the same UT). Posted
            // separately, the second one loses to the real-time floor EVERY cycle - permanently
            // invisible rather than delayed. Joined, one moment is one line.
            var acc = new SeamMessageAccumulator();

            Assert.Null(acc.Offer(10, "A docked to X"));
            Assert.Null(acc.Offer(10, "B docked to X"));
            Assert.Equal(2, acc.BufferedCount);

            string flushed = acc.Flush();
            Assert.Equal("A docked to X" + SeamMessageAccumulator.Separator + "B docked to X", flushed);
            Assert.Equal(0, acc.BufferedCount);
            Assert.Null(acc.Flush());
        }

        [Fact]
        public void Accumulator_ANewFrameClosesThePreviousLine()
        {
            // A marker arriving on a later frame must not be swallowed into an older seam's line,
            // and the older line must come out even if the per-frame flush never ran.
            var acc = new SeamMessageAccumulator();
            Assert.Null(acc.Offer(10, "first"));

            Assert.Equal("first", acc.Offer(11, "second"));
            Assert.Equal("second", acc.Flush());
        }

        [Fact]
        public void Accumulator_IgnoresEmptyTextAndExactRepeats()
        {
            // Two members can carry the identical generic sentence; saying it twice on one line adds
            // nothing.
            var acc = new SeamMessageAccumulator();
            Assert.Null(acc.Offer(1, null));
            Assert.Null(acc.Offer(1, ""));
            Assert.Equal(0, acc.BufferedCount);

            acc.Offer(1, "joined by another vessel");
            acc.Offer(1, "joined by another vessel");
            Assert.Equal(1, acc.BufferedCount);
            Assert.Equal("joined by another vessel", acc.Flush());
        }

        // ==================================================================
        // R5 gap statement (design 7.4) - the partner-journey link row
        // ==================================================================

        [Fact]
        public void R5_LoiterGap_IsStatedWhenTheClaimedLineStopsWellBeforeTheDock()
        {
            // Regression: the partner journey starts at the dock, but our own recording stopped
            // days earlier. Interpolating over that would invent a flight; the row states it.
            RecordingTree myTree = PartnerTree();
            myTree.Recordings["B0"].ExplicitEndUT = DockUT - 7200.0;   // 2 h of unrecorded loiter
            var link = new ForeignDockLink
            {
                LinkId = "dockbp",
                ForeignTreeId = "ta",
                DockUT = DockUT,
                ClaimedRecordingId = "B0",
            };

            string text = MissionsWindowUI.FormatLinkLoiterGap(myTree, link);

            Assert.Contains("loiter", text);
            Assert.Contains("not recorded", text);
        }

        [Fact]
        public void R5_NoGapText_WhenTheLineRunsUpToTheDockOrIsMissing()
        {
            // The overwhelmingly common case: no gap, no words. Also covers a claimed id the tree
            // no longer carries, which must degrade to silence rather than throw in a draw path.
            RecordingTree myTree = PartnerTree();
            var link = new ForeignDockLink
            {
                LinkId = "dockbp",
                ForeignTreeId = "ta",
                DockUT = DockUT,
                ClaimedRecordingId = "B0",
            };
            Assert.Equal("", MissionsWindowUI.FormatLinkLoiterGap(myTree, link));

            // Under the shared threshold: still silent (one rule, one number, both surfaces).
            myTree.Recordings["B0"].ExplicitEndUT =
                DockUT - MissionEventDigest.GapThresholdSeconds + 1.0;
            Assert.Equal("", MissionsWindowUI.FormatLinkLoiterGap(myTree, link));

            link.ClaimedRecordingId = "gone";
            Assert.Equal("", MissionsWindowUI.FormatLinkLoiterGap(myTree, link));
            Assert.Equal("", MissionsWindowUI.FormatLinkLoiterGap(null, link));
            Assert.Equal("", MissionsWindowUI.FormatLinkLoiterGap(myTree, null));
        }
    }
}
