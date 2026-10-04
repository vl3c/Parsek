using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Unit tests for the T2.2 flattened per-vessel row model (mission-presentation analysis §3
    // Tier 2): one row per physical vessel / EVA kerbal, depth = separation lineage only, the
    // event chain inline, and the per-vessel include affordance expanding to the vessel's OWN
    // explicit interval keys (the non-cascading ExcludedIntervalKeys contract untouched).
    //
    // The fixtures go through the REAL MissionStructureBuilder / MissionCompositionBuilder
    // (same Leg / BP / Tree helpers as MissionPresentationTests), so a change to interval
    // keying, seg chaining, or peel attachment fails here instead of silently flattening wrong.
    [Collection("Sequential")]
    public class MissionVesselRowsTests
    {
        // ---- fixture helpers (mirrors MissionPresentationTests) ----

        private static Recording Leg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0, int seats = 0, int crew = 0,
            string eva = null, string parentAnchor = null, string vessel = "Kerbal X",
            string[] crewNames = null, TerminalState? terminal = null)
        {
            var rec = new Recording
            {
                RecordingId = id,
                VesselName = vessel,
                ChainId = chainId,
                ChainIndex = chainIndex,
                ChainBranch = 0,
                IsDebris = false,
                ExplicitStartUT = start,
                ExplicitEndUT = end,
                EvaCrewName = eva,
                ParentAnchorRecordingId = parentAnchor,
                TerminalStateValue = terminal,
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            for (int i = 0; i < seats; i++) controllers.Add(new ControllerInfo { type = "ExternalSeat" });
            if (!string.IsNullOrEmpty(eva)) controllers.Add(new ControllerInfo { type = "KerbalEVA" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            if (crew > 0) rec.StartCrew = new Dictionary<string, int> { { "Pilot", crew } };
            if (crewNames != null)
            {
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
                foreach (var n in crewNames) rec.CrewEndStates[n] = default(KerbalEndState);
                rec.CrewEndStatesResolved = true;
            }
            return rec;
        }

        private static BranchPoint BP(string id, BranchPointType type, string[] parents,
            string[] children, string splitCause = null)
            => new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
                SplitCause = splitCause,
            };

        private static List<MissionVesselRow> BuildRows(Recording[] recs, BranchPoint[] bps)
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            if (bps != null) tree.BranchPoints.AddRange(bps);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            return MissionVesselRowBuilder.Build(MissionCompositionBuilder.Build(structure));
        }

        private static string Arrow => MissionPresentation.SummarySpanArrow;

        // A launch stack that decouples a named booster and lands: the flattened view must be
        // ONE "Kerbal X" row (two intervals) with ONE "Kerbal X Booster" child (its lineage).
        private static List<MissionVesselRow> BuildDecoupleRows()
        {
            return BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 42, pods: 1, probes: 1,
                        crewNames: new[] { "Jeb Kerman", "Bob Kerman" }),
                    Leg("cont", "C2", 0, 42, 200, pods: 1,
                        crewNames: new[] { "Jeb Kerman", "Bob Kerman" },
                        terminal: TerminalState.Landed),
                    Leg("boost", "C3", 0, 42, 115, probes: 1, parentAnchor: "L",
                        vessel: "Kerbal X Booster", terminal: TerminalState.Destroyed),
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak, new[] { "L" },
                        new[] { "cont", "boost" }, splitCause: "DECOUPLE"),
                });
        }

        [Fact]
        public void Build_DecoupleShape_OneVesselRowWithTheBoosterAsLineageChild()
        {
            List<MissionVesselRow> rows = BuildDecoupleRows();

            // Fails if the staircase survives (an interval rendered as a sibling vessel) or the
            // peeled booster is lost.
            Assert.Single(rows);
            MissionVesselRow ship = rows[0];
            Assert.Equal("Kerbal X", ship.VesselName);
            Assert.False(ship.IsPerson);
            Assert.Equal(2, ship.Intervals.Count);
            Assert.Equal("L", ship.Intervals[0].HeadLegId);
            Assert.Equal(0.0, ship.StartUT);
            Assert.Equal(200.0, ship.EndUT);
            Assert.Equal("Launch", ship.StartEvent);
            Assert.Equal("Landed", ship.EndEvent);

            Assert.Single(ship.Children);
            MissionVesselRow booster = ship.Children[0];
            Assert.Equal("Kerbal X Booster", booster.VesselName);
            Assert.Single(booster.Intervals);
            Assert.Equal("Decoupled", booster.StartEvent);
            Assert.Equal("Destroyed", booster.EndEvent);
            Assert.Empty(booster.Children);
        }

        [Fact]
        public void BuildEventPhrase_NamesThePieceThatLeftAtEachBoundary()
        {
            List<MissionVesselRow> rows = BuildDecoupleRows();

            // Fails if the boundary loses the peel's name (the mockup's "drop booster" moment)
            // or the terminal drops off the chain.
            Assert.Equal(
                "Launch" + Arrow + "Decoupled (Kerbal X Booster)" + Arrow + "Landed",
                rows[0].EventPhrase);
            Assert.Equal("Decoupled" + Arrow + "Destroyed", rows[0].Children[0].EventPhrase);
        }

        // A fresh post-switch recording (another mission's vessel) joins the tree under a
        // BranchPointType.Launch edge off this mission's vessel, mid-run: the shape
        // ParsekFlight.PrepareActiveTreeForFreshPostSwitchRecording produces (the joined
        // recording is the branch point's only child; the ship's own line goes on through
        // its chain continuation).
        private static List<MissionVesselRow> BuildPartnerJoinRows()
        {
            return BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, probes: 1, vessel: "Duna Supply 1"),
                    Leg("cont", "C", 1, 50, 200, probes: 1, vessel: "Duna Supply 1",
                        terminal: TerminalState.Orbiting),
                    Leg("partner", "C3", 0, 50, 180, probes: 2,
                        vessel: "Depot Station Duna I", terminal: TerminalState.Orbiting),
                },
                new[]
                {
                    BP("launchbp", BranchPointType.Launch, new[] { "L" }, new[] { "partner" }),
                });
        }

        // catches: the joined vessel listed as a piece that separated from the ship it hangs
        // under ("Depot Station Duna I   Launch -> Docked" as a child row).
        [Fact]
        public void Build_PartnerJoinedAtLaunchEdge_IsNotAChildRow()
        {
            var logLines = new List<string>();
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            try
            {
                List<MissionVesselRow> rows = BuildPartnerJoinRows();
                MissionVesselRow ship = Assert.Single(rows);
                Assert.Equal("L", ship.OwnerHeadId);
                Assert.Empty(ship.Children);
                Assert.Contains(logLines, l => l.Contains("[Mission]")
                    && l.Contains("1 joined vessel(s) not listed as separated pieces"));
            }
            finally
            {
                ParsekLog.ResetTestOverrides();
            }
        }

        // catches: the join edge renumbering the ship's interval keys (Mission.ExcludedIntervalKeys
        // stores them), and the "Launch" word at that boundary in the phrase and in the
        // expanded interval rows' Start / End event cells.
        [Fact]
        public void Build_PartnerJoin_KeepsTheKeysAndCarriesNoEventWord()
        {
            List<MissionVesselRow> rows = BuildPartnerJoinRows();
            MissionVesselRow ship = Assert.Single(rows);
            Assert.Equal(new[] { "L", "L/seg1" }, MissionVesselRowBuilder.IntervalKeys(ship).ToArray());
            Assert.Equal("Launch", ship.Intervals[0].StartEvent);
            Assert.Equal("", ship.Intervals[0].EndEvent);
            Assert.Equal("", ship.Intervals[1].StartEvent);
            Assert.Equal("Orbiting", ship.Intervals[1].EndEvent);
            Assert.Equal("Launch" + Arrow + "Orbiting", ship.EventPhrase);
        }

        // catches: the joined vessel's controllers subtracted from the ship it joined, so the
        // interval after the join read "(no controllers)" instead of the ship's own probe.
        [Fact]
        public void Build_PartnerJoin_TakesNothingFromTheShipsLabel()
        {
            MissionVesselRow ship = Assert.Single(BuildPartnerJoinRows());
            Assert.Equal("probe x1", ship.Intervals[0].CompositionLabel);
            Assert.Equal("probe x1", ship.Intervals[1].CompositionLabel);
        }

        [Fact]
        public void BuildEventPhrase_RowStartLaunch_IsUnaffectedByTheJoin()
        {
            // The row's OWN start event "Launch" is a real launch and stays first.
            List<MissionVesselRow> rows = BuildPartnerJoinRows();
            MissionVesselRow ship = rows.Find(r => r.OwnerHeadId == "L");
            Assert.NotNull(ship);
            Assert.Equal("Launch", ship.StartEvent);
            Assert.StartsWith("Launch" + Arrow, ship.EventPhrase);

            // A plain launch-to-orbit row with no boundary keeps its start piece too.
            List<MissionVesselRow> plain = BuildRows(
                new[] { Leg("solo", "C", 0, 0, 100, pods: 1, terminal: TerminalState.Orbiting) },
                null);
            Assert.Equal("Launch" + Arrow + "Orbiting", Assert.Single(plain).EventPhrase);
        }

        [Fact]
        public void BuildEventPhrase_OwnSeparationBoundary_StillRendersAlongsideTheJoin()
        {
            // A genuine own separation (decouple) on the same vessel still names the piece that
            // left, with a joined vessel's edge earlier in the same row.
            List<MissionVesselRow> rows = BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, probes: 1, vessel: "Duna Supply 1"),
                    Leg("cont", "C", 1, 50, 100, probes: 1, vessel: "Duna Supply 1"),
                    Leg("partner", "C3", 0, 50, 180, probes: 2,
                        vessel: "Depot Station Duna I", terminal: TerminalState.Orbiting),
                    Leg("cont2", "C4", 0, 100, 200, probes: 1, vessel: "Duna Supply 1",
                        terminal: TerminalState.Orbiting),
                    Leg("stage", "C5", 0, 100, 150, probes: 1, parentAnchor: "cont",
                        vessel: "Duna Supply 1 Stage", terminal: TerminalState.Destroyed),
                },
                new[]
                {
                    BP("launchbp", BranchPointType.Launch, new[] { "L" }, new[] { "partner" }),
                    BP("decbp", BranchPointType.JointBreak, new[] { "cont" },
                        new[] { "cont2", "stage" }, splitCause: "DECOUPLE"),
                });

            MissionVesselRow ship = rows.Find(r => r.OwnerHeadId == "L");
            Assert.NotNull(ship);
            Assert.Equal(3, ship.Intervals.Count);
            Assert.Equal(
                "Launch" + Arrow + "Decoupled (Duna Supply 1 Stage)" + Arrow + "Orbiting",
                ship.EventPhrase);
            MissionVesselRow stage = Assert.Single(ship.Children);
            Assert.Equal("Duna Supply 1 Stage", stage.VesselName);
        }

        // catches: a piece that separated from the joined vessel vanishing with the joined
        // vessel's row. It moves up to the row the joined vessel hung under.
        [Fact]
        public void Build_PieceThatLeftTheJoinedVessel_MovesUpToTheShipRow()
        {
            List<MissionVesselRow> rows = BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, probes: 1, vessel: "Duna Supply 1"),
                    Leg("cont", "C", 1, 50, 200, probes: 1, vessel: "Duna Supply 1",
                        terminal: TerminalState.Orbiting),
                    Leg("partner", "C3", 0, 50, 80, probes: 2, vessel: "Tug"),
                    Leg("tugCont", "C6", 0, 80, 180, probes: 1, vessel: "Tug",
                        terminal: TerminalState.Orbiting),
                    Leg("tugStage", "C7", 0, 80, 120, probes: 1, parentAnchor: "partner",
                        vessel: "Tug Stage", terminal: TerminalState.Destroyed),
                },
                new[]
                {
                    BP("launchbp", BranchPointType.Launch, new[] { "L" }, new[] { "partner" }),
                    BP("tugdec", BranchPointType.JointBreak, new[] { "partner" },
                        new[] { "tugCont", "tugStage" }, splitCause: "DECOUPLE"),
                });

            MissionVesselRow ship = Assert.Single(rows);
            MissionVesselRow moved = Assert.Single(ship.Children);
            Assert.Equal("Tug Stage", moved.VesselName);
            Assert.Equal("Decoupled", moved.StartEvent);
        }

        [Fact]
        public void Build_EvaKerbal_IsAPersonChildAndStaysOutOfThePhrase()
        {
            // A kerbal going EVA does not end the vessel's interval (crew peel), so the vessel
            // stays ONE interval and the kerbal hangs off it as a person row.
            List<MissionVesselRow> rows = BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 100, pods: 1,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" },
                        terminal: TerminalState.Orbiting),
                    Leg("eva", "C3", 0, 50, 90, eva: "Val Kerman", vessel: "Val Kerman",
                        terminal: TerminalState.Recovered),
                },
                new[] { BP("bp2", BranchPointType.EVA, new[] { "L" }, new[] { "eva" }) });

            Assert.Single(rows);
            MissionVesselRow ship = rows[0];
            Assert.Single(ship.Intervals);
            Assert.Single(ship.Children);
            Assert.True(ship.Children[0].IsPerson);
            Assert.Equal("Val Kerman", ship.Children[0].VesselName);
            Assert.Equal("EVA", ship.Children[0].StartEvent);
            // The kerbal is a child row, never a phrase boundary.
            Assert.Equal("Launch" + Arrow + "Orbiting", ship.EventPhrase);
        }

        [Fact]
        public void Build_SameTreeDock_SubIntervalStaysInsideTheOneVesselRow()
        {
            // A same-tree dock: the transport's line gains an "@dock" sub-interval, which must
            // flatten INTO the transport's row (still one physical vessel), with the station's
            // own line a separate root row.
            List<MissionVesselRow> rows = BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                    Leg("station", "C9", 0, 0, 50, probes: 2, vessel: "Munport Station",
                        crewNames: new[] { "Val Kerman" }),
                    Leg("dockedLeg", "C2", 0, 50, 120, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
                },
                new[]
                {
                    BP("dockbp", BranchPointType.Dock, new[] { "L", "station" },
                        new[] { "dockedLeg" }),
                });

            Assert.Equal(2, rows.Count);
            MissionVesselRow transport = rows.Find(r => r.OwnerHeadId == "L");
            Assert.NotNull(transport);
            Assert.Equal(2, transport.Intervals.Count);
            Assert.Contains("@dock", transport.Intervals[1].HeadLegId);
            Assert.Equal("Launch" + Arrow + "Docked", transport.EventPhrase);

            MissionVesselRow station = rows.Find(r => r.OwnerHeadId == "station");
            Assert.NotNull(station);
            Assert.Single(station.Intervals);
        }

        [Fact]
        public void Build_DockPartnerResolver_NamesThePartnerInThePhrase()
        {
            // The T1.4 fix for the flattened surface: with the resolver wired (as the UI wires
            // the real ResolveSameTreeDockPartnerVesselName), a dock boundary names the partner
            // on the default collapsed row - without it the name was only reachable by
            // expanding the interval detail in Advanced.
            var recs = new[]
            {
                Leg("L", "C", 0, 0, 50, pods: 1, crewNames: new[] { "Jeb Kerman" }),
                Leg("station", "C9", 0, 0, 50, probes: 2, vessel: "Munport Station",
                    crewNames: new[] { "Val Kerman" }),
                Leg("dockedLeg", "C2", 0, 50, 120, pods: 1, probes: 2,
                    crewNames: new[] { "Jeb Kerman", "Val Kerman" }),
            };
            var bps = new[]
            {
                BP("dockbp", BranchPointType.Dock, new[] { "L", "station" },
                    new[] { "dockedLeg" }),
            };
            var tree = new RecordingTree { Id = "t", RootRecordingId = "L" };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            tree.BranchPoints.AddRange(bps);
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            MissionThroughLineView view = MissionThroughLineBuilder.Build(structure);

            List<MissionVesselRow> rows = MissionVesselRowBuilder.Build(
                MissionCompositionBuilder.Build(structure),
                (ownerHeadId, boundaryUT) =>
                    MissionPresentation.ResolveSameTreeDockPartnerVesselName(
                        structure, view, ownerHeadId, boundaryUT));

            MissionVesselRow transport = rows.Find(r => r.OwnerHeadId == "L");
            Assert.NotNull(transport);
            Assert.Equal("Launch" + Arrow + "Docked (Munport Station)", transport.EventPhrase);
        }

        [Fact]
        public void Build_ChildrenOrderedBySeparationUT()
        {
            // Two pieces peel at different UTs; lineage order must be separation time even if
            // the composition attaches them to different intervals.
            List<MissionVesselRow> rows = BuildRows(
                new[]
                {
                    Leg("L", "C", 0, 0, 30, pods: 1, probes: 2,
                        crewNames: new[] { "Jeb Kerman" }),
                    Leg("mid", "C2", 0, 30, 60, pods: 1, probes: 1,
                        crewNames: new[] { "Jeb Kerman" }),
                    Leg("cont", "C4", 0, 60, 100, pods: 1,
                        crewNames: new[] { "Jeb Kerman" }, terminal: TerminalState.Landed),
                    Leg("p1", "C3", 0, 30, 80, probes: 1, parentAnchor: "L",
                        vessel: "Probe One", terminal: TerminalState.Destroyed),
                    Leg("p2", "C5", 0, 60, 90, probes: 1, parentAnchor: "mid",
                        vessel: "Probe Two", terminal: TerminalState.Orbiting),
                },
                new[]
                {
                    BP("bp1", BranchPointType.JointBreak, new[] { "L" },
                        new[] { "mid", "p1" }, splitCause: "DECOUPLE"),
                    BP("bp2", BranchPointType.JointBreak, new[] { "mid" },
                        new[] { "cont", "p2" }, splitCause: "DECOUPLE"),
                });

            Assert.Single(rows);
            MissionVesselRow ship = rows[0];
            Assert.Equal(3, ship.Intervals.Count);
            Assert.Equal(2, ship.Children.Count);
            Assert.Equal("Probe One", ship.Children[0].VesselName);
            Assert.Equal("Probe Two", ship.Children[1].VesselName);
            Assert.Equal(
                "Launch" + Arrow + "Decoupled (Probe One)" + Arrow
                + "Decoupled (Probe Two)" + Arrow + "Landed",
                ship.EventPhrase);
        }

        // ---- inclusion: classify / expand-to-keys / apply ----

        [Fact]
        public void ClassifyInclusion_AllPartialNone()
        {
            List<MissionVesselRow> rows = BuildDecoupleRows();
            MissionVesselRow ship = rows[0]; // intervals: "L" + "L/seg1"

            var excluded = new HashSet<string>();
            Assert.Equal(MissionVesselInclusion.All,
                MissionVesselRowBuilder.ClassifyInclusion(ship, excluded));

            excluded.Add(ship.Intervals[0].HeadLegId);
            Assert.Equal(MissionVesselInclusion.Partial,
                MissionVesselRowBuilder.ClassifyInclusion(ship, excluded));

            excluded.Add(ship.Intervals[1].HeadLegId);
            Assert.Equal(MissionVesselInclusion.None,
                MissionVesselRowBuilder.ClassifyInclusion(ship, excluded));

            // A child's exclusion never bleeds into the parent's classification (no cascade).
            excluded.Clear();
            excluded.Add(ship.Children[0].Intervals[0].HeadLegId);
            Assert.Equal(MissionVesselInclusion.All,
                MissionVesselRowBuilder.ClassifyInclusion(ship, excluded));
            Assert.Equal(MissionVesselInclusion.None,
                MissionVesselRowBuilder.ClassifyInclusion(ship.Children[0], excluded));
        }

        [Fact]
        public void IntervalKeys_AreTheVesselsOwnOnly()
        {
            List<MissionVesselRow> rows = BuildDecoupleRows();
            MissionVesselRow ship = rows[0];

            List<string> keys = MissionVesselRowBuilder.IntervalKeys(ship);
            Assert.Equal(2, keys.Count);
            Assert.Contains("L", keys);
            Assert.DoesNotContain(ship.Children[0].Intervals[0].HeadLegId, keys);
        }

        [Fact]
        public void ApplyVesselInclusion_ExpandsToExplicitOwnKeysAndNeverCascades()
        {
            List<MissionVesselRow> rows = BuildDecoupleRows();
            MissionVesselRow ship = rows[0];
            string childKey = ship.Children[0].Intervals[0].HeadLegId;

            // Excluding the vessel writes exactly its own keys; the child's key is untouched
            // (the non-cascading contract - the booster keeps looping when the ship is dropped).
            var excluded = new HashSet<string>();
            int changed = MissionVesselRowBuilder.ApplyVesselInclusion(ship, false, excluded);
            Assert.Equal(2, changed);
            Assert.Equal(2, excluded.Count);
            Assert.DoesNotContain(childKey, excluded);

            // Re-including removes exactly those keys; a stray child exclusion survives.
            excluded.Add(childKey);
            changed = MissionVesselRowBuilder.ApplyVesselInclusion(ship, true, excluded);
            Assert.Equal(2, changed);
            Assert.Single(excluded);
            Assert.Contains(childKey, excluded);

            // Idempotent: re-applying the same state changes nothing.
            Assert.Equal(0, MissionVesselRowBuilder.ApplyVesselInclusion(ship, true, excluded));
        }

        [Fact]
        public void Build_NullOrEmptyRoots_YieldNoRows()
        {
            Assert.Empty(MissionVesselRowBuilder.Build(null));
            Assert.Empty(MissionVesselRowBuilder.Build(new List<MissionCompositionNode>()));
            Assert.Equal(MissionVesselInclusion.All,
                MissionVesselRowBuilder.ClassifyInclusion(null, null));
            Assert.Empty(MissionVesselRowBuilder.IntervalKeys(null));
            Assert.Equal(0, MissionVesselRowBuilder.ApplyVesselInclusion(null, true, null));
            Assert.Equal("", MissionVesselRowBuilder.BuildEventPhrase(null));
        }
    }
}
