using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure halves of the ghost fairing visuals: the stock ModuleStructuralNode rule
    /// (interstage truss / cap plates) transcribed from KSP 1.12.5, the snapshot attN
    /// parsing it reads, and the XSECTION / nArcs inputs of the stock panel builder.
    /// </summary>
    public class FairingGhostStructureTests
    {
        private static ConfigNode Module(string name, params string[] kv)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", name);
            for (int i = 0; i + 1 < kv.Length; i += 2)
                node.AddValue(kv[i], kv[i + 1]);
            return node;
        }

        // The stock fairingSize1 shape: Cap1..2 on their own node pair, Truss1 on every
        // interstage node, Truss2 on node 02 and up, plus the toggle.
        private static ConfigNode StockLikeConfig()
        {
            var part = new ConfigNode("PART");
            part.AddNode(Module("ModuleProceduralFairing"));
            part.AddNode(Module("ModuleStructuralNode", "rootObject", "Cap1",
                "attachNodeNames", "interstage01a,interstage01b"));
            part.AddNode(Module("ModuleStructuralNode", "rootObject", "Cap2",
                "attachNodeNames", "interstage02a,interstage02b"));
            part.AddNode(Module("ModuleStructuralNode", "rootObject", "Truss1",
                "attachNodeNames", "interstage01a,interstage01b,interstage02a,interstage02b"));
            part.AddNode(Module("ModuleStructuralNode", "rootObject", "Truss2",
                "attachNodeNames", "interstage02a,interstage02b"));
            part.AddNode(Module("ModuleStructuralNodeToggle", "showNodes", "false"));
            return part;
        }

        private static ConfigNode Snapshot(bool showMesh, params string[] attN)
        {
            var part = new ConfigNode("PART");
            part.AddValue("persistentId", "500");
            for (int i = 0; i < attN.Length; i++)
                part.AddValue("attN", attN[i]);
            part.AddNode(Module("ModuleProceduralFairing", "fsm", "st_flight_idle", "nArcs", "3"));
            for (int i = 0; i < 4; i++)
                part.AddNode(Module("ModuleStructuralNode", "spawnState", "False", "visibilityState", "True"));
            part.AddNode(Module("ModuleStructuralNodeToggle",
                "showMesh", showMesh ? "True" : "False", "showNodes", "True"));
            return part;
        }

        [Fact]
        public void ComputeStructureNodeVisible_MatchesStockCheckDisplay()
        {
            // visibilityState off hides regardless of anything else (reverse included).
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(false, false, false, false, true));
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(false, false, false, true, false));
            // Attachment-driven.
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(true, false, false, false, true));
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(true, false, false, false, false));
            // Manual spawn ignores attachment.
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(true, true, true, false, false));
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(true, true, false, false, true));
            // Reverse inverts only inside the visible branch.
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(true, false, false, true, false));
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(true, false, false, true, true));
        }

        [Fact]
        public void ParseAttachNodeIndices_KeepsRealAttachmentsOnly()
        {
            var part = new ConfigNode("PART");
            part.AddValue("attN", "top, -1");
            part.AddValue("attN", "bottom, 0");
            part.AddValue("attN", "interstage02b, 3");
            part.AddValue("attN", "garbage");
            part.AddValue("attN", "interstage02b, 7"); // first row wins, as stock lists nodes once

            Dictionary<string, int> map = GhostVisualBuilder.ParseAttachNodeIndices(part);

            Assert.Equal(2, map.Count);
            Assert.Equal(0, map["bottom"]);
            Assert.Equal(3, map["interstage02b"]);
            Assert.False(map.ContainsKey("top"));
        }

        [Fact]
        public void ParseFairingStructureNodes_InterstageTwo_ShowsTrussStackUpToIt()
        {
            List<FairingStructureNodeGhostInfo> nodes = GhostVisualBuilder.ParseFairingStructureNodes(
                StockLikeConfig(), Snapshot(true, "bottom, 0", "interstage02b, 2"));

            Assert.Equal(4, nodes.Count);
            Assert.Equal(new[] { "Cap1", "Cap2", "Truss1", "Truss2" },
                new[] { nodes[0].rootObject, nodes[1].rootObject, nodes[2].rootObject, nodes[3].rootObject });
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[0])); // Cap1
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[1]));  // Cap2
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[2]));  // Truss1
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[3]));  // Truss2
        }

        [Fact]
        public void ParseFairingStructureNodes_NoInterstageAttachment_ShowsNothing()
        {
            List<FairingStructureNodeGhostInfo> nodes = GhostVisualBuilder.ParseFairingStructureNodes(
                StockLikeConfig(), Snapshot(true, "bottom, 0", "top, 2", "interstage01a, -1"));

            Assert.Equal(4, nodes.Count);
            foreach (var node in nodes)
                Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(node));
        }

        [Fact]
        public void ParseFairingStructureNodes_TrussToggleOff_HidesEvenWhenAttached()
        {
            List<FairingStructureNodeGhostInfo> nodes = GhostVisualBuilder.ParseFairingStructureNodes(
                StockLikeConfig(), Snapshot(false, "interstage02b, 2"));

            foreach (var node in nodes)
            {
                Assert.False(node.visibilityState);
                Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(node));
            }
        }

        [Fact]
        public void ParseFairingStructureNodes_ConfigDefaultsAndMissingSnapshotModules()
        {
            var config = new ConfigNode("PART");
            config.AddNode(Module("ModuleStructuralNode"));
            config.AddNode(Module("ModuleStructuralNode", "rootObject", "Plate",
                "spawnManually", "True", "reverseVisibility", "True"));

            List<FairingStructureNodeGhostInfo> nodes =
                GhostVisualBuilder.ParseFairingStructureNodes(config, new ConfigNode("PART"));

            Assert.Equal(2, nodes.Count);
            Assert.Equal("Fairing", nodes[0].rootObject);   // stock KSPField default
            Assert.True(nodes[0].visibilityState);
            Assert.False(nodes[0].spawnManually);
            Assert.Empty(nodes[0].attachedPartIndices);
            Assert.True(nodes[1].spawnManually);
            Assert.True(nodes[1].reverseVisibility);
            Assert.False(nodes[1].spawnState);
            // spawnManually + spawnState false -> flag false -> reversed -> shown.
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[1]));
            Assert.Empty(GhostVisualBuilder.ParseFairingStructureNodes(null, null));
        }

        [Fact]
        public void RecordedShownState_OverridesTheRule_AfterTheStageSeparated()
        {
            // The snapshot taken right after separation: the interstage row is empty, but the
            // live truss was still shown (stock keeps it), so the recorder stamped it.
            ConfigNode snap = Snapshot(true, "bottom, 0", "interstage02b, -1");
            Assert.False(GhostFairingStructureStamp.ApplyStamp(new ConfigNode("VESSEL"), 500, 0, true));
            var vessel = new ConfigNode("VESSEL");
            vessel.AddNode(snap);
            Assert.True(GhostFairingStructureStamp.ApplyStamp(vessel, 500, 2, true)); // Truss1
            Assert.True(GhostFairingStructureStamp.ApplyStamp(vessel, 500, 1, true)); // Cap2

            List<FairingStructureNodeGhostInfo> nodes =
                GhostVisualBuilder.ParseFairingStructureNodes(StockLikeConfig(), vessel.GetNode("PART"));
            Assert.Null(nodes[0].recordedShown);
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[0])); // Cap1: rule
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[1]));  // Cap2: stamped
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[2]));  // Truss1: stamped
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[3])); // Truss2: rule

            // The mirror case: a stamped False hides a structure the rule would show.
            ConfigNode attached = Snapshot(true, "interstage02b, 2");
            var vessel2 = new ConfigNode("VESSEL");
            vessel2.AddNode(attached);
            Assert.True(GhostFairingStructureStamp.ApplyStamp(vessel2, 500, 2, false));
            nodes = GhostVisualBuilder.ParseFairingStructureNodes(StockLikeConfig(), vessel2.GetNode("PART"));
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[2]));
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[3]));
        }

        [Fact]
        public void ApplyStamp_MatchesPartByPidAndModuleByOrdinal()
        {
            var vessel = new ConfigNode("VESSEL");
            vessel.AddNode(Snapshot(true));
            Assert.False(GhostFairingStructureStamp.ApplyStamp(vessel, 501, 0, true)); // wrong pid
            Assert.False(GhostFairingStructureStamp.ApplyStamp(vessel, 500, 4, true)); // only 4 nodes
            Assert.False(GhostFairingStructureStamp.ApplyStamp(vessel, 500, -1, true));
            Assert.False(GhostFairingStructureStamp.ApplyStamp(null, 500, 0, true));
            Assert.True(GhostFairingStructureStamp.ApplyStamp(vessel, 500, 3, false));
            ConfigNode[] modules = vessel.GetNode("PART").GetNodes("MODULE");
            int stampedCount = 0;
            for (int i = 0; i < modules.Length; i++)
                if (modules[i].HasValue(GhostFairingStructureStamp.ShownKey)) stampedCount++;
            Assert.Equal(1, stampedCount);
            Assert.Equal("False", modules[4].GetValue(GhostFairingStructureStamp.ShownKey)); // 4th structural node
        }

        [Fact]
        public void ParseFairingSections_SortsByHeightAndSkipsBadRows()
        {
            var module = Module("ModuleProceduralFairing");
            var x1 = new ConfigNode("XSECTION"); x1.AddValue("h", "1.5"); x1.AddValue("r", "0.2");
            var x0 = new ConfigNode("XSECTION"); x0.AddValue("h", "0"); x0.AddValue("r", "0.625");
            var bad = new ConfigNode("XSECTION"); bad.AddValue("h", "x");
            module.AddNode(x1); module.AddNode(bad); module.AddNode(x0);

            var sections = GhostVisualBuilder.ParseFairingSections(module);

            Assert.Equal(2, sections.Count);
            Assert.Equal(0f, sections[0].h);
            Assert.Equal(0.625f, sections[0].r);
            Assert.Equal(1.5f, sections[1].h);
            Assert.Empty(GhostVisualBuilder.ParseFairingSections(null));
        }

        [Fact]
        public void ResolveFairingArcCount_UsesThePlayersPanelCount()
        {
            Assert.Equal(4f, GhostVisualBuilder.ResolveFairingArcCount(
                Module("ModuleProceduralFairing", "nArcs", "4"), 2f));
            Assert.Equal(2f, GhostVisualBuilder.ResolveFairingArcCount(
                Module("ModuleProceduralFairing"), 2f));
            Assert.Equal(2f, GhostVisualBuilder.ResolveFairingArcCount(
                Module("ModuleProceduralFairing", "nArcs", "0"), 2f));
            Assert.Equal(3f, GhostVisualBuilder.ResolveFairingArcCount(null, 3f));
        }

        [Fact]
        public void ResolveFairingBaseShaderName_FollowsStockOnVariantApplied()
        {
            // No shaderName and the explicit default both become the plain bumped shader.
            Assert.Equal("KSP/Bumped Specular", GhostVisualBuilder.ResolveFairingBaseShaderName(null));
            Assert.Equal("KSP/Bumped Specular", GhostVisualBuilder.ResolveFairingBaseShaderName(""));
            Assert.Equal("KSP/Bumped Specular",
                GhostVisualBuilder.ResolveFairingBaseShaderName("KSP/Bumped Specular (Cutoff)"));
            // The Gold / Silver variants name their own shader, used as given.
            Assert.Equal("KSP/Bumped Specular (Mapped)",
                GhostVisualBuilder.ResolveFairingBaseShaderName("KSP/Bumped Specular (Mapped)"));
        }

        [Fact]
        public void IsFairingBaseMaterialName_MatchesByPrefixIgnoringInstanceSuffix()
        {
            Assert.True(GhostVisualBuilder.IsFairingBaseMaterialName("FairingBase", "FairingBase"));
            Assert.True(GhostVisualBuilder.IsFairingBaseMaterialName("FairingBase (Instance)", "FairingBase"));
            Assert.True(GhostVisualBuilder.IsFairingBaseMaterialName("FairingBase_bw", "FairingBase"));
            Assert.False(GhostVisualBuilder.IsFairingBaseMaterialName("FairingsMat", "FairingBase"));
            Assert.False(GhostVisualBuilder.IsFairingBaseMaterialName(null, "FairingBase"));
            Assert.False(GhostVisualBuilder.IsFairingBaseMaterialName("FairingBase", null));
        }
    }
}
