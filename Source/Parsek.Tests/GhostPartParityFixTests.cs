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

        #region Flag part meshes

        private static ConfigNode FlagMesh(string name, string meshName, string orientation,
            string variantNames)
        {
            var mesh = new ConfigNode("MESH");
            mesh.AddValue("name", name);
            mesh.AddValue("displayName", "#loc_" + name);
            mesh.AddValue("meshName", meshName);
            if (orientation != null) mesh.AddValue("orientation", orientation);
            mesh.AddValue("variantNames", variantNames);
            return mesh;
        }

        // The flagSize1 shape: two sizes, each with portrait / landscape and plain / panel meshes.
        private static ConfigNode FlagSize1Config()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "FlagDecalBackground");
            const string plain = "White,Dark,Orange,TransparentBackground";
            const string panel = "WhitePanel,DarkPanel,OrangePanel";
            module.AddNode(FlagMesh("small", "SmallPortrait", "PORTRAIT", plain));
            module.AddNode(FlagMesh("small", "SmallLandscape", "LANDSCAPE", plain));
            module.AddNode(FlagMesh("small", "SmallPortraitPanel", "PORTRAIT", panel));
            module.AddNode(FlagMesh("small", "SmallLandscapePanel", "LANDSCAPE", panel));
            module.AddNode(FlagMesh("medium", "MediumPortrait", "PORTRAIT", plain));
            module.AddNode(FlagMesh("medium", "MediumLandscape", "LANDSCAPE", plain));
            module.AddNode(FlagMesh("medium", "MediumPortraitPanel", "PORTRAIT", panel));
            module.AddNode(FlagMesh("medium", "MediumLandscapePanel", "LANDSCAPE", panel));
            return module;
        }

        private static ConfigNode FlagSnapshot(string flagSize, string displayingPortrait)
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "FlagDecalBackground");
            if (flagSize != null) module.AddValue("flagSize", flagSize);
            if (displayingPortrait != null) module.AddValue("displayingPortrait", displayingPortrait);
            return module;
        }

        private static void AssertOnlyShown(HashSet<string> hidden, string shown)
        {
            string[] all =
            {
                "SmallPortrait", "SmallLandscape", "SmallPortraitPanel", "SmallLandscapePanel",
                "MediumPortrait", "MediumLandscape", "MediumPortraitPanel", "MediumLandscapePanel",
            };
            foreach (string name in all)
                Assert.Equal(name != shown, hidden.Contains(name));
        }

        [Fact]
        public void FlagMeshes_DefaultSnapshot_ShowsSmallPortraitOfTheVariant()
        {
            AssertOnlyShown(GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(
                FlagSize1Config(), FlagSnapshot(null, null), "White"), "SmallPortrait");
        }

        [Fact]
        public void FlagMeshes_SizeOneLandscape_IndexCountsDistinctNames()
        {
            AssertOnlyShown(GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(
                FlagSize1Config(), FlagSnapshot("1", "False"), "Dark"), "MediumLandscape");
        }

        [Fact]
        public void FlagMeshes_PanelVariant_ShowsThePanelMesh()
        {
            AssertOnlyShown(GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(
                FlagSize1Config(), FlagSnapshot("0", "True"), "OrangePanel"), "SmallPortraitPanel");
        }

        [Fact]
        public void FlagMeshes_NoPortraitMesh_DefaultsToLandscape()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "FlagDecalBackground");
            module.AddNode(FlagMesh("flat", "FlatA", null, "White"));
            module.AddNode(FlagMesh("flat2", "FlatB", "LANDSCAPE", "White"));
            HashSet<string> hidden = GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(
                module, FlagSnapshot(null, null), "White");
            Assert.DoesNotContain("FlatA", hidden);
            Assert.Contains("FlatB", hidden);
        }

        [Fact]
        public void FlagMeshes_IncompleteMeshNode_IsNotIndexed()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "FlagDecalBackground");
            var broken = new ConfigNode("MESH");
            broken.AddValue("name", "first");
            broken.AddValue("meshName", "Broken");
            module.AddNode(broken);
            module.AddNode(FlagMesh("second", "Second", "PORTRAIT", "White"));
            HashSet<string> hidden = GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(
                module, FlagSnapshot("0", "True"), "White");
            // The node without a displayName is skipped, so "second" takes index 0.
            Assert.DoesNotContain("Second", hidden);
            Assert.DoesNotContain("Broken", hidden);
        }

        [Fact]
        public void FlagMeshes_NoConfig_HidesNothing()
        {
            Assert.Empty(GhostVisualBuilder.ResolveFlagDecalHiddenMeshNames(null, null, "White"));
        }

        #endregion

        #region Hinge piston arm

        [Fact]
        public void HingePistons_StockDefault_NamesTheFourArmTransforms()
        {
            HashSet<string> names = GhostVisualBuilder.ParseHingePistonTransformNames(
                GhostVisualBuilder.DefaultHingePistonTransforms);
            Assert.Equal(4, names.Count);
            Assert.Contains("Bar1", names);
            Assert.Contains("Bar2", names);
            Assert.Contains("TopPlateKnuckle", names);
            Assert.Contains("BasePlateKnuckle", names);
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("Bar1,,Bar2,", 2)]
        public void HingePistons_SplitsLikeTogglePistons(string raw, int expected)
        {
            Assert.Equal(expected, GhostVisualBuilder.ParseHingePistonTransformNames(raw).Count);
        }

        #endregion

        #region Standalone ModuleAnimateGeneric endpoints

        [Fact]
        public void AnimateGeneric_StowedAndDeployedPoses_AreTheEndsTheRecorderClassifies()
        {
            // The ghost's stowed pose must be the clip time the recorder and the snapshot
            // baseline read as retracted, and its deployed pose the one they read as deployed.
            FlightRecorder.ClassifyLadderState(GhostVisualBuilder.AnimateGenericStowedNormalizedTime,
                out bool stowedExtended, out bool stowedRetracted);
            Assert.True(stowedRetracted);
            Assert.False(stowedExtended);

            FlightRecorder.ClassifyLadderState(GhostVisualBuilder.AnimateGenericDeployedNormalizedTime,
                out bool deployedExtended, out bool deployedRetracted);
            Assert.True(deployedExtended);
            Assert.False(deployedRetracted);
        }

        #endregion

        #region Structural nodes outside fairings

        private static ConfigNode PartWithModules(params string[] moduleNames)
        {
            var part = new ConfigNode("PART");
            foreach (string name in moduleNames)
                part.AddNode("MODULE").AddValue("name", name);
            return part;
        }

        [Fact]
        public void StructureNodes_NonFairingPartWithStructuralNode_GetsTheRule()
        {
            Assert.True(GhostVisualBuilder.ShouldBuildNonFairingStructureNodes(
                PartWithModules("ModuleCommand"), PartWithModules("ModuleCommand", "ModuleStructuralNode")));
        }

        [Fact]
        public void StructureNodes_FairingPart_LeftToTheFairingBuilder()
        {
            Assert.False(GhostVisualBuilder.ShouldBuildNonFairingStructureNodes(
                PartWithModules("ModuleProceduralFairing"),
                PartWithModules("ModuleProceduralFairing", "ModuleStructuralNode")));
        }

        [Fact]
        public void StructureNodes_NoStructuralNode_NothingToBuild()
        {
            Assert.False(GhostVisualBuilder.ShouldBuildNonFairingStructureNodes(
                PartWithModules("ModuleCommand"), PartWithModules("ModuleCommand")));
        }

        [Fact]
        public void StructureNodes_MemLanderAdapter_HiddenWhenBottom2IsEmpty()
        {
            // Size1_5_Lander: rootObject = Adapter, attachNodeNames = bottom2.
            var config = PartWithModules("ModuleCommand");
            var structural = config.AddNode("MODULE");
            structural.AddValue("name", "ModuleStructuralNode");
            structural.AddValue("rootObject", "Adapter");
            structural.AddValue("attachNodeNames", "bottom2");

            var empty = PartWithModules("ModuleCommand", "ModuleStructuralNode");
            empty.AddValue("attN", "bottom2, -1");
            var nodes = GhostVisualBuilder.ParseFairingStructureNodes(config, empty);
            Assert.Single(nodes);
            Assert.Equal("Adapter", nodes[0].rootObject);
            Assert.False(GhostVisualBuilder.ComputeStructureNodeVisible(nodes[0]));

            var attached = PartWithModules("ModuleCommand", "ModuleStructuralNode");
            attached.AddValue("attN", "bottom2, 1");
            Assert.True(GhostVisualBuilder.ComputeStructureNodeVisible(
                GhostVisualBuilder.ParseFairingStructureNodes(config, attached)[0]));
        }

        #endregion

        #region Light lens and flare

        [Fact]
        public void LightLens_OffIsBlack_OnIsTheLightColour()
        {
            var onColor = new Color(0.9f, 0.5f, 0.25f);
            Color off = GhostPlaybackLogic.ResolveLightLensEmissive(false, onColor);
            Assert.Equal(0f, off.r);
            Assert.Equal(0f, off.g);
            Assert.Equal(0f, off.b);
            Color on = GhostPlaybackLogic.ResolveLightLensEmissive(true, onColor);
            Assert.Equal(0.9, (double)on.r, 4);
            Assert.Equal(0.5, (double)on.g, 4);
            Assert.Equal(0.25, (double)on.b, 4);
        }

        [Fact]
        public void LightFlare_OffIsBlack_OnIsWhite()
        {
            Assert.Equal(0f, GhostPlaybackLogic.ResolveLightFlareTint(false).r);
            Assert.Equal(1f, GhostPlaybackLogic.ResolveLightFlareTint(true).r);
        }

        [Fact]
        public void LightOnColor_ReadsSnapshotFields_FallingBackToThePrefab()
        {
            var module = new ConfigNode("MODULE");
            module.AddValue("name", "ModuleLight");
            module.AddValue("lightR", "0.25");
            module.AddValue("lightB", "not-a-number");
            Color c = GhostVisualBuilder.ResolveLightOnColor(module, 0.875f, 0.6f, 0.7f);
            Assert.Equal(0.25, (double)c.r, 4);
            Assert.Equal(0.6, (double)c.g, 4);
            Assert.Equal(0.7, (double)c.b, 4);

            Color fromPrefab = GhostVisualBuilder.ResolveLightOnColor(null, 0.875f, 0.875f, 0.875f);
            Assert.Equal(0.875, (double)fromPrefab.r, 4);
        }

        #endregion
    }
}
