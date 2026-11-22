using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The Missions half of the Profile (design-mission-profile.md sections 5, 9, 11.1): the
    /// line builder on synthetic trees (docks either way, stagings batched, debris dropped,
    /// short EVAs), the empty state, the log lines, and row independence on a real flight.
    /// </summary>
    [Collection("Sequential")]
    public class MissionProfileBuilderTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public MissionProfileBuilderTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionCompositionBuilder.SuppressLogging = true;
            MissionStructureListBuilder.SuppressLogging = true;
            DockEventGraph.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionCompositionBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
        }

        // ------------------------------------------------------------------ synthetic trees

        private static Recording Leg(ProfilePiece piece, double start, double end, uint pid,
            TerminalState? terminal = null, string chain = null, int chainIndex = -1)
        {
            Recording r = piece.Build();
            r.ExplicitStartUT = start;
            r.ExplicitEndUT = end;
            r.VesselPersistentId = pid;
            r.TerminalStateValue = terminal;
            r.ChainId = chain;
            r.ChainIndex = chainIndex;
            r.Controllers = new List<ControllerInfo> { new ControllerInfo { type = "ProbeCore" } };
            return r;
        }

        private static BranchPoint BP(string id, BranchPointType type, double ut, string[] parents, string[] children,
            string cause = null)
            => new BranchPoint
            {
                Id = id, Type = type, UT = ut, SplitCause = cause,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };

        private static RecordingTree Tree(string root, IEnumerable<Recording> recs, IEnumerable<BranchPoint> bps)
        {
            var tree = new RecordingTree { Id = "t-" + root, RootRecordingId = root, TreeName = "Synthetic" };
            foreach (Recording r in recs) tree.Recordings[r.RecordingId] = r;
            foreach (BranchPoint b in bps) tree.BranchPoints.Add(b);
            foreach (BranchPoint b in bps)
            {
                foreach (string c in b.ChildRecordingIds) tree.Recordings[c].ParentBranchPointId = b.Id;
                // A parent's ChildBranchPointId is the branch point its leg ENDS at.
                foreach (string p in b.ParentRecordingIds)
                    if (Math.Abs(tree.Recordings[p].EndUT - b.UT) < 1e-6) tree.Recordings[p].ChildBranchPointId = b.Id;
            }
            return tree;
        }

        private static MissionProfile Build(RecordingTree tree, ICollection<string> partners = null,
            Func<BranchPoint, string, string> partnerResolver = null)
            => MissionProfileBuilder.BuildProfile(new MissionProfileRequest
            {
                Mission = new Mission("m", tree.Id, "Synthetic"),
                Tree = tree,
                PartnerLegIds = partners,
                BodyFacts = RecordingOptimizer.StockBodyFacts,
                MergePartnerResolver = partnerResolver,
                DateFormatter = ut => "D" + ut.ToString("F0", CultureInfo.InvariantCulture),
            });

        private static ProfilePiece Orbiting(string id, double t0, double t1)
            => new ProfilePiece(id).Cp(t0, t1, "Kerbin", 680000, 0.005);

        // catches: the dock read from the wrong side: this mission's ship was the side KSP
        // kept (its leg does not end Docked), so the partner came aboard.
        [Fact]
        public void Dock_PartnerAbsorbed_ReadsDockedPartnerAboard()
        {
            var recs = new[]
            {
                Leg(Orbiting("ship", 0, 100), 0, 100, 1),
                Leg(Orbiting("station", 0, 100), 0, 100, 2),
                Leg(Orbiting("combined", 100, 300), 100, 300, 1, TerminalState.Orbiting),
            };
            recs[0].VesselName = "Ship";
            recs[1].VesselName = "Station";
            recs[2].VesselName = "Ship";
            RecordingTree tree = Tree("ship", recs, new[]
            {
                BP("dock", BranchPointType.Dock, 100, new[] { "ship", "station" }, new[] { "combined" }),
            });
            MissionProfile p = Build(tree, new HashSet<string> { "station" });
            MissionProfileLine ship = MissionProfileLineBuilder.All(p.Lines).First(l => l.LineId == "ship");
            ProfileAnchorInput dock = ship.Anchors.Single(a => a.Kind == ProfileEventKind.Docking);
            Assert.False(dock.OwnShipAbsorbed);
            Assert.Equal("Station", dock.RefName);
            Assert.Contains(p.Rows, r => r.Element == "Docked: Station aboard");
        }

        // catches: the own-ship-absorbed reading lost: the ship's leg ends Docked into a
        // partner leg, so the line reads "Docked to <partner>".
        [Fact]
        public void Dock_OwnShipAbsorbed_ReadsDockedToPartner()
        {
            var recs = new[]
            {
                Leg(Orbiting("ship", 0, 100), 0, 100, 1, TerminalState.Docked),
                Leg(Orbiting("combined", 100, 300), 100, 300, 2, TerminalState.Orbiting),
            };
            recs[0].VesselName = "Ship";
            recs[1].VesselName = "Station";
            RecordingTree tree = Tree("ship", recs, new[]
            {
                BP("dock", BranchPointType.Dock, 100, new[] { "ship" }, new[] { "combined" }),
            });
            MissionProfile p = Build(tree, new HashSet<string> { "combined" }, (bp, viewer) => "Station (mission 'Depot')");
            ProfileAnchorInput dock = MissionProfileLineBuilder.All(p.Lines).SelectMany(l => l.Anchors)
                .Single(a => a.Kind == ProfileEventKind.Docking);
            Assert.True(dock.OwnShipAbsorbed);
            Assert.Contains(p.Rows, r => r.Element == "Docked to Station (mission 'Depot')");
        }

        // catches: debris drawn as a line, stagings at one moment not batched like the Log,
        // or a controlled child not opening its own line under the separation event.
        [Fact]
        public void Separations_DebrisBatchedAsStagings_ControlledChildNested()
        {
            Recording root = Leg(Orbiting("root", 0, 1000), 0, 1000, 1, TerminalState.Orbiting);
            root.VesselName = "Ship";
            Recording d1 = Leg(Orbiting("d1", 100, 200), 100, 200, 5, TerminalState.Destroyed);
            Recording d2 = Leg(Orbiting("d2", 100.05, 200), 100.05, 200, 6, TerminalState.Orbiting);
            d1.IsDebris = d2.IsDebris = true;
            Recording probe = Leg(Orbiting("probe", 500, 1000), 500, 1000, 7, TerminalState.Orbiting);
            probe.VesselName = "Probe";
            // A decoupled controlled child is anchored to its parent (an offshoot, not the
            // vessel continuing).
            probe.ParentAnchorRecordingId = "root";
            RecordingTree tree = Tree("root", new[] { root, d1, d2, probe }, new[]
            {
                BP("s1", BranchPointType.JointBreak, 100, new[] { "root" }, new[] { "d1" }, "DECOUPLE"),
                BP("s2", BranchPointType.JointBreak, 100.05, new[] { "root" }, new[] { "d2" }, "DECOUPLE"),
                BP("sep", BranchPointType.JointBreak, 500, new[] { "root" }, new[] { "probe" }, "DECOUPLE"),
            });
            MissionProfile p = Build(tree);
            var lines = MissionProfileLineBuilder.All(p.Lines).ToList();
            Assert.DoesNotContain(lines, l => l.LineId == "d1" || l.LineId == "d2");
            MissionProfileLine rootLine = lines.First(l => l.LineId == "root");
            Assert.True(rootLine.Anchors.Count(a => a.Kind == ProfileEventKind.StageSeparation) == 1,
                string.Join(",", lines.Select(l => l.LineId + ":" + string.Join("/", l.Pieces.Select(x => x.RecordingId))
                    + "[" + string.Join("/", l.Anchors.Select(a => a.Kind + "@" + a.UT)) + "]")));
            ProfileAnchorInput stage = rootLine.Anchors.Single(a => a.Kind == ProfileEventKind.StageSeparation);
            Assert.Equal(2, stage.Count);
            Assert.Equal("destroyed, in orbit", stage.RefName);
            string rows = string.Join(" // ", p.Rows.Select(MissionProfileFixtureTests.Row));
            Assert.True(p.Rows.Count(r => r.Element == "Separated: Probe") == 1, rows);
            MissionProfileRow sepRow = p.Rows.Single(r => r.Element == "Separated: Probe");
            MissionProfileRow probeHeader = p.Rows.Single(r => r.Kind == MissionProfileRowKind.Line && r.LineId == "probe");
            Assert.Equal(sepRow.Depth + 1, probeHeader.Depth);
            Assert.Equal(sepRow.Key, probeHeader.ParentKey);
            Assert.Contains(p.Rows, r => r.Element == "Stage separation (2 pieces)"
                && r.Hover == "Stage separation (2 pieces): destroyed, in orbit.");
        }

        // catches: a chain with a hole (index 0 then 2) splitting the vessel into two lines.
        [Fact]
        public void ChainHole_IsOneLine()
        {
            Recording a = Leg(Orbiting("c0", 0, 1000), 0, 1000, 1, null, "chain", 0);
            Recording c = Leg(Orbiting("c2", 1000, 2000), 1000, 2000, 1, TerminalState.Orbiting, "chain", 2);
            a.VesselName = c.VesselName = "Ship";
            RecordingTree tree = Tree("c0", new[] { a, c }, new BranchPoint[0]);
            MissionProfile p = Build(tree);
            MissionProfileLine line = MissionProfileLineBuilder.All(p.Lines).Single(l => l.IsDrawn);
            Assert.Equal(new[] { "c0", "c2" }, line.Pieces.Select(r => r.RecordingId).ToArray());
            Assert.Equal("Orbit", string.Join(",", line.Plan.Phases.Select(ph => ph.Kind.ToString())));
        }

        // catches: a short EVA (out and back aboard within two minutes) drawn as a child line
        // instead of one event row, or a long EVA collapsed.
        [Fact]
        public void ShortEva_CollapsesToOneEventRow_LongEvaKeepsItsLine()
        {
            RecordingTree Shape(double evaSeconds)
            {
                Recording ship = Leg(Orbiting("ship", 0, 100), 0, 100, 1);
                Recording shipDuring = Leg(Orbiting("during", 100, 100 + evaSeconds), 100, 100 + evaSeconds, 1, TerminalState.Boarded);
                Recording kerbal = Leg(new ProfilePiece("jeb", "Jebediah Kerman").Eva("Jebediah Kerman")
                    .Sec(SegmentEnvironment.ExoBallistic, 100, 100 + evaSeconds), 100, 100 + evaSeconds, 9, TerminalState.Boarded);
                kerbal.EvaCrewName = "Jebediah Kerman";
                Recording after = Leg(Orbiting("after", 100 + evaSeconds, 500), 100 + evaSeconds, 500, 1, TerminalState.Orbiting);
                foreach (Recording r in new[] { ship, shipDuring, after }) r.VesselName = "Ship";
                kerbal.VesselName = "Jebediah Kerman";
                RecordingTree t = Tree("ship", new[] { ship, shipDuring, kerbal, after }, new[]
                {
                    BP("eva", BranchPointType.EVA, 100, new[] { "ship" }, new[] { "during", "jeb" }),
                    BP("board", BranchPointType.Board, 100 + evaSeconds, new[] { "jeb", "during" }, new[] { "after" }),
                });
                return t;
            }
            MissionProfile shortEva = Build(Shape(3));
            Assert.Contains(shortEva.Rows, r => r.Element == "EVA: Jebediah Kerman (3s), boarded");
            Assert.DoesNotContain(shortEva.Rows, r => r.Kind == MissionProfileRowKind.Line && r.Element.StartsWith("EVA:", StringComparison.Ordinal));
            Assert.True(MissionProfileLineBuilder.All(shortEva.Lines).Single(l => l.IsPerson).CollapsedToEvent);

            MissionProfile longEva = Build(Shape(400));
            Assert.Contains(longEva.Rows, r => r.Kind == MissionProfileRowKind.Line && r.Element == "EVA: Jebediah Kerman");
            Assert.Contains(longEva.Rows, r => r.Element == "EVA: Jebediah Kerman");
        }

        // catches: the empty state missing (a tree with no recorded flight shows the Log's
        // one muted row) and a null tree throwing.
        [Fact]
        public void EmptyTree_ShowsTheLogsEmptyRow()
        {
            MissionProfile p = Build(new RecordingTree { Id = "empty", TreeName = "Empty" });
            Assert.True(p.IsEmpty);
            Assert.Equal(ProfileLabels.EmptyText, p.Rows.Single().Element);
            MissionProfile none = MissionProfileBuilder.BuildProfile(new MissionProfileRequest());
            Assert.True(none.IsEmpty);
            Assert.Equal("Parsek - Profile", none.Title);
        }

        // catches: the build summary, the per-line trace or the rate-limited In space line
        // missing from the log (section 9), or a culture-dependent number in them.
        [Fact]
        public void Logging_SummaryPerLineAndUnclassified()
        {
            Recording r = Leg(new ProfilePiece("lost").Sec(SegmentEnvironment.ExoBallistic, 0, 500, alt0: 200000, alt1: 300000),
                0, 500, 1);
            r.VesselName = "Lost";
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                Build(Tree("lost", new[] { r }, new BranchPoint[0]));
            }
            finally
            {
                CultureInfo.CurrentCulture = saved;
            }
            Assert.Contains(logLines, l => l.Contains("[Profile]") && l.Contains("Profile build: mission=m tree=t-lost lines=1")
                && l.Contains("unclassified=1"));
            Assert.Contains(logLines, l => l.Contains("[Profile]") && l.Contains("Profile line: id=lost name='Lost' pieces=1 ut=[0.0,500.0]")
                && l.Contains("Unclassified(space-start)"));
            Assert.Contains(logLines, l => l.Contains("[Profile]") && l.Contains("In space phase: line=lost")
                && l.Contains("reason=orbit unknown"));
        }

        // catches: rows that depend on where the recording was cut (section 11.2 row
        // independence): mun-landing-recorded's one version-1 recording re-cut inside a coast
        // with checkpoint evidence spanning the cut, inside a burn, at the atmosphere exit and
        // inside the capture burn reads the same plan.
        [Fact]
        public void RowIndependence_ReCutRecording_SamePlan()
        {
            string Signature(PhasePlan plan)
            {
                var ic = CultureInfo.InvariantCulture;
                return string.Join("\n", plan.Phases.Select(ph => ph.Kind + " " + ph.StartUT.ToString("F1", ic) + "-"
                    + ph.EndUT.ToString("F1", ic) + " :: " + string.Join("; ", ph.Events.Select(e => e.Kind + "/" + e.BurnKind
                    + "@" + e.UT.ToString("F1", ic) + "-" + (double.IsNaN(e.EndUT) ? "" : e.EndUT.ToString("F1", ic))))));
            }
            PhasePlan Run(List<Recording> pieces)
            {
                var input = new VesselLineInput
                {
                    LineId = "line", LaunchBodyName = "Kerbin", BodyFacts = RecordingOptimizer.StockBodyFacts,
                };
                input.Pieces.AddRange(pieces);
                return MissionPhaseAnalyzer.Analyze(input);
            }
            Recording Root() => StructureListBdockFixtureTests.LoadFixtureTrees("mun-landing-recorded", 1)[0]
                .Recordings["61f3775361fe4130a66a69b1425b7209"];

            string whole = Signature(Run(new List<Recording> { Root() }));
            Recording head = Root();
            var pieces = new List<Recording> { head };
            // Cuts on physics section boundaries that carry a sample (SplitAtUT interpolates any
            // other cut through Unity's Quaternion.Slerp): the atmosphere exit, inside the
            // parking-orbit coast whose checkpoint evidence lies on both sides of the cut,
            // inside the merged correction burn, between the capture and the descent.
            var regions = new[] { new[] { 212.0, 212.5 }, new[] { 300.5, 1871.0 }, new[] { 4897.0, 4915.0 } };
            foreach (double[] region in regions)
            {
                Recording last = pieces[pieces.Count - 1];
                double cut = last.TrackSections.Select(t => t.startUT)
                    .Where(ut => ut > region[0] && ut < region[1] && last.Points.Any(pt => pt.ut == ut))
                    .DefaultIfEmpty(double.NaN).First();
                Assert.False(double.IsNaN(cut), "no sampled section start in " + region[0] + ".." + region[1]);
                Recording tail = RecordingOptimizer.SplitAtUT(last, cut);
                Assert.NotNull(tail);
                tail.RecordingId = "cut-" + cut.ToString("F0", CultureInfo.InvariantCulture);
                tail.TerminalStateValue = last.TerminalStateValue;
                last.TerminalStateValue = null;
                pieces.Add(tail);
            }
            Assert.Equal(4, pieces.Count);
            Assert.Equal(whole, Signature(Run(pieces)));
        }
    }
}
