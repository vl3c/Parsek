using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Characterization tests for the ConfigNode-only helpers of <see cref="GhostVisualBuilder"/>
    /// that no test called directly: the fairing internal-structure name extraction and the
    /// deployable clip length fallback.
    /// </summary>
    public class GhostVisualBuilderCharacterizationTests
    {
        private static ConfigNode Module(string name, string rootObject = null)
        {
            var node = new ConfigNode("MODULE");
            node.AddValue("name", name);
            if (rootObject != null)
                node.AddValue("rootObject", rootObject);
            return node;
        }

        [Fact]
        public void GetFairingInternalStructureNames_NullOrNoModules_Empty()
        {
            Assert.Empty(GhostVisualBuilder.GetFairingInternalStructureNames(null));
            Assert.Empty(GhostVisualBuilder.GetFairingInternalStructureNames(new ConfigNode("PART")));
        }

        [Fact]
        public void GetFairingInternalStructureNames_CollectsDistinctStructuralNodeRoots()
        {
            var part = new ConfigNode("PART");
            part.AddNode(Module("ModuleProceduralFairing"));
            part.AddNode(Module("ModuleStructuralNode", "Truss"));
            part.AddNode(Module("ModuleStructuralNode", "Cap"));
            part.AddNode(Module("ModuleStructuralNode", "Truss"));
            // A structural node with no or an empty rootObject contributes nothing.
            part.AddNode(Module("ModuleStructuralNode"));
            part.AddNode(Module("ModuleStructuralNode", ""));
            // rootObject on another module type is ignored, and the name match is exact.
            part.AddNode(Module("ModuleDecouple", "NotAStructure"));
            part.AddNode(Module("modulestructuralnode", "WrongCase"));

            var names = GhostVisualBuilder.GetFairingInternalStructureNames(part);

            Assert.Equal(2, names.Count);
            Assert.Contains("Truss", names);
            Assert.Contains("Cap", names);
        }

        [Fact]
        public void ResolveDeployableClipSeconds_UnsampledPart_ReturnsDefault()
        {
            // Headless runs never sample an Animation, so every key reads the default.
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds(null));
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds(""));
            Assert.Equal(GhostPlaybackLogic.DefaultDeployableClipSeconds,
                GhostVisualBuilder.ResolveDeployableClipSeconds("never-sampled-part-key"));
        }
    }
}
