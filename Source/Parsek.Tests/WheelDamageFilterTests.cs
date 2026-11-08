using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Parsek.Tests
{
    public class WheelDamageFilterTests
    {
        #region GetDamagedWheelTransformNames

        [Fact]
        public void GetDamagedWheelTransformNames_WithDamageModule_ReturnsNames()
        {
            // Build a part config with a ModuleWheelDamage that has damagedTransformName
            var partConfig = new ConfigNode("PART");
            var module = partConfig.AddNode("MODULE");
            module.AddValue("name", "ModuleWheelDamage");
            module.AddValue("damagedTransformName", "bustedwheel");

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Single(result);
            Assert.Contains("bustedwheel", result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_MultipleDamageModules_ReturnsAll()
        {
            // Part with two ModuleWheelDamage modules (theoretical edge case)
            var partConfig = new ConfigNode("PART");

            var mod1 = partConfig.AddNode("MODULE");
            mod1.AddValue("name", "ModuleWheelDamage");
            mod1.AddValue("damagedTransformName", "bustedwheel");

            var mod2 = partConfig.AddNode("MODULE");
            mod2.AddValue("name", "ModuleWheelDamage");
            mod2.AddValue("damagedTransformName", "wheelDamaged");

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Equal(2, result.Count);
            Assert.Contains("bustedwheel", result);
            Assert.Contains("wheelDamaged", result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_NoDamageModule_ReturnsEmpty()
        {
            // Part config with other modules but no ModuleWheelDamage
            var partConfig = new ConfigNode("PART");
            var module = partConfig.AddNode("MODULE");
            module.AddValue("name", "ModuleWheelBase");
            module.AddValue("wheelTransformName", "WheelPivot");

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Empty(result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_DamageModuleWithoutTransformName_ReturnsEmpty()
        {
            // ModuleWheelDamage exists but has no damagedTransformName field
            // (e.g., landing gear GearSmall, GearMedium, GearFixed, GearFree)
            var partConfig = new ConfigNode("PART");
            var module = partConfig.AddNode("MODULE");
            module.AddValue("name", "ModuleWheelDamage");
            module.AddValue("stressTolerance", "5600");
            // No damagedTransformName

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Empty(result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_NullNode_ReturnsEmpty()
        {
            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(null);

            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_NoModuleNodes_ReturnsEmpty()
        {
            // Part config with no MODULE children at all
            var partConfig = new ConfigNode("PART");
            partConfig.AddValue("name", "testPart");

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Empty(result);
        }

        [Fact]
        public void GetDamagedWheelTransformNames_MixedModules_OnlyReturnsDamageNames()
        {
            // Part config with multiple module types, only one being ModuleWheelDamage
            var partConfig = new ConfigNode("PART");

            var mod1 = partConfig.AddNode("MODULE");
            mod1.AddValue("name", "ModuleWheelBase");
            mod1.AddValue("wheelTransformName", "WheelPivot");

            var mod2 = partConfig.AddNode("MODULE");
            mod2.AddValue("name", "ModuleWheelDamage");
            mod2.AddValue("damagedTransformName", "bustedwheel");
            mod2.AddValue("undamagedTransformName", "wheel");

            var mod3 = partConfig.AddNode("MODULE");
            mod3.AddValue("name", "ModuleWheelSuspension");
            mod3.AddValue("suspensionTransformName", "SuspensionPivot");

            var result = GhostVisualBuilder.GetDamagedWheelTransformNames(partConfig);

            Assert.Single(result);
            Assert.Contains("bustedwheel", result);
            // undamagedTransformName should NOT be collected
            Assert.DoesNotContain("wheel", result);
        }

        #endregion

        #region IsRendererOnDamagedTransform

        // A real Transform needs the Unity runtime, so the null-transform guard is the only
        // part of the Transform adapter reachable headlessly; the names guard and the
        // ancestor walk live in the pure IsNodeOrAncestorNamed, exercised below over a
        // fake node chain (TQ-2-wheel-damage-guard-needs-live-transform).
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("bustedwheel")]
        public void IsRendererOnDamagedTransform_NullTransform_ReturnsFalseForAnyNames(string name)
        {
            // null -> no set, "" -> empty set, otherwise a one-name set.
            HashSet<string> damagedNames = name == null ? null
                : name.Length == 0 ? new HashSet<string>()
                : new HashSet<string> { name };
            Assert.False(GhostVisualBuilder.IsRendererOnDamagedTransform(null, damagedNames));
        }

        private sealed class FakeNode
        {
            public string Name;
            public FakeNode Parent;
        }

        private static bool Walk(FakeNode start, HashSet<string> names)
        {
            return GhostVisualBuilder.IsNodeOrAncestorNamed(
                start, names, n => n.Name, n => n.Parent);
        }

        // root "wheelroot" <- "bustedwheel" <- "mesh" (the renderer's own node)
        private static FakeNode MakeChain(out FakeNode root, out FakeNode damaged)
        {
            root = new FakeNode { Name = "wheelroot" };
            damaged = new FakeNode { Name = "bustedwheel", Parent = root };
            return new FakeNode { Name = "mesh", Parent = damaged };
        }

        [Fact]
        public void IsNodeOrAncestorNamed_MatchOnParent_ReturnsTrue()
        {
            FakeNode leaf = MakeChain(out _, out _);
            Assert.True(Walk(leaf, new HashSet<string> { "bustedwheel" }));
        }

        [Fact]
        public void IsNodeOrAncestorNamed_MatchOnSelfAndOnRoot_ReturnsTrue()
        {
            FakeNode leaf = MakeChain(out _, out _);
            Assert.True(Walk(leaf, new HashSet<string> { "mesh" }));
            Assert.True(Walk(leaf, new HashSet<string> { "wheelroot" }));
        }

        [Fact]
        public void IsNodeOrAncestorNamed_UnrelatedNames_ReturnsFalse()
        {
            FakeNode leaf = MakeChain(out _, out _);
            Assert.False(Walk(leaf, new HashSet<string> { "otherwheel", "Mesh" }));
        }

        [Fact]
        public void IsNodeOrAncestorNamed_MatchBelowStart_IsNotSeen()
        {
            // The walk goes up only: a damaged name on a child of the start is no match.
            MakeChain(out _, out FakeNode damaged);
            Assert.False(Walk(damaged.Parent, new HashSet<string> { "bustedwheel", "mesh" }));
        }

        [Fact]
        public void IsNodeOrAncestorNamed_NullOrEmptyNames_ReturnsFalseWithLiveNode()
        {
            FakeNode leaf = MakeChain(out _, out _);
            Assert.False(Walk(leaf, null));
            Assert.False(Walk(leaf, new HashSet<string>()));
            Assert.False(Walk(null, new HashSet<string> { "mesh" }));
        }

        #endregion
    }
}
