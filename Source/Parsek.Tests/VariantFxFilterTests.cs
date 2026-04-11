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
        public void FirstDeepestMatchWins_EnabledChildUnderDisabledParent()
        {
            // Inverse of above: EnabledChild is closer (checked first), should win
            var ancestors = new List<string> { "thrustTransform", "EnabledChild", "DisabledParent" };
            var rules = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                { "EnabledChild", true },
                { "DisabledParent", false }
            };

            bool result = GhostVisualBuilder.IsAncestorChainEnabledByVariantRule(
                ancestors, rules, out string matched, out bool enabled);

            Assert.True(result);
            Assert.Equal("EnabledChild", matched);
            Assert.True(enabled);
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
