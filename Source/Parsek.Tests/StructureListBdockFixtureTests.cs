using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace Parsek.Tests
{
    /// <summary>
    /// The mission Log against the committed bdock-recorded fixture (GUI-4's docking
    /// mission "Kerbal X #2", whose Log used to read 31 rows, mostly noise), plus the
    /// synthetic shapes the fixture does not carry. Each cell names the defect it pins.
    /// </summary>
    [Collection("Sequential")]
    public class StructureListBdockFixtureTests : IDisposable
    {
        private readonly ITestOutputHelper output;
        private const string DockingTreeId = "8c677bba0ec04e558c6ebec4ad8c3c9f";
        private const string FirstTreeId = "788554a928324f148dd313dd26322638";

        public StructureListBdockFixtureTests(ITestOutputHelper output)
        {
            this.output = output;
            ParsekLog.ResetTestOverrides();
            ParsekLog.SuppressLogging = true;
            MissionStructureBuilder.SuppressLogging = true;
            MissionStructureListBuilder.SuppressLogging = true;
            DockEventGraph.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            MissionStructureBuilder.SuppressLogging = false;
            MissionStructureListBuilder.SuppressLogging = false;
            DockEventGraph.SuppressLogging = false;
        }

        // The stock titles of the parts this fixture stages (KSP 1.12 part cfgs).
        internal static string StockTitles(string partName)
        {
            switch (partName)
            {
                case "launchClamp1": return "TT18-A Launch Stability Enhancer";
                case "radialDecoupler1-2": return "Hydraulic Detachment Manifold";
                default: return null;
            }
        }

        internal static List<RecordingTree> LoadBdockTrees()
            => LoadFixtureTrees("bdock-recorded", 2);

        internal static List<RecordingTree> LoadFixtureTrees(string fixture, int expectedTrees)
        {
            string saveDir = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
                "harness", "fixtures", "saves", fixture));
            Assert.True(Directory.Exists(saveDir), "fixture missing: " + saveDir);
            ConfigNode root = ConfigNode.Load(Path.Combine(saveDir, "persistent.sfs"));
            ConfigNode game = root.GetNode("GAME") ?? root;
            var trees = new List<RecordingTree>();
            foreach (ConfigNode scen in game.GetNodes("SCENARIO"))
            {
                if (scen.GetValue("name") != "ParsekScenario") continue;
                foreach (ConfigNode tn in scen.GetNodes("RECORDING_TREE"))
                {
                    RecordingTree tree = RecordingTree.Load(tn);
                    foreach (Recording rec in tree.Recordings.Values)
                    {
                        string precTxt = Path.Combine(saveDir, "Parsek", "Recordings",
                            rec.RecordingId + ".prec.txt");
                        if (!File.Exists(precTxt)) continue;
                        ConfigNode node = ConfigNode.Load(precTxt);
                        if (node != null)
                            TrajectoryTextSidecarCodec.DeserializeTrajectoryFrom(node, rec);
                    }
                    trees.Add(tree);
                }
            }
            Assert.Equal(expectedTrees, trees.Count);
            return trees;
        }

        private static List<StructureStep> BuildDockingMissionLog(out List<RecordingTree> trees)
        {
            trees = LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            DockEventGraph graph = DockEventGraph.Build(trees, id => true, "test");
            Func<string, string, string> missionNames = (treeId, recId) =>
                treeId == FirstTreeId ? "Kerbal X" : treeId == DockingTreeId ? "Kerbal X #2" : null;
            MissionStructure structure = MissionStructureBuilder.Build(tree);
            return MissionStructureListBuilder.Build(tree, structure, StockTitles,
                (bp, viewer) => MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                    graph, tree.Id, bp, viewer, missionNames));
        }

        private static string Row(StructureStep s)
            => s.Label + " | " + s.Location + " | " + s.VesselName;

        // catches: every noise source the design review traced in this fixture's Log -
        // seeds, debris breakup, per-part staging beside its branch point, the symmetric
        // partner, dock coupling parts, the merged "End x2", and Prelaunch locations.
        [Fact]
        public void DockingMission_LogIsOneRowPerRealEvent()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            foreach (StructureStep s in steps)
                output.WriteLine(s.UT.ToString("F2", CultureInfo.InvariantCulture) + "  " + Row(s));

            var expected = new[]
            {
                "Launch | Kerbin, Launch Pad | Kerbal X",
                "Staged: 3 pieces (TT18-A Launch Stability Enhancer x3) | Kerbin, Launch Pad | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Decoupled (Kerbal X Probe) | Kerbin | Kerbal X",
                "Docked (Kerbal X (mission 'Kerbal X')) | Kerbin orbit | Kerbal X",
                "Undocked (Kerbal X) | Kerbin orbit | Kerbal X",
                "End: Orbiting | Kerbin orbit | Kerbal X",
                "End: Orbiting | Kerbin orbit | Kerbal X",
                "End: Orbiting | Kerbin orbit | Kerbal X Probe",
            };
            Assert.Equal(expected, steps.Select(Row).ToArray());
            Assert.All(steps, s => Assert.Null(s.Tooltip));
        }

        // catches: the in-game shape of this fixture. On load the optimizer splits the
        // docking mission's root at its atmosphere exit (UT 568.23, GUI-4 2026-10-01_1603)
        // into chain segments, forwarding every permanent part event as seeds at the cut;
        // the probe separation's branch point (UT 692.77) and the dock follow the cut onto
        // the TAIL (SplitParentLinks), where the Poodle shroud that drops with the probe
        // sits. The flight once drew a stray "Shroud jettisoned" row there; the Log must
        // read exactly as before the split.
        [Fact]
        public void DockingMission_AfterTheOptimizerSplit_ReadsTheSameRows()
        {
            List<RecordingTree> trees = LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            Recording head = tree.Recordings["5157d6555bd3499592c46d8508dbedf4"];
            Recording tail = RecordingOptimizer.SplitAtUT(head, 568.23160278301521);
            Assert.NotNull(tail);
            tail.RecordingId = "d60398f6229c4e669e8dcee87ffcf698";
            tail.VesselName = head.VesselName;
            tail.VesselPersistentId = head.VesselPersistentId;
            tail.StartBodyName = head.StartBodyName;
            head.ChainId = tail.ChainId = "091139ec254d446e93cbf89b26d975d1";
            head.ChainIndex = 0;
            tail.ChainIndex = 1;
            tail.ChildBranchPointId = head.ChildBranchPointId;
            head.ChildBranchPointId = null;
            tail.TerminalStateValue = head.TerminalStateValue;
            head.TerminalStateValue = null;
            SplitParentLinks.RepointToTail(tree, head.RecordingId, tail, tail.StartUT, null, "test");
            tree.Recordings[tail.RecordingId] = tail;
            BranchPoint dock = tree.BranchPoints.Single(b => b.Type == BranchPointType.Dock);
            BranchPoint probe = tree.BranchPoints.Single(b => b.Id == "fc4591e43cdf443993a0d6399a9cf516");
            Assert.Equal(new[] { tail.RecordingId }, dock.ParentRecordingIds);
            Assert.Equal(new[] { tail.RecordingId }, probe.ParentRecordingIds);
            Assert.All(tree.BranchPoints.Where(b => b.UT < 568.23),
                b => Assert.Equal(new[] { head.RecordingId }, b.ParentRecordingIds));
            // The cut really did forward the launch's part state onto the tail.
            Assert.Contains(tail.PartEvents, e => Math.Abs(e.ut - 568.23160278301521) < 1e-6
                && e.eventType == PartEventType.ShroudJettisoned);

            DockEventGraph graph = DockEventGraph.Build(trees, id => true, "test");
            Func<string, string, string> missionNames = (treeId, recId) =>
                treeId == FirstTreeId ? "Kerbal X" : treeId == DockingTreeId ? "Kerbal X #2" : null;
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles,
                (bp, viewer) => MissionStructureListBuilder.DescribeMergePartnerFromGraph(
                    graph, tree.Id, bp, viewer, missionNames));
            foreach (StructureStep s in steps)
                output.WriteLine(s.UT.ToString("F2", CultureInfo.InvariantCulture) + "  " + Row(s));

            Assert.DoesNotContain(steps, s => s.Label.StartsWith("Shroud", StringComparison.Ordinal));
            Assert.Equal(11, steps.Count);
            Assert.Contains(steps, s => s.Label == "Decoupled (Kerbal X Probe)");
        }

        // catches (a): the shroud seeds a root records at its first frame, and the seed the
        // background recorder writes when a split child loads (the probe's Mainsail shroud
        // 0.52 s after its separation) reading as staging.
        [Fact]
        public void Seeds_AtARecordingStart_AreNotEvents()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out List<RecordingTree> trees);
            Assert.DoesNotContain(steps, s => s.Label.Contains("Shroud"));
            // The fixture really carries them, so the absence is the filter's work.
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            Assert.Contains(tree.Recordings.Values, r => !r.IsDebris
                && r.PartEvents.Any(e => e.eventType == PartEventType.ShroudJettisoned));
        }

        // catches (a): RecordingOptimizer.SplitAtUT copies every permanent part event of the
        // first half into the second at the split UT; the Log listed the whole launch again.
        [Fact]
        public void ChainSplitSeeds_AreNotRepeatedAsStaging()
        {
            var head = new Recording
            {
                RecordingId = "head", VesselName = "Stack", ChainId = "c", ChainIndex = 0,
                ExplicitStartUT = 0, ExplicitEndUT = 180, StartBodyName = "Kerbin",
            };
            var tail = new Recording
            {
                RecordingId = "tail", VesselName = "Stack", ChainId = "c", ChainIndex = 1,
                ExplicitStartUT = 180, ExplicitEndUT = 600, StartBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
            };
            head.PartEvents.Add(new PartEvent { ut = 40, eventType = PartEventType.Decoupled, partPersistentId = 1, partName = "radialDecoupler1-2" });
            head.PartEvents.Add(new PartEvent { ut = 90, eventType = PartEventType.FairingJettisoned, partPersistentId = 2, partName = "fairingSize1" });
            RecordingOptimizer.ForwardPermanentStateEvents(head.PartEvents, tail.PartEvents, 180);
            Assert.Equal(2, tail.PartEvents.Count);

            var tree = new RecordingTree { Id = "t", RootRecordingId = "head" };
            tree.Recordings["head"] = head;
            tree.Recordings["tail"] = tail;
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);

            Assert.Single(steps, s => s.Label.StartsWith("Staged", StringComparison.Ordinal));
            Assert.Single(steps, s => s.Label.StartsWith("Fairing", StringComparison.Ordinal));
            Assert.DoesNotContain(steps, s => s.UT == 180 && s.Kind == StructureStepKind.Staging);
        }

        // catches: the Log losing a stage's own part when the load repair had to stop and the
        // branch point still names the first segment (here the middle segment carries no
        // trajectory, so its start is unknown): the decouple's part sits two segments later
        // and must still be absorbed into the stage, not drawn as a row of its own.
        [Fact]
        public void StaleBranchPointTheRepairCouldNotMove_KeepsItsStageParts()
        {
            var head = new Recording
            {
                RecordingId = "head", VesselName = "Stack", ChainId = "c", ChainIndex = 0,
                ExplicitStartUT = 0, ExplicitEndUT = 180, StartBodyName = "Kerbin",
            };
            var middle = new Recording
            {
                RecordingId = "middle", VesselName = "Stack", ChainId = "c", ChainIndex = 1,
                StartBodyName = "Kerbin",
            };
            var tail = new Recording
            {
                RecordingId = "tail", VesselName = "Stack", ChainId = "c", ChainIndex = 2,
                ExplicitStartUT = 250, ExplicitEndUT = 600, StartBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
            };
            tail.PartEvents.Add(new PartEvent { ut = 300.2, eventType = PartEventType.Decoupled, partPersistentId = 7, partName = "radialDecoupler1-2" });
            var tree = new RecordingTree { Id = "t", RootRecordingId = "head" };
            tree.Recordings["head"] = head;
            tree.Recordings["middle"] = middle;
            tree.Recordings["tail"] = tail;
            tree.BranchPoints.Add(new BranchPoint
            {
                Id = "stage", Type = BranchPointType.JointBreak, UT = 300, CoalesceWindow = 0.5,
                SplitCause = "DECOUPLE", ParentRecordingIds = new List<string> { "head" },
                ChildRecordingIds = new List<string>(),
            });
            Assert.Equal(0, SplitParentLinks.RepairStaleParents(tree, "test"));
            Assert.Equal("head", tree.BranchPoints[0].ParentRecordingIds[0]);

            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);
            foreach (StructureStep s in steps)
                output.WriteLine(s.UT.ToString("F2", CultureInfo.InvariantCulture) + "  " + Row(s));

            StructureStep stage = Assert.Single(steps, s => s.Kind == StructureStepKind.Staging);
            Assert.Equal(300, stage.UT);
            Assert.Contains("Hydraulic Detachment Manifold", stage.Label);
        }

        // catches (a) mirror: a root's start-UT Decoupled is the launch clamps letting go -
        // a real event - while its start-UT jettisons are the recorder's seeds.
        [Fact]
        public void RootStart_DecoupleIsReal_JettisonIsSeed()
        {
            var r = new Recording
            {
                RecordingId = "r", VesselName = "V", ExplicitStartUT = 10, ExplicitEndUT = 100,
                StartBodyName = "Kerbin", LaunchSiteName = "Launch Pad",
                TerminalStateValue = TerminalState.Orbiting,
            };
            Assert.True(MissionStructureListBuilder.IsSeedEvent(r,
                new PartEvent { ut = 10, eventType = PartEventType.ShroudJettisoned }, continuation: false));
            Assert.False(MissionStructureListBuilder.IsSeedEvent(r,
                new PartEvent { ut = 10, eventType = PartEventType.Decoupled }, continuation: false));
            Assert.False(MissionStructureListBuilder.IsSeedEvent(r,
                new PartEvent { ut = 10.5, eventType = PartEventType.ShroudJettisoned }, continuation: false));
            // A continuing segment: only its exact start is a seed by time alone.
            Assert.True(MissionStructureListBuilder.IsSeedEvent(r,
                new PartEvent { ut = 10, eventType = PartEventType.Decoupled }, continuation: true));
            Assert.False(MissionStructureListBuilder.IsSeedEvent(r,
                new PartEvent { ut = 10.4, eventType = PartEventType.FairingJettisoned }, continuation: true));
        }

        // catches: a time window after a continuation's start swallowing a REAL jettison
        // (a scripted fairing deploy right at an atmosphere-exit chain split), while the
        // background loaded-physics seed of an ancestor's part, the same distance after a
        // split, still drops.
        [Fact]
        public void RealJettisonJustAfterAChainSplit_IsARow_AncestorRestatementIsNot()
        {
            var head = new Recording
            {
                RecordingId = "head", VesselName = "Stack", ChainId = "c", ChainIndex = 0,
                ExplicitStartUT = 0, ExplicitEndUT = 180, StartBodyName = "Kerbin",
            };
            var tail = new Recording
            {
                RecordingId = "tail", VesselName = "Stack", ChainId = "c", ChainIndex = 1,
                ExplicitStartUT = 180, ExplicitEndUT = 600, StartBodyName = "Kerbin",
                TerminalStateValue = TerminalState.Orbiting,
            };
            head.PartEvents.Add(new PartEvent { ut = 0, eventType = PartEventType.ShroudJettisoned, partPersistentId = 5, partName = "liquidEngine2" });
            // A seed of the ancestor's shroud 0.52 s after the split (the BG load shape)...
            tail.PartEvents.Add(new PartEvent { ut = 180.52, eventType = PartEventType.ShroudJettisoned, partPersistentId = 5, partName = "liquidEngine2" });
            // ...and a real fairing deploy 0.4 s after the split, a part no ancestor jettisoned.
            tail.PartEvents.Add(new PartEvent { ut = 180.4, eventType = PartEventType.FairingJettisoned, partPersistentId = 9, partName = "fairingSize1" });
            var tree = new RecordingTree { Id = "t", RootRecordingId = "head" };
            tree.Recordings["head"] = head;
            tree.Recordings["tail"] = tail;

            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);

            Assert.Single(steps, s => s.Kind == StructureStepKind.Staging
                && s.Label == "Fairing jettisoned (fairingSize1)" && Math.Abs(s.UT - 180.4) < 1e-9);
            Assert.DoesNotContain(steps, s => s.Label.StartsWith("Shroud", StringComparison.Ordinal));
        }

        // catches (b): a debris piece's own breakup at its end UT ("Staged fuelTank x2",
        // "Staged liquidEngine2") listed as the mission's staging.
        [Fact]
        public void DebrisRecordings_ContributeNoRows()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            Assert.DoesNotContain(steps, s => s.Label.Contains("fuelTank") || s.Label.Contains("liquidEngine2"));
            Assert.DoesNotContain(steps, s => s.VesselName == "Kerbal X Debris");
        }

        // catches (c): the stage is the branch point - one row per JointBreak, with the
        // symmetric partner decoupler (whose PID the branch point does not store) inside it,
        // and no extra "Decoupled" row beside it.
        [Fact]
        public void EachDebrisBranchPoint_IsExactlyOneRow()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out List<RecordingTree> trees);
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            foreach (BranchPoint bp in tree.BranchPoints.Where(b => b.Type == BranchPointType.JointBreak))
                Assert.Single(steps, s => s.Kind != StructureStepKind.Launch && Math.Abs(s.UT - bp.UT) < 1e-6);
        }

        // catches (c) mirror: ripple staging - two branch points half a second apart are two
        // stages and must stay two rows (the 0.25 s simultaneous window's reason to exist).
        [Fact]
        public void RippleStaging_DistinctBranchPoints_StayDistinctRows()
        {
            var r = new Recording
            {
                RecordingId = "r", VesselName = "Lifter", ExplicitStartUT = 0, ExplicitEndUT = 300,
                StartBodyName = "Kerbin", TerminalStateValue = TerminalState.Orbiting,
            };
            r.PartEvents.Add(new PartEvent { ut = 50.0, eventType = PartEventType.Decoupled, partPersistentId = 1, partName = "radialDecoupler1-2" });
            r.PartEvents.Add(new PartEvent { ut = 50.5, eventType = PartEventType.Decoupled, partPersistentId = 2, partName = "radialDecoupler1-2" });
            var tree = new RecordingTree { Id = "t", RootRecordingId = "r" };
            tree.Recordings["r"] = r;
            // CoalesceWindow 0.5 is the recorder's production value: each event lies inside
            // BOTH branch points' windows, so this exercises the nearest-branch-point rule.
            tree.BranchPoints.Add(new BranchPoint { Id = "b1", Type = BranchPointType.JointBreak, UT = 50.0, SplitCause = "DECOUPLE", DebrisCount = 1, CoalesceWindow = 0.5, ParentRecordingIds = { "r" } });
            tree.BranchPoints.Add(new BranchPoint { Id = "b2", Type = BranchPointType.JointBreak, UT = 50.5, SplitCause = "DECOUPLE", DebrisCount = 1, CoalesceWindow = 0.5, ParentRecordingIds = { "r" } });

            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);

            Assert.Equal(2, steps.Count(s => s.Label == "Staged: 1 piece (Hydraulic Detachment Manifold)"));
        }

        // catches (d): the docking port and pod "Decoupled" at the undock UT on the docked
        // recording read as staging.
        [Fact]
        public void DockCouplingParts_AreNotStaging()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            Assert.DoesNotContain(steps, s => s.Label.Contains("dockingPort2") || s.Label.Contains("mk1-3pod"));
            Assert.DoesNotContain(steps, s => s.UT > 8949 && s.Kind == StructureStepKind.Staging);
        }

        // catches (e): two different vessels that share a name ending together collapsed
        // into one "End x2" row.
        [Fact]
        public void SameNamedVesselsEndingTogether_AreSeparateRows()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            List<StructureStep> ends = steps.Where(s => s.Kind == StructureStepKind.Terminal
                && s.VesselName == "Kerbal X").ToList();
            Assert.Equal(2, ends.Count);
            Assert.NotEqual(ends[0].RecordingId, ends[1].RecordingId);
            Assert.DoesNotContain(steps, s => s.Label.EndsWith(" x2", StringComparison.Ordinal));
        }

        // catches (f): in-flight decouples inheriting the root's launch context
        // ("Prelaunch", "Shores") because a debris branch point has no controlled child.
        [Fact]
        public void InFlightRows_NeverInheritTheLaunchContext()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            double launchUT = steps[0].UT;
            foreach (StructureStep s in steps.Where(x => x.UT - launchUT > 1.0))
            {
                Assert.DoesNotContain("Launch Pad", s.Location);
                Assert.DoesNotContain("Shores", s.Location);
            }
        }

        // catches: the first fixture tree (no dock) still reads its own story, and the
        // winglet that broke off with no branch point stays visible as its own row.
        [Fact]
        public void FirstMission_LogRows()
        {
            List<RecordingTree> trees = LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == FirstTreeId);
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);
            var expected = new[]
            {
                "Launch | Kerbin, Launch Pad | Kerbal X",
                "Staged: 3 pieces (TT18-A Launch Stability Enhancer x3) | Kerbin, Launch Pad | Kerbal X",
                "Staged: 1 piece (R8winglet) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Kerbal X",
                "Decoupled (Kerbal X Probe) | Kerbin | Kerbal X",
                "End: Orbiting | Kerbin orbit | Kerbal X",
                "End: Orbiting | Kerbin orbit | Kerbal X Probe",
            };
            Assert.Equal(expected, steps.Select(Row).ToArray());
        }

        // catches: GUI-3's mission (interbody-route-recorded, "Duna Supply 1", 38 rows before
        // the rework): the chain tail's launch re-statement at its cut, the depot vessel's
        // seeds at the UT of the branch point that created its recording (10.9 s after its
        // backfilled start), the Poodle shroud on the chain tail beside the probe separation
        // whose branch point names the head, and the dock / undock coupling parts.
        [Fact]
        public void DunaSupplyMission_LogIsOneRowPerRealEvent()
        {
            List<RecordingTree> trees = LoadFixtureTrees("interbody-route-recorded",
                CountTrees("interbody-route-recorded"));
            RecordingTree tree = trees.Single(t => t.Id == "3daf0cff159d413794c626137cd81002");
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), StockTitles);
            foreach (StructureStep s in steps)
                output.WriteLine(Row(s));

            var expected = new[]
            {
                "Launch | Kerbin, Launch Pad | Duna Supply 1",
                "Staged: 3 pieces (TT18-A Launch Stability Enhancer x3) | Kerbin, Launch Pad | Duna Supply 1",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Duna Supply 1",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Duna Supply 1",
                "Staged: 2 pieces (Hydraulic Detachment Manifold x2) | Kerbin | Duna Supply 1",
                "Decoupled (Duna Supply 1 Probe) | Kerbin | Duna Supply 1",
                "Docked (Depot Station Duna I) | Duna orbit | Duna Supply 1",
                "Undocked (Duna Supply 1) | Duna orbit | Depot Station Duna I",
                "End: Orbiting | Duna orbit | Depot Station Duna I",
                "End: Orbiting | Duna orbit | Duna Supply 1",
                "End: Orbiting | Kerbin orbit | Duna Supply 1 Probe",
            };
            Assert.Equal(expected, steps.Select(Row).ToArray());
        }

        private static int CountTrees(string fixture)
        {
            string saveDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", "harness", "fixtures", "saves", fixture));
            return File.ReadAllLines(Path.Combine(saveDir, "persistent.sfs")).Count(l => l.Trim() == "RECORDING_TREE");
        }

        // catches: the Time column - first row the date, later rows elapsed whole seconds.
        [Fact]
        public void DockingMission_TimeCells()
        {
            List<StructureStep> steps = BuildDockingMissionLog(out _);
            string[] cells = StructureTimeFormatter.FormatStepTimes(steps, ut => "DATE");
            Assert.Equal("DATE", cells[0]);
            Assert.Equal("T+0:00:00", cells[1]);   // the clamps, at the launch moment
            Assert.Equal("T+0:00:13", cells[2]);   // 404.37 - 391.07
            Assert.Equal("T+2:22:38", cells[6]);   // the dock at 8949.27
        }

        // catches: part titles resolved per ROW (the window passes a PartLoader lookup;
        // a per-row call multiplies across every Log open).
        [Fact]
        public void TitleResolver_IsCalledOncePerDistinctName()
        {
            var calls = new Dictionary<string, int>();
            List<RecordingTree> trees = LoadBdockTrees();
            RecordingTree tree = trees.Single(t => t.Id == DockingTreeId);
            MissionStructureListBuilder.Build(tree, MissionStructureBuilder.Build(tree), name =>
            {
                calls[name] = calls.TryGetValue(name, out int n) ? n + 1 : 1;
                return StockTitles(name);
            });
            Assert.NotEmpty(calls);
            Assert.All(calls, kv => Assert.Equal(1, kv.Value));
        }
    }

    /// <summary>Pure formatter and label cells for the Log's columns.</summary>
    [Collection("Sequential")]
    public class StructureListFormatterTests
    {
        [Theory]
        [InlineData(0.0, "T+0:00:00")]
        [InlineData(13.3, "T+0:00:13")]
        [InlineData(59.999, "T+0:00:59")]
        [InlineData(3725.9, "T+1:02:05")]
        [InlineData(100000.0, "T+27:46:40")]
        [InlineData(-5.2, "T-0:00:05")]
        public void FormatElapsed_IsHoursMinutesSeconds(double seconds, string want)
        {
            Assert.Equal(want, StructureTimeFormatter.FormatElapsed(seconds));
        }

        // catches: a culture-formatted elapsed cell (the xUnit host runs under the OS
        // culture; the cell must not change with it).
        [Fact]
        public void FormatElapsed_IsCultureInvariant()
        {
            CultureInfo saved = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                Assert.Equal("T+1:02:05", StructureTimeFormatter.FormatElapsed(3725.9));
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
                Assert.Equal("T+1:02:05", StructureTimeFormatter.FormatElapsed(3725.9));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = saved;
            }
        }

        [Fact]
        public void FormatStepTimes_FirstTimedRowIsTheDate_RouteOriginIsMissing()
        {
            var steps = new List<StructureStep>
            {
                new StructureStep { UT = double.NaN },
                new StructureStep { UT = 1000 },
                new StructureStep { UT = 1000.4 },
                new StructureStep { UT = 1061 },
            };
            string[] cells = StructureTimeFormatter.FormatStepTimes(
                steps, ut => "D" + ut.ToString("F0", CultureInfo.InvariantCulture));
            Assert.Equal(new[] { "-", "D1000", "T+0:00:00", "T+0:01:01" }, cells);
        }

        [Fact]
        public void FormatPieces_Singular_Plural()
        {
            Assert.Equal("1 piece", MissionStructureListBuilder.FormatPieces(1));
            Assert.Equal("2 pieces", MissionStructureListBuilder.FormatPieces(2));
        }

        [Fact]
        public void FormatTitleGroups_CountsAndOrdersByTitle()
        {
            List<string> groups = MissionStructureListBuilder.FormatTitleGroups(
                new[] { "TT-38K Radial Decoupler", "Aero Fin", "TT-38K Radial Decoupler" });
            Assert.Equal(new[] { "Aero Fin", "TT-38K Radial Decoupler x2" }, groups);
        }

        // catches: a long piece list clipping silently in the Event cell.
        [Fact]
        public void ShortenList_FitsTheCellBudget()
        {
            var entries = new List<string> { "TT-38K Radial Decoupler x2", "TT18-A Launch Stability Enhancer x3", "Aero Fin" };
            string label = MissionStructureListBuilder.ShortenList("Staged: 7 pieces", entries);
            Assert.True(label.Length <= MissionStructureListBuilder.EventCellCharBudget, label);
            Assert.Equal("Staged: 7 pieces (TT-38K Radial Decoupler x2, ...)", label);
            Assert.Equal("Staged: 2 pieces (A x2)",
                MissionStructureListBuilder.ShortenList("Staged: 2 pieces", new List<string> { "A x2" }));
        }

        [Fact]
        public void LongPieceList_GoesWholeIntoTheTooltip()
        {
            var r = new Recording
            {
                RecordingId = "r", VesselName = "Lifter", ExplicitStartUT = 0, ExplicitEndUT = 300,
                StartBodyName = "Kerbin", TerminalStateValue = TerminalState.Orbiting,
            };
            string[] names = { "a", "b", "c", "d" };
            for (int i = 0; i < names.Length; i++)
                r.PartEvents.Add(new PartEvent { ut = 50.0, eventType = PartEventType.Decoupled, partPersistentId = (uint)(i + 1), partName = names[i] });
            var tree = new RecordingTree { Id = "t", RootRecordingId = "r" };
            tree.Recordings["r"] = r;
            tree.BranchPoints.Add(new BranchPoint { Id = "b", Type = BranchPointType.JointBreak, UT = 50.0, SplitCause = "DECOUPLE", DebrisCount = 4, CoalesceWindow = 0.5, ParentRecordingIds = { "r" } });

            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), n => "Long Stock Part Title " + n.ToUpperInvariant());

            StructureStep staged = steps.Single(s => s.Kind == StructureStepKind.Staging);
            Assert.True(staged.Label.Length <= MissionStructureListBuilder.EventCellCharBudget, staged.Label);
            Assert.Equal("Staged: 4 pieces (Long Stock Part Title A, Long Stock Part Title B, "
                + "Long Stock Part Title C, Long Stock Part Title D)", staged.Tooltip);
        }

        [Fact]
        public void UnknownTitle_FallsBackToTheInternalName()
        {
            var r = new Recording
            {
                RecordingId = "r", VesselName = "V", ExplicitStartUT = 0, ExplicitEndUT = 300,
                StartBodyName = "Kerbin", TerminalStateValue = TerminalState.Landed,
            };
            r.PartEvents.Add(new PartEvent { ut = 50.0, eventType = PartEventType.Decoupled, partPersistentId = 1, partName = "modPart.v2" });
            var tree = new RecordingTree { Id = "t", RootRecordingId = "r" };
            tree.Recordings["r"] = r;
            List<StructureStep> steps = MissionStructureListBuilder.Build(
                tree, MissionStructureBuilder.Build(tree), n => null);
            Assert.Contains(steps, s => s.Label == "Staged: 1 piece (modPart.v2)");
        }

        [Fact]
        public void EndLabel_IsTheMissionsEndWord()
        {
            Assert.Equal("End: Orbiting", MissionStructureListBuilder.FormatEndLabel(TerminalState.Orbiting));
            Assert.Equal("End: Suborbital", MissionStructureListBuilder.FormatEndLabel(TerminalState.SubOrbital));
            Assert.Equal("End", MissionStructureListBuilder.FormatEndLabel(null));
        }

        // catches: an End row for a leg that ended Docked / Boarded, repeating the branch row.
        [Fact]
        public void DockedAndBoardedLegs_DrawNoEndRow()
        {
            var tug = new Recording
            {
                RecordingId = "tug", VesselName = "Tug", ExplicitStartUT = 0, ExplicitEndUT = 100,
                StartBodyName = "Kerbin", TerminalStateValue = TerminalState.Docked,
            };
            var tree = new RecordingTree { Id = "t", RootRecordingId = "tug" };
            tree.Recordings["tug"] = tug;
            tree.BranchPoints.Add(new BranchPoint { Id = "d", Type = BranchPointType.Dock, UT = 100, ParentRecordingIds = { "tug" }, ChildRecordingIds = { "ext" } });
            List<StructureStep> steps = MissionStructureListBuilder.Build(tree, MissionStructureBuilder.Build(tree));
            Assert.DoesNotContain(steps, s => s.Kind == StructureStepKind.Terminal);
            Assert.Contains(steps, s => s.Kind == StructureStepKind.Dock && s.Label == "Docked");
        }

        // catches: a same-tree two-parent dock naming nobody, or naming the viewer itself.
        [Fact]
        public void TwoParentDock_NamesTheOtherParent()
        {
            var a = new Recording { RecordingId = "a", VesselName = "Tug", ExplicitStartUT = 0, ExplicitEndUT = 100, StartBodyName = "Kerbin" };
            var b = new Recording { RecordingId = "b", VesselName = "Station", ExplicitStartUT = 0, ExplicitEndUT = 100, StartBodyName = "Kerbin" };
            var m = new Recording { RecordingId = "m", VesselName = "Station", ExplicitStartUT = 100, ExplicitEndUT = 200, StartBodyName = "Kerbin", StartSituation = "Orbiting", TerminalStateValue = TerminalState.Orbiting };
            var tree = new RecordingTree { Id = "t", RootRecordingId = "a" };
            tree.Recordings["a"] = a;
            tree.Recordings["b"] = b;
            tree.Recordings["m"] = m;
            tree.BranchPoints.Add(new BranchPoint { Id = "d", Type = BranchPointType.Dock, UT = 100, ParentRecordingIds = { "a", "b" }, ChildRecordingIds = { "m" } });
            List<StructureStep> steps = MissionStructureListBuilder.Build(tree, MissionStructureBuilder.Build(tree));
            StructureStep dock = steps.Single(s => s.Kind == StructureStepKind.Dock);
            Assert.Equal("Docked (Station)", dock.Label);
            Assert.Equal("Tug", dock.VesselName);
            Assert.Equal("Kerbin orbit", dock.Location);
        }

        [Fact]
        public void PartnerWithMission_IsTheMissionsSpelling()
        {
            Assert.Equal("CD (mission 'CD Freighter')",
                MissionChapters.FormatPartnerWithMission("CD", "CD Freighter"));
            Assert.Equal("CD", MissionChapters.FormatPartnerWithMission("CD", null));
            Assert.Null(MissionChapters.FormatPartnerWithMission(null, "M"));
        }

        // catches: GUI-3's Kerbal X #4 Log showing "End: Suborbital | Kerbin, Grasslands" at
        // T+0:04:33 - the launch chain head c549ef6e carries terminal SubOrbital while its
        // chain continues on 04177024; the vessel's only ending is the chain tip's.
        [Fact]
        public void InterbodyMunMission_ChainHeadSubOrbital_IsNoEndRow()
        {
            List<RecordingTree> trees = StructureListBdockFixtureTests.LoadFixtureTrees("interbody-route-recorded", 4);
            RecordingTree tree = trees.Single(t => t.Id == "02382fcdcb1a465388529350c0879cd6");
            Recording head = tree.Recordings.Values.Single(r => r.RecordingId.StartsWith("c549ef6e", StringComparison.Ordinal));
            Assert.Equal(TerminalState.SubOrbital, head.TerminalStateValue);
            List<StructureStep> steps = MissionStructureListBuilder.Build(tree, MissionStructureBuilder.Build(tree));
            Assert.DoesNotContain(steps, s => s.Label == "End: Suborbital");
            Assert.DoesNotContain(steps, s => s.Kind == StructureStepKind.Terminal && s.RecordingId == head.RecordingId);
        }

        [Fact]
        public void EmptyMission_HasTheOwnerWording()
        {
            Assert.Equal("This mission has no recorded flight.",
                StructureListWindowUI.EmptyText(StructureListWindowUI.TargetMode.Mission));
            Assert.Equal("Nothing to show.",
                StructureListWindowUI.EmptyText(StructureListWindowUI.TargetMode.None));
            Assert.Empty(MissionStructureListBuilder.Build(new RecordingTree { Id = "e" },
                MissionStructureBuilder.Build(new RecordingTree { Id = "e" })));
        }

        [Fact]
        public void TerminalLocation_OrbitOnlyForAnOrbitalEnding()
        {
            var rec = new Recording { StartBodyName = "Mun", EndBiome = "Midlands", TerminalOrbitBody = "Mun" };
            Assert.Equal("Mun orbit", MissionStructureListBuilder.TerminalLocation(rec, TerminalState.Orbiting));
            Assert.Equal("Mun, Midlands", MissionStructureListBuilder.TerminalLocation(rec, TerminalState.Landed));
            Assert.Equal(StructureLocationFormatter.Missing,
                MissionStructureListBuilder.TerminalLocation(null, TerminalState.Landed));
        }
    }
}
