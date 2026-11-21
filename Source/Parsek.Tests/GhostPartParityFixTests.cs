using System;
using System.Collections.Generic;
using UnityEngine;
using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// Pure decisions behind the part ghost parity sweep fixes
    /// (docs/dev/design-part-ghost-parity.md): each test pins what stock KSP shows on the
    /// live part so the ghost matches it.
    /// </summary>
    [Collection("Sequential")]
    public class GhostPartParityFixTests : IDisposable
    {
        public GhostPartParityFixTests()
        {
            ParsekLog.SuppressLogging = true;
        }

        public void Dispose()
        {
            ParsekLog.ResetTestOverrides();
        }

        #region Heat shield ablation char

        private static ConfigNode ShieldCharColorChanger()
        {
            var moduleNode = new ConfigNode("MODULE");
            moduleNode.AddValue("name", "ModuleColorChanger");
            moduleNode.AddValue("moduleID", "shieldChar");
            moduleNode.AddValue("shaderProperty", "_BurnColor");
            moduleNode.AddValue("toggleInFlight", "False");
            foreach (string curve in new[] { "redCurve", "greenCurve", "blueCurve" })
            {
                var c = new ConfigNode(curve);
                c.AddValue("key", "0 0");
                c.AddValue("key", "1 1");
                moduleNode.AddNode(c);
            }
            var alpha = new ConfigNode("alphaCurve");
            alpha.AddValue("key", "0 0.8");
            moduleNode.AddNode(alpha);
            return moduleNode;
        }

        private static ConfigNode HeatShieldPrefabConfig(string ablativeResource = "Ablator",
            string charModuleName = "shieldChar", string useChar = "True")
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "HeatShield1");
            var ablator = part.AddNode("MODULE");
            ablator.AddValue("name", "ModuleAblator");
            if (ablativeResource != null) ablator.AddValue("ablativeResource", ablativeResource);
            if (useChar != null) ablator.AddValue("useChar", useChar);
            if (charModuleName != null) ablator.AddValue("charModuleName", charModuleName);
            part.AddNode(ShieldCharColorChanger());
            return part;
        }

        private static ConfigNode HeatShieldSnapshot(double amount, double maxAmount, string recip)
        {
            var part = new ConfigNode("PART");
            part.AddValue("name", "HeatShield1");
            var ablator = part.AddNode("MODULE");
            ablator.AddValue("name", "ModuleAblator");
            if (recip != null) ablator.AddValue("nominalAmountRecip", recip);
            var res = part.AddNode("RESOURCE");
            res.AddValue("name", "Ablator");
            res.AddValue("amount", amount.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            res.AddValue("maxAmount", maxAmount.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            return part;
        }

        [Fact]
        public void AblationChar_UnburntShield_IsLightGreyNotBlack()
        {
            bool driven = GhostVisualBuilder.TryResolveAblationCharScalars(
                HeatShieldSnapshot(200, 200, "0.005"), HeatShieldPrefabConfig(), "shieldChar",
                out float current, out float charred);

            Assert.True(driven);
            Assert.InRange(current, 0.849f, 0.851f);
            Assert.InRange(charred, 0.099f, 0.101f);

            // The initial ghost colour: stock's unburnt shield, not the charred t=0 black.
            Color initial = GhostVisualBuilder.EvaluateColorCurves(ShieldCharColorChanger(), current);
            Assert.InRange(initial.r, 0.849f, 0.851f);
            Assert.InRange(initial.g, 0.849f, 0.851f);
            Assert.InRange(initial.b, 0.849f, 0.851f);
            Assert.InRange(initial.a, 0.79f, 0.81f);
        }

        [Fact]
        public void AblationChar_HalfSpentShield_UsesPersistedNominalRecip()
        {
            // Half the nominal ablator left: charMin + 0.75 * 0.5.
            bool driven = GhostVisualBuilder.TryResolveAblationCharScalars(
                HeatShieldSnapshot(100, 200, "0.005"), HeatShieldPrefabConfig(), "shieldChar",
                out float current, out _);
            Assert.True(driven);
            Assert.InRange(current, 0.474f, 0.476f);
        }

        [Fact]
        public void AblationChar_NoPersistedRecip_FallsBackToMaxAmount()
        {
            bool driven = GhostVisualBuilder.TryResolveAblationCharScalars(
                HeatShieldSnapshot(0, 200, null), HeatShieldPrefabConfig(), "shieldChar",
                out float current, out _);
            Assert.True(driven);
            Assert.InRange(current, 0.099f, 0.101f);
        }

        [Fact]
        public void AblationChar_NoResourceNode_ReadsAsUnburnt()
        {
            var snapshot = new ConfigNode("PART");
            bool driven = GhostVisualBuilder.TryResolveAblationCharScalars(
                snapshot, HeatShieldPrefabConfig(), "shieldChar", out float current, out _);
            Assert.True(driven);
            Assert.InRange(current, 0.849f, 0.851f);
        }

        [Theory]
        [InlineData(null, "shieldChar", "True", "shieldChar")] // no ablativeResource: stock never drives
        [InlineData("Ablator", "shieldChar", "False", "shieldChar")] // useChar off
        [InlineData("Ablator", "otherChar", "True", "shieldChar")] // ablator drives another module
        [InlineData("Ablator", "shieldChar", "True", null)] // colour changer keeps default moduleID
        public void AblationChar_NotDrivenByAblator_ReturnsFalse(
            string ablativeResource, string charModuleName, string useChar, string moduleId)
        {
            bool driven = GhostVisualBuilder.TryResolveAblationCharScalars(
                HeatShieldSnapshot(200, 200, "0.005"),
                HeatShieldPrefabConfig(ablativeResource, charModuleName, useChar),
                moduleId, out _, out _);
            Assert.False(driven);
        }

        #endregion

        #region Variant rules skip flag materials

        [Theory]
        [InlineData("Flag", true)]
        [InlineData("roverBody_flag (Instance)", true)]
        [InlineData("FLAGDECAL", true)]
        [InlineData("Auto Truss", true)]
        [InlineData("model_auto truss_mat", true)]
        [InlineData("roverBody", false)]
        [InlineData("AutoTruss", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void VariantRules_SkipFlagAndAutoTrussMaterials(string name, bool exempt)
        {
            Assert.Equal(exempt, GhostVisualBuilder.IsVariantExemptMaterial(name));
        }

        #endregion
    }
}
