using System.Collections.Generic;
using Parsek;
using Xunit;

namespace Parsek.Tests
{
    // Tests for the "vessel composition over time" read model (MissionCompositionBuilder):
    // composition labels, env-split grouping, branching at splits/EVA, and terminal leaves.
    public class MissionCompositionTests
    {
        // Builds a controlled leg with an explicit composition (controllers by type + crew).
        private static Recording Leg(
            string id, string chainId, int chainIndex, double start, double end,
            int pods = 0, int probes = 0, int seats = 0, int crew = 0,
            string eva = null, string parentAnchor = null, string vessel = "Kerbal X")
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
            };
            var controllers = new List<ControllerInfo>();
            for (int i = 0; i < pods; i++) controllers.Add(new ControllerInfo { type = "CrewedPod" });
            for (int i = 0; i < probes; i++) controllers.Add(new ControllerInfo { type = "ProbeCore" });
            for (int i = 0; i < seats; i++) controllers.Add(new ControllerInfo { type = "ExternalSeat" });
            if (!string.IsNullOrEmpty(eva)) controllers.Add(new ControllerInfo { type = "KerbalEVA" });
            if (controllers.Count > 0) rec.Controllers = controllers;
            if (crew > 0) rec.StartCrew = new Dictionary<string, int> { { "Pilot", crew } };
            return rec;
        }

        private static BranchPoint BP(string id, BranchPointType type, string[] parents, string[] children)
            => new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
            };

        private static RecordingTree Tree(Recording[] recs, BranchPoint[] bps = null)
        {
            var tree = new RecordingTree { Id = "t", RootRecordingId = recs[0].RecordingId };
            foreach (var r in recs) tree.Recordings[r.RecordingId] = r;
            if (bps != null) tree.BranchPoints.AddRange(bps);
            return tree;
        }

        private static List<MissionCompositionNode> Build(Recording[] recs, BranchPoint[] bps = null)
            => MissionCompositionBuilder.Build(MissionStructureBuilder.Build(Tree(recs, bps)));

        [Fact]
        public void FormatComposition_OmitsZeros_UsesXn()
        {
            var leg = new MissionLeg { PodCount = 1, ProbeCount = 1, CrewCount = 3 };
            Assert.Equal("pod x1, probe x1, crew x3", MissionCompositionBuilder.FormatComposition(leg));

            var probe = new MissionLeg { ProbeCount = 1 };
            Assert.Equal("probe x1", MissionCompositionBuilder.FormatComposition(probe));

            var evaLeg = new MissionLeg { EvaCrewName = "Bob Kerman" };
            Assert.Equal("Bob Kerman", MissionCompositionBuilder.FormatComposition(evaLeg));
        }

        [Fact]
        public void SingleUncrewedController_IsLeaf()
        {
            var set = Build(new[] { Leg("p", "C", 0, 0, 100, probes: 1) });
            Assert.Single(set);
            Assert.Equal("probe x1", set[0].CompositionLabel);
            Assert.True(set[0].IsLeaf);
            Assert.Empty(set[0].Children);
        }

        [Fact]
        public void CrewedTerminal_IsMultiAtom_ExpandsToAtoms()
        {
            var set = Build(new[] { Leg("pod", "C", 0, 0, 100, pods: 1, crew: 3) });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
            Assert.False(set[0].IsLeaf);
            // One Pod atom + one crew atom.
            Assert.Equal(2, set[0].Children.Count);
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Pod");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "crew x3");
        }

        [Fact]
        public void EnvSplit_SameComposition_CollapsesToOneInterval()
        {
            // Two legs of the same vessel/chain, same composition: one interval, no extra node.
            var set = Build(new[]
            {
                Leg("a1", "C", 0, 0, 50, pods: 1, crew: 3),
                Leg("a2", "C", 1, 50, 120, pods: 1, crew: 3),
            });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
            Assert.Equal(0.0, set[0].StartUT);
            Assert.Equal(120.0, set[0].EndUT); // spans both env-split legs
        }

        [Fact]
        public void Decouple_BranchesIntoContinuingPlusPeeledProbe()
        {
            // Launch stack pod+probe+crew3 -> Undock -> continuing pod+crew3 + peeled probe.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 42, pods: 1, probes: 1, crew: 3),
                    Leg("cont", "C2", 0, 42, 200, pods: 1, crew: 3),          // continuing (not anchored)
                    Leg("probe", "C3", 0, 42, 115, probes: 1, parentAnchor: "L"), // peeled (anchored offshoot)
                },
                new[] { BP("bp1", BranchPointType.Undock, new[] { "L" }, new[] { "cont", "probe" }) });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Launch", root.StartEvent);
            Assert.Equal("Undock", root.EndEvent);
            Assert.Equal(2, root.Children.Count);

            // Continuing vessel first, then the peeled probe.
            Assert.Equal("pod x1, crew x3", root.Children[0].CompositionLabel);
            Assert.Equal("Undock", root.Children[0].StartEvent);
            Assert.Equal("probe x1", root.Children[1].CompositionLabel);
            Assert.True(root.Children[1].IsLeaf);
        }

        [Fact]
        public void Eva_PeelsNamedKerbal_CrewCountDrops()
        {
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 60, pods: 1, crew: 3),
                    Leg("cont", "C2", 0, 60, 200, pods: 1, crew: 2),       // continuing, crew dropped
                    Leg("bob", "C3", 0, 60, 90, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[] { BP("bp1", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }) });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, crew x3", root.CompositionLabel);
            Assert.Equal("EVA", root.EndEvent);
            Assert.Equal(2, root.Children.Count);
            Assert.Equal("pod x1, crew x2", root.Children[0].CompositionLabel);
            Assert.Equal("Bob Kerman", root.Children[1].CompositionLabel);
            Assert.True(root.Children[1].IsLeaf); // an EVA kerbal is a single atom
        }

        [Fact]
        public void EmptyStructure_YieldsNoRoots()
        {
            Assert.Empty(MissionCompositionBuilder.Build(MissionStructureBuilder.Build(new RecordingTree { Id = "t" })));
        }
    }
}
