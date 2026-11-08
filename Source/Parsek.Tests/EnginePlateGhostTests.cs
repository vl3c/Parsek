using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// The pure halves of the engine plate ghost: stock ModuleDynamicNodes' one shown mount
    /// pattern, and the one shroud a ModuleJettison over variant meshes drives.
    /// </summary>
    public class EnginePlateGhostTests
    {
        private static ConfigNode PlateConfig()
        {
            var part = new ConfigNode("PART");
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", "ModuleDynamicNodes");
            foreach (string mesh in new[] { "N1", "N2", "N3" })
            {
                ConfigNode set = m.AddNode("NODE_SET");
                set.AddValue("MeshTransform", mesh);
            }
            return part;
        }

        private static ConfigNode PlateSnapshot(string nodeSetIdx)
        {
            var part = new ConfigNode("PART");
            ConfigNode m = part.AddNode("MODULE");
            m.AddValue("name", "ModuleDynamicNodes");
            if (nodeSetIdx != null) m.AddValue("NodeSetIdx", nodeSetIdx);
            return part;
        }

        [Fact]
        public void DynamicNodeMeshes_ShowOnlyTheSelectedSet()
        {
            List<KeyValuePair<string, bool>> states =
                GhostVisualBuilder.ResolveDynamicNodeMeshStates(PlateConfig(), PlateSnapshot("1"));
            Assert.Equal(3, states.Count);
            Assert.Equal(new KeyValuePair<string, bool>("N1", false), states[0]);
            Assert.Equal(new KeyValuePair<string, bool>("N2", true), states[1]);
            Assert.Equal(new KeyValuePair<string, bool>("N3", false), states[2]);
        }

        [Fact]
        public void DynamicNodeMeshes_DefaultIndexIsZero_AndBadIndexLeavesThePrefab()
        {
            List<KeyValuePair<string, bool>> states =
                GhostVisualBuilder.ResolveDynamicNodeMeshStates(PlateConfig(), PlateSnapshot(null));
            Assert.True(states[0].Value);
            Assert.False(states[1].Value);
            Assert.Empty(GhostVisualBuilder.ResolveDynamicNodeMeshStates(PlateConfig(), PlateSnapshot("7")));
            Assert.Empty(GhostVisualBuilder.ResolveDynamicNodeMeshStates(PlateConfig(), PlateSnapshot("-1")));
            Assert.Empty(GhostVisualBuilder.ResolveDynamicNodeMeshStates(new ConfigNode("PART"), PlateSnapshot("0")));
            Assert.Empty(GhostVisualBuilder.ResolveDynamicNodeMeshStates(null, null));
        }

        [Fact]
        public void SelectJettisonNames_UsesStocksActiveShroud()
        {
            const string list = "Shroud2x0, Shroud2x1,Shroud2x2,Shroud2x3,Shroud2x4";
            Assert.Equal(new List<string> { "Shroud2x2" }, GhostVisualBuilder.SelectJettisonNames(list, "Shroud2x2"));
            // No recorded choice, or one not in the list: every listed name.
            Assert.Equal(5, GhostVisualBuilder.SelectJettisonNames(list, null).Count);
            Assert.Equal(5, GhostVisualBuilder.SelectJettisonNames(list, "").Count);
            Assert.Equal(5, GhostVisualBuilder.SelectJettisonNames(list, "Other").Count);
            Assert.Equal("Shroud2x1", GhostVisualBuilder.SelectJettisonNames(list, null)[1]);
            // Single-name engines (fairingL,fairingR pairs too) are unchanged.
            Assert.Equal(new List<string> { "fairingL", "fairingR" },
                GhostVisualBuilder.SelectJettisonNames("fairingL,fairingR", ""));
            Assert.Empty(GhostVisualBuilder.SelectJettisonNames(null, "x"));
        }
    }
}
