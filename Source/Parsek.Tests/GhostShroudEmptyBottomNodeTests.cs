using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// todo GHOST-SHROUD-EMPTY-BOTTOM-NODE: stock ModuleJettison hides the shroud of a part
    /// whose bottom node holds nothing (ignoreNodes off) without setting isJettisoned, so the
    /// ghost must read that rule from the snapshot instead of waiting for an event that never
    /// comes. These pin the pure decision in both directions.
    /// </summary>
    public class GhostShroudEmptyBottomNodeTests
    {
        private static ConfigNode Part(params string[] attN)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "liquidEngine2");
            foreach (string row in attN)
                part.AddValue("attN", row);
            return part;
        }

        private static ConfigNode Module(string isJettisoned = null, string hideOverride = null)
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleJettison");
            if (isJettisoned != null) module.AddValue("isJettisoned", isJettisoned);
            if (hideOverride != null) module.AddValue("shroudHideOverride", hideOverride);
            return module;
        }

        private static GhostVisualBuilder.JettisonSpawnHideReason Resolve(
            ConfigNode part, ConfigNode module, bool ignoreNodes = false, string bottom = "bottom")
            => GhostVisualBuilder.ResolveJettisonSpawnHideReason(ignoreNodes, bottom, part, module);

        [Fact]
        public void LoneEngine_EmptyBottomNode_StartsHidden()
        {
            // The parity spike's one-part LV-T45: both nodes empty, shroud intact.
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.BottomNodeEmpty,
                Resolve(Part("top, -1", "bottom, -1"), Module(isJettisoned: "False")));
        }

        [Fact]
        public void EngineWithPartBelow_StaysShown()
        {
            // Mirror direction (FGV-1 config 6): a tank / decoupler on the bottom node keeps the shroud.
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(Part("top, 0", "bottom, 2"), Module(isJettisoned: "False")));
        }

        [Fact]
        public void EmptyBottomNode_WithIgnoreNodes_StaysShown()
        {
            // Service modules set ignoreNodes: stock skips the node check.
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(Part("top, 0", "bottom, -1"), Module(isJettisoned: "False"), ignoreNodes: true));
        }

        [Fact]
        public void JettisonedShroud_HidesWhateverTheNode()
        {
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.Jettisoned,
                Resolve(Part("bottom, 2"), Module(isJettisoned: "True")));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.Jettisoned,
                Resolve(Part("bottom, -1"), Module(isJettisoned: "True"), ignoreNodes: true));
        }

        [Fact]
        public void HideOverride_Hides_EvenWithPartBelow()
        {
            // Stock OnStart folds shroudHideOverride into isJettisoned.
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.HideOverride,
                Resolve(Part("bottom, 2"), Module(isJettisoned: "False", hideOverride: "True")));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(Part("bottom, 2"), Module(isJettisoned: "False", hideOverride: "False")));
        }

        [Fact]
        public void CustomBottomNodeName_IsTheNodeRead()
        {
            ConfigNode part = Part("bottom, 3", "top, -1");
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.BottomNodeEmpty,
                Resolve(part, Module(), bottom: "top"));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(part, Module(), bottom: "bottom"));
            // A node the part does not list is no evidence.
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(part, Module(), bottom: "bottom02"));
        }

        [Fact]
        public void SurfaceAttachedChild_DoesNotFillTheBottomNode()
        {
            // Stock reads only FindAttachNode(bottomNodeName).attachedPart.
            ConfigNode part = Part("top, 0", "bottom, -1");
            part.AddValue("srfN", "srfAttach, -1");
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.BottomNodeEmpty,
                Resolve(part, Module()));
        }

        [Fact]
        public void NoEvidence_StaysShown()
        {
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None, Resolve(Part(), Module()));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None, Resolve(null, null));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None, Resolve(Part("bottom, 2"), null));
            Assert.Equal(GhostVisualBuilder.JettisonSpawnHideReason.None,
                Resolve(Part("bottom, -1"), Module(), bottom: null));
        }

        [Theory]
        [InlineData("bottom, -1", true)]
        [InlineData("bottom,-1", true)]
        [InlineData("  bottom ,  -1 ", true)]
        [InlineData("bottom, 0", false)]
        [InlineData("bottom, 12", false)]
        [InlineData("bottom, fuelTank_123", false)]
        [InlineData("bottom", false)]
        [InlineData("bottomX, -1", false)]
        [InlineData("Bottom, -1", false)]
        public void IsAttachNodeEmptyInSnapshot_Rows(string row, bool expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(Part(row), "bottom"));
        }

        [Fact]
        public void IsAttachNodeEmptyInSnapshot_FirstMatchingRowDecides()
        {
            Assert.False(GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(
                Part("top, -1", "bottom, 4"), "bottom"));
            Assert.True(GhostVisualBuilder.IsAttachNodeEmptyInSnapshot(
                Part("top, -1", "bottom, 4"), "top"));
        }
    }
}
