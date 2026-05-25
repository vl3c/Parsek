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
            string eva = null, string parentAnchor = null, string vessel = "Kerbal X",
            string[] crewNames = null)
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
            // StartCrew (per-trait counts) only from the explicit count; crew NAMES go in
            // CrewEndStates WITHOUT StartCrew, mirroring the real saves (which carry the named
            // roster but no StartCrew node) so the name-count fallback is exercised.
            if (crew > 0) rec.StartCrew = new Dictionary<string, int> { { "Pilot", crew } };
            if (crewNames != null)
            {
                rec.CrewEndStates = new Dictionary<string, KerbalEndState>();
                foreach (var n in crewNames) rec.CrewEndStates[n] = default(KerbalEndState);
                rec.CrewEndStatesResolved = true;
            }
            return rec;
        }

        private static BranchPoint BP(string id, BranchPointType type, string[] parents, string[] children,
            string splitCause = null)
            => new BranchPoint
            {
                Id = id,
                Type = type,
                UT = 0,
                ParentRecordingIds = new List<string>(parents),
                ChildRecordingIds = new List<string>(children),
                SplitCause = splitCause,
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
        public void CrewCount_FallsBackToCrewNames_WhenNoStartCrew()
        {
            // Real saves carry CrewEndStates names but no StartCrew; the count must still show.
            var set = Build(new[] { Leg("pod", "C", 0, 0, 100, pods: 1, crewNames: new[] { "Jeb", "Bill", "Bob" }) });
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel);
        }

        [Fact]
        public void StaleVessel_PeelsAnchoredProbe_SynthesizesContinuingRemainder()
        {
            // The exact reported shape: launch pod+probe+crew3, the probe separates as an ANCHORED
            // controlled offshoot, and the pod keeps the SAME recording (no recaptured continuation
            // leg, so its Controllers stay [pod,probe]). The builder must synthesize the remaining
            // "Kerbal X (pod x1, crew x3)" continuing vessel alongside the peeled probe.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 200, pods: 1, probes: 1, crewNames: new[] { "Jeb", "Bill", "Bob" }),
                    Leg("probe", "C2", 0, 60, 115, probes: 1, parentAnchor: "L", vessel: "Kerbal X Probe"),
                },
                new[] { BP("bp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, splitCause: "DECOUPLE") });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Decoupled", root.EndEvent); // the probe decouple ends the launch composition
            Assert.Equal(2, root.Children.Count);
            Assert.Equal("pod x1, crew x3", root.Children[0].CompositionLabel); // synthesized remainder, first
            Assert.Equal("Kerbal X", root.Children[0].VesselName);
            Assert.Equal("probe x1", root.Children[1].CompositionLabel);         // the peeled probe
            Assert.Equal("Decoupled", root.Children[1].StartEvent);             // DECOUPLE -> "Decoupled"
            Assert.True(root.Children[1].IsLeaf);
        }

        [Fact]
        public void EndEvent_IsFirstCompositionChange_NotRecordingTerminalFork()
        {
            // Mirrors the real Kerbal X: the recording forks at a LATER EVA, but the launch's
            // composition first changes when the probe decouples earlier. The launch node's end
            // event must be the decouple, NOT the EVA the recording happens to fork at.
            var set = Build(
                new[]
                {
                    Leg("L", "C", 0, 0, 122, pods: 1, probes: 1, crewNames: new[] { "Jeb", "Bill", "Bob" }),
                    Leg("probe", "C2", 0, 59, 84, probes: 1, parentAnchor: "L", vessel: "Kerbal X Probe"),
                    Leg("cont", "C3", 0, 122, 200, pods: 1, crewNames: new[] { "Bill", "Jeb" }),
                    Leg("bob", "C4", 0, 122, 150, eva: "Bob Kerman", parentAnchor: "L"),
                },
                new[]
                {
                    BP("dp", BranchPointType.JointBreak, new[] { "L" }, new[] { "probe" }, splitCause: "DECOUPLE"),
                    BP("ev", BranchPointType.EVA, new[] { "L" }, new[] { "cont", "bob" }),
                });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Decoupled", root.EndEvent); // earliest change (probe @59), not the EVA @122
        }

        [Fact]
        public void CrewedTerminal_WithNames_ExpandsToNamedCrewLeaves()
        {
            var set = Build(new[]
            {
                Leg("pod", "C", 0, 0, 100, pods: 1, crewNames: new[] { "Bob Kerman", "Bill Kerman", "Jeb Kerman" }),
            });
            Assert.Single(set);
            Assert.Equal("pod x1, crew x3", set[0].CompositionLabel); // label still uses counts
            Assert.False(set[0].IsLeaf);
            // One Pod atom + one named leaf per crew member (no "crew x3" count atom).
            Assert.Equal(4, set[0].Children.Count);
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Pod");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Bob Kerman");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Bill Kerman");
            Assert.Contains(set[0].Children, c => c.CompositionLabel == "Jeb Kerman");
            Assert.DoesNotContain(set[0].Children, c => c.CompositionLabel == "crew x3");
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
                new[] { BP("bp1", BranchPointType.JointBreak, new[] { "L" }, new[] { "cont", "probe" }, splitCause: "DECOUPLE") });

            Assert.Single(set);
            MissionCompositionNode root = set[0];
            Assert.Equal("pod x1, probe x1, crew x3", root.CompositionLabel);
            Assert.Equal("Launch", root.StartEvent);
            Assert.Equal("Decoupled", root.EndEvent); // DECOUPLE cause -> "Decoupled", not "Broke off"
            Assert.Equal(2, root.Children.Count);

            // Continuing vessel first, then the peeled probe.
            Assert.Equal("pod x1, crew x3", root.Children[0].CompositionLabel);
            Assert.Equal("Decoupled", root.Children[0].StartEvent);
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
