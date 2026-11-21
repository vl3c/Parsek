using System;
using System.Collections.Generic;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Tests for bug #242c: ghost variant geometry not toggled for FX transforms.
    ///
    /// Parts with ModulePartVariants that toggle geometry via GAMEOBJECTS (e.g.,
    /// Poodle DoubleBell/SingleBell) showed FX from all variant geometries on
    /// the ghost. The fix filters FX transforms against the selected variant's
    /// GAMEOBJECTS rules using IsAncestorChainEnabledByVariantRule.
    ///
    /// These tests cover the pure ancestor-matching logic extracted from
    /// IsRendererEnabledByVariantRule — no Unity dependency.
    /// </summary>
    public class VariantFxFilterTests
    {
        [Fact]
        public void AncestorUnderEnabledVariant_ReturnsTrue()
        {
            // thrustTransform is under EngineFixed, which is enabled
            var ancestors = new List<string> { "thrustTransform", "EngineFixed" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", true }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Equal("EngineFixed", matched);
            Assert.True(enabled);
        }

        [Fact]
        public void AncestorUnderDisabledVariant_ReturnsFalse()
        {
            // thrustTransform is under EngineFixed, which is disabled (SingleBell variant)
            var ancestors = new List<string> { "thrustTransform", "EngineFixed" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("EngineFixed", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void NoMatchingRule_ReturnsTrue()
        {
            // thrustTransform is under UnrelatedParent — no rule matches
            var ancestors = new List<string> { "thrustTransform", "UnrelatedParent" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", true }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Null(matched);
            Assert.True(enabled);
        }

        [Fact]
        public void NullGameObjectStates_ReturnsTrue()
        {
            var ancestors = new List<string> { "thrustTransform", "EngineFixed" };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, null, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Null(matched);
            Assert.True(enabled);
        }

        [Fact]
        public void EmptyGameObjectStates_ReturnsTrue()
        {
            var ancestors = new List<string> { "thrustTransform", "EngineFixed" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Null(matched);
            Assert.True(enabled);
        }

        [Fact]
        public void FirstDeepestMatchWins_DisabledChildUnderEnabledParent()
        {
            // Walk is bottom-up: DisabledChild is checked before EnabledParent
            var ancestors = new List<string> { "thrustTransform", "DisabledChild", "EnabledParent" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EnabledParent", true },
                { "DisabledChild", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("DisabledChild", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void DisabledParentHidesEnabledChild()
        {
            // Stock applies rules as SetActive: an inactive parent hides an active child,
            // so a deeper enabling rule must not override a disabling ancestor.
            var ancestors = new List<string> { "thrustTransform", "EnabledChild", "DisabledParent" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EnabledChild", true },
                { "DisabledParent", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("DisabledParent", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void AllEnabledChain_ReportsDeepestEnablingRule()
        {
            var ancestors = new List<string> { "mesh", "EnabledChild", "EnabledParent", "model" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EnabledChild", true },
                { "EnabledParent", true }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Equal("EnabledChild", matched);
            Assert.True(enabled);
        }

        [Fact]
        public void DisabledMultiModelRootHidesEnabledChild()
        {
            // A disabling rule on a multi-MODEL root (matched by short name) still wins
            // over an enabling rule deeper in the chain.
            var ancestors = new List<string>
            {
                "ShroudMesh", "ShroudPiece", "SquadExpansion/Parts/Shroud3x0(Clone)", "model"
            };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "ShroudPiece", true },
                { "Shroud3x0", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("Shroud3x0", matched);
            Assert.False(enabled);
        }

        // ReStock LV-T45 (liquidEngine2) Size1 variant, verbatim from
        // ReStock/PatchesLegacy/Engines/restock-engines-liquid-125.cfg. It enables
        // Frame_Boat and ShroudT45_CompactWhite whose parents it disables; the live part
        // hides both, and the ghost drew them as booster skirts before this rule.
        private static Dictionary<string, bool> ReStockLvT45Size1Rules()
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "LV_T45_Boattail", false }, { "LV_T45_Basic", true }, { "LV_T45_Compact", false },
                { "T45_Piston001", true }, { "T45_Piston002", true },
                { "T45_Piston003", false }, { "T45_Piston004", false },
                { "Engine", true }, { "EngineBoattail", false },
                { "fairing", true }, { "ShroudT45_Compact", false },
                { "fairingWhite", true }, { "ShroudT45_CompactWhite", true },
                { "Frame_Boat", true }, { "fairingOrange", false },
                { "ShroudT45_CompactOrange", false }, { "Frame_BoatOrange", false }
            };
        }

        private const string ReStockSwivelRoot = "ReStock/Assets/Engine/restock-engine-swivel-1(Clone)";

        [Theory]
        [InlineData(false, "Frame_Boat", "LV_T45_Boattail")]
        [InlineData(false, "ShroudT45_CompactWhite", "ShroudT45_Compact", "LV_T45_Compact")]
        [InlineData(false, "Frame_BoatOrange", "LV_T45_Boattail")]
        [InlineData(false, "T45_Piston003", "Gimbal", "LV_T45_Common")]
        [InlineData(true, "fairingWhite", "fairing", "LV_T45_Basic")]
        [InlineData(true, "Frame", "LV_T45_Basic")]
        [InlineData(true, "Engine", "Gimbal", "LV_T45_Common")]
        [InlineData(true, "EngineCoreT45Glow", "Gimbal", "LV_T45_Common")]
        public void ReStockLvT45Size1_MatchesLiveVisibility(bool expected, params string[] chain)
        {
            var ancestors = new List<string>(chain) { "liquidEngine2", ReStockSwivelRoot, "model" };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, ReStockLvT45Size1Rules(), out _, out _);

            Assert.Equal(expected, result);
        }

        [Fact]
        public void TransformItselfMatchesRule()
        {
            // The transform name itself (index 0) matches a variant rule
            var ancestors = new List<string> { "EngineFixed", "model" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("EngineFixed", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void CaseInsensitiveMatching()
        {
            // Rule uses "EngineFixed", ancestor has "engineFixed" (different case)
            // matchedRuleName returns the ancestor name as-found, not the dictionary key
            var ancestors = new List<string> { "thrustTransform", "engineFixed" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", true }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Equal("engineFixed", matched);
            Assert.True(enabled);
        }

        [Fact]
        public void ExtractShortTransformName_MatchesCloneSuffix()
        {
            // Multi-MODEL parts: KSP names with full path + (Clone)
            var ancestors = new List<string>
            {
                "thrustTransform",
                "Squad/Parts/Engine/Shroud3x0(Clone)"
            };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "Shroud3x0", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("Shroud3x0", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void PoodleDoubleBell_EnabledTransformsPass()
        {
            // Poodle DoubleBell variant: EngineFixed=true, Shroud=false
            // thrustTransform under EngineFixed should pass
            var ancestors = new List<string> { "thrustTransform", "EngineFixed", "model" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", true },
                { "Shroud", false },
                { "Shroud1", true },
                { "Shroud2", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Equal("EngineFixed", matched);
            Assert.True(enabled);
        }

        [Fact]
        public void PoodleSingleBell_DisabledEngineFixedBlocked()
        {
            // Poodle SingleBell variant: EngineFixed=false
            // thrustTransform under EngineFixed should be blocked
            var ancestors = new List<string> { "thrustTransform", "EngineFixed", "model" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", false },
                { "Shroud", true },
                { "Shroud1", false },
                { "Shroud2", true }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.False(result);
            Assert.Equal("EngineFixed", matched);
            Assert.False(enabled);
        }

        [Fact]
        public void NullAncestorList_ReturnsTrue()
        {
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                null, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Null(matched);
            Assert.True(enabled);
        }

        [Fact]
        public void EmptyAncestorList_ReturnsTrue()
        {
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EngineFixed", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                new List<string>(), rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Null(matched);
            Assert.True(enabled);
        }
    }
}
