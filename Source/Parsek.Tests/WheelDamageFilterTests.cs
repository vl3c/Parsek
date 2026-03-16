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

        [Fact]
        public void IsRendererOnDamagedTransform_NullTransform_ReturnsFalse()
        {
            var names = new HashSet<string> { "bustedwheel" };
            Assert.False(GhostVisualBuilder.IsRendererOnDamagedTransform(null, names));
        }

        [Fact]
        public void IsRendererOnDamagedTransform_NullNames_ReturnsFalse()
        {
            // Cannot create real Transform in tests (requires Unity runtime),
            // but null names should short-circuit before accessing transform
            Assert.False(GhostVisualBuilder.IsRendererOnDamagedTransform(null, null));
        }

        [Fact]
        public void IsRendererOnDamagedTransform_EmptyNames_ReturnsFalse()
        {
            var names = new HashSet<string>();
            Assert.False(GhostVisualBuilder.IsRendererOnDamagedTransform(null, names));
        }

        #endregion
    }
}
