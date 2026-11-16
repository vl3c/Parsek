using System;
using System.Collections.Generic;
using System.Text;
using Parsek.Tests.Generators;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The tool-use fact on a recording (design 7.12, addition A3; plan R3E-07): the codec,
    /// the single-player byte-identity promise, the newer-build hold, and the carry through
    /// clone, optimizer merge, optimizer split, tree commit and the fixture generators.
    /// </summary>
    [Collection("Sequential")]
    public class ToolUseFactTests : IDisposable
    {
        private readonly List<string> logLines = new List<string>();

        public ToolUseFactTests()
        {
            ParsekLog.ResetTestOverrides();
            ParsekLog.TestSinkForTesting = line => logLines.Add(line);
            ParsekLog.VerboseOverrideForTesting = true;
            RecordingStore.SuppressLogging = false;
            ParsekScenario.ResetInstanceForTesting();
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
            RecordingStore.ResetForTesting();
            MilestoneStore.ResetForTesting();
            ParsekScenario.ResetInstanceForTesting();
        }

        #region Fixtures

        private static Recording BuildRichRecording()
        {
            return new Recording
            {
                RecordingId = "rich-rec",
                VesselName = "Rich Ship",
                TreeId = "tree-1",
                TreeOrder = 2,
                VesselPersistentId = 4242u,
                RecordedVesselGuid = "0123456789abcdef0123456789abcdef",
                ExplicitStartUT = 100.25,
                ExplicitEndUT = 900.5,
                TerminalStateValue = TerminalState.Orbiting,
                ParentBranchPointId = "bp-parent",
                ChildBranchPointId = "bp-child",
                TerminalOrbitBody = "Kerbin",
                TerminalOrbitInclination = 1.5,
                TerminalOrbitEccentricity = 0.01,
                TerminalOrbitSemiMajorAxis = 700000.0,
                TerminalOrbitLAN = 12.0,
                TerminalOrbitArgumentOfPeriapsis = 34.0,
                TerminalOrbitMeanAnomalyAtEpoch = 0.5,
                TerminalOrbitEpoch = 880.0,
                LoopPlayback = true,
                LoopIntervalSeconds = 120.0,
                ParentRecordingId = "parent-rec",
                ChainId = "chain-1",
                ChainIndex = 1,
                SegmentPhase = "exo",
                SegmentBodyName = "Kerbin",
                StartBodyName = "Kerbin",
                StartBiome = "Shores",
                StartSituation = "Prelaunch",
                EndBiome = "Space",
                LaunchSiteName = "LaunchPad",
                PreLaunchFunds = 1000.0,
                MaxDistanceFromLaunch = 12345.5,
                Generation = 1,
                IsDebris = false,
                ParentAnchorRecordingId = "anchor-rec",
                DockTargetVesselPid = 77u,
                MergeState = MergeState.CommittedProvisional,
                CreatingSessionId = "sess-1",
                RecordingGroups = new List<string> { "Group A" },
                Controllers = new List<ControllerInfo>
                {
                    new ControllerInfo { type = "ModuleCommand", partName = "mk1pod.v2", partPersistentId = 100000u }
                },
            };
        }

        // The node SaveRecordingInto wrote for BuildRichRecording() on the multiplayer branch
        // BEFORE the tool-use fact existed (captured from that build, 2026-11-16). A recording
        // with no tool uses must keep writing exactly this.
        private const string RichRecordingNodeBeforeToolUse =
            "RECORDING\n" +
            "  recordingId = rich-rec\n" +
            "  vesselName = Rich Ship\n" +
            "  treeId = tree-1\n" +
            "  treeOrder = 2\n" +
            "  vesselPersistentId = 4242\n" +
            "  recordedVesselGuid = 0123456789abcdef0123456789abcdef\n" +
            "  explicitStartUT = 100.25\n" +
            "  explicitEndUT = 900.5\n" +
            "  terminalState = 0\n" +
            "  parentBranchPointId = bp-parent\n" +
            "  childBranchPointId = bp-child\n" +
            "  tOrbInc = 1.5\n" +
            "  tOrbEcc = 0.01\n" +
            "  tOrbSma = 700000\n" +
            "  tOrbLan = 12\n" +
            "  tOrbArgPe = 34\n" +
            "  tOrbMna = 0.5\n" +
            "  tOrbEpoch = 880\n" +
            "  tOrbBody = Kerbin\n" +
            "  recordingFormatVersion = 1\n" +
            "  recordingSchemaGeneration = 4\n" +
            "  loopPlayback = True\n" +
            "  loopIntervalSeconds = 120\n" +
            "  parentRecordingId = parent-rec\n" +
            "  chainId = chain-1\n" +
            "  chainIndex = 1\n" +
            "  segmentPhase = exo\n" +
            "  segmentBodyName = Kerbin\n" +
            "  startBodyName = Kerbin\n" +
            "  startBiome = Shores\n" +
            "  startSituation = Prelaunch\n" +
            "  endBiome = Space\n" +
            "  launchSiteName = LaunchPad\n" +
            "  preLaunchFunds = 1000\n" +
            "  lastResIdx = -1\n" +
            "  pointCount = 0\n" +
            "  recordingGroup = Group A\n" +
            "  parentAnchorRecordingId = anchor-rec\n" +
            "  maxDist = 12345.5\n" +
            "  generation = 1\n" +
            "  dockTargetPid = 77\n" +
            "  mergeState = CommittedProvisional\n" +
            "  creatingSessionId = sess-1\n" +
            "  CONTROLLER\n" +
            "    type = ModuleCommand\n" +
            "    part = mk1pod.v2\n" +
            "    pid = 100000\n";

        private static string Dump(ConfigNode node)
        {
            var sb = new StringBuilder();
            DumpInto(sb, node, 0);
            return sb.ToString();
        }

        private static void DumpInto(StringBuilder sb, ConfigNode node, int depth)
        {
            string indent = new string(' ', depth * 2);
            sb.Append(indent).Append(node.name).Append('\n');
            for (int i = 0; i < node.values.Count; i++)
                sb.Append(indent).Append("  ").Append(node.values[i].name).Append(" = ")
                    .Append(node.values[i].value).Append('\n');
            for (int i = 0; i < node.nodes.Count; i++)
                DumpInto(sb, node.nodes[i], depth + 1);
        }

        private static ToolUse Use(ToolKind kind, string detail, double ut)
        {
            return new ToolUse { Kind = kind, Detail = detail, FirstUT = ut };
        }

        private static ConfigNode Save(Recording rec)
        {
            var node = new ConfigNode("RECORDING");
            RecordingTree.SaveRecordingInto(node, rec);
            return node;
        }

        private static Recording Load(ConfigNode node)
        {
            var rec = new Recording();
            RecordingTreeRecordCodec.LoadRecordingFrom(node, rec);
            return rec;
        }

        private static TrajectoryPoint PointAt(double ut)
        {
            return new TrajectoryPoint
            {
                ut = ut,
                altitude = 50000.0,
                bodyName = "Kerbin",
                rotation = Quaternion.identity,
                velocity = Vector3.zero,
            };
        }

        private static TrackSection Section(SegmentEnvironment env, double startUT, double endUT)
        {
            return new TrackSection
            {
                environment = env,
                referenceFrame = ReferenceFrame.Absolute,
                startUT = startUT,
                endUT = endUT,
                sampleRateHz = 1f,
                minAltitude = float.NaN,
                maxAltitude = float.NaN,
                frames = new List<TrajectoryPoint> { PointAt(startUT), PointAt(endUT) },
            };
        }

        // Two sections joined at midUT, so SplitAtUT cuts at an existing boundary.
        private static Recording MakeTwoSectionRecording(double startUT, double midUT, double endUT)
        {
            var rec = new Recording { RecordingId = "split-rec" };
            rec.Points.Add(PointAt(startUT));
            rec.Points.Add(PointAt(midUT));
            rec.Points.Add(PointAt(endUT));
            rec.TrackSections.Add(Section(SegmentEnvironment.Atmospheric, startUT, midUT));
            rec.TrackSections.Add(Section(SegmentEnvironment.ExoBallistic, midUT, endUT));
            return rec;
        }

        #endregion

        #region Single-player promise

        [Fact]
        public void NoToolUses_NodeIsByteIdenticalToTheOutputBeforeTheFact()
        {
            var rec = BuildRichRecording();
            Assert.Null(rec.ToolUses);
            Assert.Equal(RichRecordingNodeBeforeToolUse, Dump(Save(rec)));
        }

        [Fact]
        public void EmptyToolUseList_NodeIsByteIdenticalToTheOutputBeforeTheFact()
        {
            var rec = BuildRichRecording();
            rec.ToolUses = new List<ToolUse>();
            Assert.Equal(RichRecordingNodeBeforeToolUse, Dump(Save(rec)));
        }

        [Fact]
        public void NoToolUses_LoadsAsNullAndResavesByteIdentical()
        {
            Recording loaded = Load(Save(BuildRichRecording()));
            Assert.Null(loaded.ToolUses);
            Assert.Equal(RichRecordingNodeBeforeToolUse, Dump(Save(loaded)));
        }

        #endregion

        #region Codec

        [Fact]
        public void Codec_WritesOneChildPerUse_KindAsNumber_DetailOnlyWhenSet()
        {
            var rec = BuildRichRecording();
            rec.ToolUses = new List<ToolUse>
            {
                Use(ToolKind.GravityHack, "factor=2", 150.5),
                Use(ToolKind.TestTooling, null, 100.25),
            };

            ConfigNode node = Save(rec);
            ConfigNode[] children = node.GetNodes(ToolUseFacts.NodeName);

            Assert.Equal(2, children.Length);
            Assert.Equal("1", children[0].GetValue("kind"));
            Assert.Equal("factor=2", children[0].GetValue("detail"));
            Assert.Equal("150.5", children[0].GetValue("ut"));
            Assert.Equal("6", children[1].GetValue("kind"));
            Assert.Null(children[1].GetValue("detail"));
            Assert.Equal("100.25", children[1].GetValue("ut"));
            // Everything else is untouched: the node minus its TOOL_USE children is the
            // pre-fact node.
            node.RemoveNodes(ToolUseFacts.NodeName);
            Assert.Equal(RichRecordingNodeBeforeToolUse, Dump(node));
        }

        [Fact]
        public void Codec_RoundTrip_KeepsEveryUse()
        {
            var rec = BuildRichRecording();
            rec.ToolUses = new List<ToolUse>
            {
                Use(ToolKind.CheatTeleport, "Mun", 300.0),
                Use(ToolKind.CheatCurrency, "funds", 0.1 + 0.2),
                Use(ToolKind.DebugAction, null, 450.0),
            };

            Recording loaded = Load(Save(rec));

            Assert.Equal(rec.ToolUses, loaded.ToolUses);
            Assert.Contains(logLines, l => l.Contains("[RecordingTree]")
                && l.Contains("ToolUse loaded: rec=rich-rec uses=3 unknownKindsKept=0 malformedSkipped=0"));
        }

        [Fact]
        public void Codec_UnknownKindFromANewerBuild_IsKeptAndWrittenBack()
        {
            var node = Save(BuildRichRecording());
            ConfigNode newer = node.AddNode(ToolUseFacts.NodeName);
            newer.AddValue("kind", "99");
            newer.AddValue("detail", "from-the-future");
            newer.AddValue("ut", "512.75");

            Recording loaded = Load(node);

            Assert.Single(loaded.ToolUses);
            Assert.Equal(99, (int)loaded.ToolUses[0].Kind);
            Assert.False(Enum.IsDefined(typeof(ToolKind), loaded.ToolUses[0].Kind));
            Assert.Contains(logLines, l => l.Contains("[RecordingTree]")
                && l.Contains("unknownKindsKept=1"));

            ConfigNode resaved = Save(loaded);
            ConfigNode[] children = resaved.GetNodes(ToolUseFacts.NodeName);
            Assert.Single(children);
            Assert.Equal("99", children[0].GetValue("kind"));
            Assert.Equal("from-the-future", children[0].GetValue("detail"));
            Assert.Equal("512.75", children[0].GetValue("ut"));
        }

        [Fact]
        public void Codec_MalformedChildren_AreSkippedWithALine()
        {
            var node = Save(BuildRichRecording());
            ConfigNode a = node.AddNode(ToolUseFacts.NodeName);
            a.AddValue("kind", "GravityHack");          // a name, not a number
            a.AddValue("ut", "1");
            ConfigNode b = node.AddNode(ToolUseFacts.NodeName);
            b.AddValue("kind", "0");                    // not a positive kind
            b.AddValue("ut", "1");
            ConfigNode c = node.AddNode(ToolUseFacts.NodeName);
            c.AddValue("kind", "1");                    // no ut
            ConfigNode d = node.AddNode(ToolUseFacts.NodeName);
            d.AddValue("kind", "2");
            d.AddValue("ut", "NaN");
            ConfigNode ok = node.AddNode(ToolUseFacts.NodeName);
            ok.AddValue("kind", "3");
            ok.AddValue("ut", "7");

            Recording loaded = Load(node);

            Assert.Single(loaded.ToolUses);
            Assert.Equal(ToolKind.CheatCurrency, loaded.ToolUses[0].Kind);
            Assert.Contains(logLines, l => l.Contains("[RecordingTree]")
                && l.Contains("uses=1") && l.Contains("malformedSkipped=4"));
        }

        [Fact]
        public void Codec_OnlyMalformedChildren_LoadsAsNull()
        {
            var node = Save(BuildRichRecording());
            node.AddNode(ToolUseFacts.NodeName).AddValue("kind", "x");
            Assert.Null(Load(node).ToolUses);
        }

        [Fact]
        public void Codec_NodeWithoutChildren_ClearsAStaleList()
        {
            ConfigNode node = Save(BuildRichRecording());
            var target = new Recording { ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, null, 1) } };
            RecordingTreeRecordCodec.LoadRecordingFrom(node, target);
            Assert.Null(target.ToolUses);
        }

        #endregion

        #region Clone

        [Fact]
        public void DeepClone_CopiesUses_IntoAnIndependentList()
        {
            var rec = BuildRichRecording();
            rec.ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, "factor=2", 120) };

            Recording clone = Recording.DeepClone(rec);
            rec.ToolUses.Add(Use(ToolKind.CheatTeleport, null, 130));

            Assert.Single(clone.ToolUses);
            Assert.Equal(Use(ToolKind.GravityHack, "factor=2", 120), clone.ToolUses[0]);
            Assert.NotSame(rec.ToolUses, clone.ToolUses);
        }

        [Fact]
        public void DeepClone_NoUses_StaysNull()
        {
            Assert.Null(Recording.DeepClone(BuildRichRecording()).ToolUses);
        }

        [Fact]
        public void ApplyPersistenceArtifactsFrom_TakesTheSourceUses()
        {
            var source = new Recording { ToolUses = new List<ToolUse> { Use(ToolKind.CheatOption, "noCrash", 50) } };
            var target = new Recording { ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, null, 1) } };

            target.ApplyPersistenceArtifactsFrom(source);

            Assert.Equal(source.ToolUses, target.ToolUses);
            Assert.NotSame(source.ToolUses, target.ToolUses);
        }

        #endregion

        #region Merge

        [Fact]
        public void Union_KeepsOnePerKindAndDetail_AtTheLeastUT_OrderedByUT()
        {
            var first = new List<ToolUse>
            {
                Use(ToolKind.GravityHack, null, 200),
                Use(ToolKind.CheatTeleport, "Mun", 150),
            };
            var second = new List<ToolUse>
            {
                Use(ToolKind.GravityHack, "", 100),       // "" and null are the same detail
                Use(ToolKind.CheatTeleport, "Minmus", 120), // another detail is another use
                Use(ToolKind.CheatTeleport, "Mun", 400),
            };

            List<ToolUse> union = ToolUseFacts.Union(first, second);

            Assert.Equal(new List<ToolUse>
            {
                Use(ToolKind.GravityHack, "", 100),
                Use(ToolKind.CheatTeleport, "Minmus", 120),
                Use(ToolKind.CheatTeleport, "Mun", 150),
            }, union);
            Assert.Equal(2, first.Count);
            Assert.Equal(3, second.Count);
        }

        [Fact]
        public void Union_BothEmpty_IsNull()
        {
            Assert.Null(ToolUseFacts.Union(null, new List<ToolUse>()));
        }

        [Fact]
        public void MergeInto_UnionsTheAbsorbedUses()
        {
            var target = new Recording { RecordingId = "target" };
            target.Points.Add(PointAt(100));
            target.Points.Add(PointAt(200));
            target.ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, null, 150) };
            var absorbed = new Recording { RecordingId = "absorbed" };
            absorbed.Points.Add(PointAt(200));
            absorbed.Points.Add(PointAt(300));
            absorbed.ToolUses = new List<ToolUse>
            {
                Use(ToolKind.GravityHack, null, 250),
                Use(ToolKind.CheatCurrency, "science", 260),
            };

            RecordingOptimizer.MergeInto(target, absorbed);

            Assert.Equal(new List<ToolUse>
            {
                Use(ToolKind.GravityHack, null, 150),
                Use(ToolKind.CheatCurrency, "science", 260),
            }, target.ToolUses);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("MergeInto: tool uses unioned target=target absorbed=absorbed count=2"));
        }

        [Fact]
        public void MergeInto_AbsorbedWithoutUses_LeavesTargetUses()
        {
            var target = new Recording { RecordingId = "target" };
            target.Points.Add(PointAt(100));
            target.ToolUses = new List<ToolUse> { Use(ToolKind.DebugAction, null, 150) };
            var absorbed = new Recording { RecordingId = "absorbed" };
            absorbed.Points.Add(PointAt(200));

            RecordingOptimizer.MergeInto(target, absorbed);

            Assert.Equal(new List<ToolUse> { Use(ToolKind.DebugAction, null, 150) }, target.ToolUses);
        }

        #endregion

        #region Split

        [Fact]
        public void SplitAtUT_GivesEachHalfTheUsesInItsRange()
        {
            Recording rec = MakeTwoSectionRecording(100, 200, 300);
            rec.ToolUses = new List<ToolUse>
            {
                Use(ToolKind.TestTooling, null, 50),     // before the start: first half
                Use(ToolKind.GravityHack, null, 150),
                Use(ToolKind.CheatTeleport, "Mun", 200), // at the cut: second half
                Use(ToolKind.CheatCurrency, "funds", 250),
                Use(ToolKind.DebugAction, null, 350),   // after the end: second half
            };

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 200);

            Assert.NotNull(tip);
            Assert.Equal(new List<ToolUse>
            {
                Use(ToolKind.TestTooling, null, 50),
                Use(ToolKind.GravityHack, null, 150),
            }, rec.ToolUses);
            Assert.Equal(new List<ToolUse>
            {
                Use(ToolKind.CheatTeleport, "Mun", 200),
                Use(ToolKind.CheatCurrency, "funds", 250),
                Use(ToolKind.DebugAction, null, 350),
            }, tip.ToolUses);
            Assert.Contains(logLines, l => l.Contains("[Optimizer]")
                && l.Contains("ToolUse split: rec=split-rec splitUT=200 head=2 tail=3"));
        }

        [Fact]
        public void SplitAtUT_AllUsesBeforeTheCut_SecondHalfHoldsNull()
        {
            Recording rec = MakeTwoSectionRecording(100, 200, 300);
            rec.ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, null, 120) };

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 200);

            Assert.NotNull(tip);
            Assert.Single(rec.ToolUses);
            Assert.Null(tip.ToolUses);
        }

        [Fact]
        public void SplitAtUT_NoUses_BothHalvesNull()
        {
            Recording rec = MakeTwoSectionRecording(100, 200, 300);

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 200);

            Assert.NotNull(tip);
            Assert.Null(rec.ToolUses);
            Assert.Null(tip.ToolUses);
        }

        [Fact]
        public void SplitThenMerge_RestoresTheUses()
        {
            Recording rec = MakeTwoSectionRecording(100, 200, 300);
            var uses = new List<ToolUse>
            {
                Use(ToolKind.GravityHack, null, 150),
                Use(ToolKind.CheatTeleport, "Mun", 250),
            };
            rec.ToolUses = new List<ToolUse>(uses);

            Recording tip = RecordingOptimizer.SplitAtUT(rec, 200);
            tip.RecordingId = "tip";
            RecordingOptimizer.MergeInto(rec, tip);

            Assert.Equal(uses, rec.ToolUses);
        }

        #endregion

        #region Tree commit (SessionMerger) and generators

        [Fact]
        public void CommitTree_KeepsTheUsesThroughTheSessionMerge()
        {
            // SessionMerger.MergeTree builds a new recording field by field, but
            // TreeCommitService copies only its trajectory fields back onto the original,
            // so every other field, the tool uses included, stays on the committed recording.
            RecordingStore.SuppressLogging = true;
            MilestoneStore.ResetForTesting();
            RecordingStore.ResetForTesting();
            RecordingSidecarStore.SkipSidecarCurrencyCheckForTesting = true;
            var tree = new RecordingTree
            {
                Id = "tool-tree",
                TreeName = "Tool Tree",
                RootRecordingId = "root",
                ActiveRecordingId = "root",
            };
            Recording root = MakeTwoSectionRecording(100, 200, 300);
            root.RecordingId = "root";
            root.TreeId = "tool-tree";
            root.VesselName = "Root";
            root.VesselPersistentId = 1000u;
            root.ToolUses = new List<ToolUse> { Use(ToolKind.GravityHack, "factor=4", 140) };
            tree.Recordings["root"] = root;

            TreeCommitService.CommitTree(tree);

            Recording committed = RecordingStore.CommittedTrees[0].Recordings["root"];
            Assert.Equal(new List<ToolUse> { Use(ToolKind.GravityHack, "factor=4", 140) }, committed.ToolUses);
        }

        [Fact]
        public void RecordingBuilder_WithToolUse_WritesTheProductionShape()
        {
            var builder = new RecordingBuilder("Builder Ship")
                .WithRecordingId("builder-rec")
                .WithToolUse(ToolKind.CheatOption, "infiniteFuel", 75.5)
                .WithToolUse(ToolKind.TestTooling, null, 10);

            foreach (ConfigNode node in new[] { builder.BuildV3Metadata(), builder.Build() })
            {
                ConfigNode[] children = node.GetNodes(ToolUseFacts.NodeName);
                Assert.Equal(2, children.Length);
                Assert.Equal("4", children[0].GetValue("kind"));
                Assert.Equal("infiniteFuel", children[0].GetValue("detail"));
                Assert.Equal("75.5", children[0].GetValue("ut"));
                Assert.Equal("6", children[1].GetValue("kind"));
            }

            Recording loaded = Load(builder.BuildV3Metadata());
            Assert.Equal(new List<ToolUse>
            {
                Use(ToolKind.CheatOption, "infiniteFuel", 75.5),
                Use(ToolKind.TestTooling, null, 10),
            }, loaded.ToolUses);
        }

        [Fact]
        public void RecordingBuilder_WithoutToolUse_WritesNoChild()
        {
            var builder = new RecordingBuilder("Plain Ship").WithRecordingId("plain-rec");
            Assert.Empty(builder.BuildV3Metadata().GetNodes(ToolUseFacts.NodeName));
            Assert.Empty(builder.Build().GetNodes(ToolUseFacts.NodeName));
        }

        #endregion
    }
}
